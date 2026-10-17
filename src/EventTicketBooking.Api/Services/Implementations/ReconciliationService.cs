using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class ReconciliationService : IReconciliationService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<ReconciliationService> _logger;

        public ReconciliationService(AppDbContext context, ILogger<ReconciliationService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<Dictionary<string, InternalTransactionRecord>> GetInternalTransactionsAsync(
            DateTimeOffset fromDate,
            DateTimeOffset toDate,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, InternalTransactionRecord>(StringComparer.OrdinalIgnoreCase);

            // 1. Nguồn chính theo Story: payment_events
            var allEvents = await _context.PaymentEvents
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var events = allEvents
                .Where(pe => pe.CreatedAt >= fromDate && pe.CreatedAt <= toDate)
                .OrderBy(pe => pe.CreatedAt)
                .ToList();

            foreach (var pe in events)
            {
                var txId = pe.TransactionId?.Trim();
                if (string.IsNullOrEmpty(txId))
                    continue;

                int? rawAmount = TryExtractAmountFromPayload(pe.RawPayload);
                long? rawOrderCode = TryExtractOrderCodeFromPayload(pe.RawPayload);

                var record = new InternalTransactionRecord
                {
                    TransactionId = txId,
                    OrderCode = rawOrderCode,
                    Amount = rawAmount,
                    EventDate = pe.CreatedAt,
                    Source = "PaymentEvent",
                    HasAmountWarning = !rawAmount.HasValue,
                    WarningMessage = !rawAmount.HasValue ? "Chưa xác định được số tiền từ sự kiện webhook" : null
                };

                result[txId] = record;
            }

            // 2. Nguồn bổ sung cho Mock Payment hoặc bù đắp thông tin: payment_transactions
            var allTransactions = await _context.PaymentTransactions
                .AsNoTracking()
                .Where(pt => !string.IsNullOrEmpty(pt.TransactionId))
                .ToListAsync(cancellationToken);

            var transactions = allTransactions
                .Where(pt => (pt.CreatedAt >= fromDate && pt.CreatedAt <= toDate) ||
                             (pt.UpdatedAt >= fromDate && pt.UpdatedAt <= toDate))
                .OrderBy(pt => pt.UpdatedAt)
                .ToList();

            foreach (var pt in transactions)
            {
                var txId = pt.TransactionId?.Trim();
                if (string.IsNullOrEmpty(txId))
                    continue;

                if (result.TryGetValue(txId, out var existing))
                {
                    // Hợp nhất: ưu tiên số tiền chuẩn từ PaymentTransaction
                    existing.OrderCode = pt.OrderCode;
                    existing.OrderId = pt.OrderId;
                    existing.OrderStatus = pt.Status;
                    existing.Source = "Both";
                    if (pt.Amount > 0)
                    {
                        existing.Amount = pt.Amount;
                        existing.HasAmountWarning = false;
                        existing.WarningMessage = null;
                    }
                }
                else
                {
                    // Bản ghi chỉ có trong payment_transactions (ví dụ giao dịch Mock)
                    result[txId] = new InternalTransactionRecord
                    {
                        TransactionId = txId,
                        Amount = pt.Amount,
                        OrderCode = pt.OrderCode,
                        OrderId = pt.OrderId,
                        OrderStatus = pt.Status,
                        EventDate = pt.UpdatedAt,
                        Source = "PaymentTransaction",
                        HasAmountWarning = pt.Amount <= 0,
                        WarningMessage = pt.Amount <= 0 ? "Số tiền giao dịch không hợp lệ trong hệ thống" : null
                    };
                }
            }

            // Đánh giá tính đủ điều kiện đối soát (Requirement 6)
            foreach (var rec in result.Values)
            {
                if (!string.IsNullOrEmpty(rec.OrderStatus) &&
                    (string.Equals(rec.OrderStatus, "PENDING", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rec.OrderStatus, "FAILED", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rec.OrderStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase)))
                {
                    rec.IsEligible = false;
                    rec.IneligibilityReason = $"Giao dịch ở trạng thái '{rec.OrderStatus}', chưa hoàn tất thanh toán để đối soát.";
                }
                else if (string.IsNullOrWhiteSpace(rec.TransactionId) && (!rec.OrderCode.HasValue || rec.OrderCode.Value <= 0))
                {
                    rec.IsEligible = false;
                    rec.IneligibilityReason = "Giao dịch thiếu mã định danh (TransactionId hoặc OrderCode).";
                }
                else if (rec.Amount.HasValue && rec.Amount.Value < 0)
                {
                    rec.IsEligible = false;
                    rec.IneligibilityReason = "Giao dịch có số tiền âm không hợp lệ.";
                }
                else
                {
                    rec.IsEligible = true;
                    rec.IneligibilityReason = null;
                }
            }

            return result;
        }

        public (List<GatewayStatementItemDto> items, List<string> errors) ParseCsvStatement(string csvContent)
        {
            var items = new List<GatewayStatementItemDto>();
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(csvContent))
            {
                errors.Add("Tệp sao kê rỗng.");
                return (items, errors);
            }

            using var reader = new StringReader(csvContent);
            string? line;
            int lineNumber = 0;
            bool isHeaderParsed = false;

            int colTxId = 0;
            int colAmount = 1;
            int colDate = 2;
            int colDesc = 3;
            int? colOrderCode = null;

            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                char separator = trimmed.Contains('\t') ? '\t' : (trimmed.Contains(';') && !trimmed.Contains(',') ? ';' : ',');
                var columns = ParseCsvLine(trimmed, separator);

                if (columns.Count < 2)
                {
                    errors.Add($"Dòng {lineNumber}: Không đủ số cột tối thiểu (Cần ít nhất Mã giao dịch và Số tiền).");
                    continue;
                }

                // Phát hiện dòng header
                if (!isHeaderParsed)
                {
                    string firstCol = columns[0].ToLowerInvariant();
                    if (firstCol.Contains("trans") || firstCol.Contains("mã") || firstCol.Contains("id") || firstCol.Contains("code") || firstCol.Contains("order"))
                    {
                        for (int i = 0; i < columns.Count; i++)
                        {
                            var headerName = columns[i].ToLowerInvariant();
                            if (headerName.Contains("date") || headerName.Contains("time") || headerName.Contains("ngày") || headerName.Contains("giờ"))
                                colDate = i;
                            else if (headerName.Contains("amount") || headerName.Contains("tiền") || headerName.Contains("giá") || headerName.Contains("price"))
                                colAmount = i;
                            else if (headerName.Contains("desc") || headerName.Contains("nội dung") || headerName.Contains("ghi chú") || headerName.Contains("note"))
                                colDesc = i;
                            else if (headerName.Contains("order") || headerName.Contains("đơn hàng") || headerName.Contains("mã đơn"))
                                colOrderCode = i;
                            else if (headerName.Contains("trans") || headerName.Contains("mã") || headerName.Contains("id") || headerName.Contains("ref"))
                                colTxId = i;
                        }
                        isHeaderParsed = true;
                        continue;
                    }
                    isHeaderParsed = true;
                }

                if (colTxId >= columns.Count || colAmount >= columns.Count)
                {
                    errors.Add($"Dòng {lineNumber}: Vị trí cột không hợp lệ.");
                    continue;
                }

                string txId = columns[colTxId].Trim();
                if (string.IsNullOrEmpty(txId))
                {
                    errors.Add($"Dòng {lineNumber}: Mã giao dịch (TransactionId) bị trống.");
                    continue;
                }

                // Parse số tiền (hỗ trợ định dạng số có dấu phẩy hoặc chấm)
                string rawAmountStr = columns[colAmount]
                    .Replace(" ", string.Empty)
                    .Replace("VND", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("Đ", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("đ", string.Empty);

                // Nếu có dấu phân cách hàng nghìn dạng 100.000 hoặc 100,000
                if (rawAmountStr.Contains('.') && !rawAmountStr.Contains(','))
                {
                    // 100.000 -> 100000
                    rawAmountStr = rawAmountStr.Replace(".", string.Empty);
                }
                else if (rawAmountStr.Contains(',') && !rawAmountStr.Contains('.'))
                {
                    rawAmountStr = rawAmountStr.Replace(",", string.Empty);
                }

                if (!int.TryParse(rawAmountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out int amount))
                {
                    errors.Add($"Dòng {lineNumber}: Số tiền '{columns[colAmount]}' không hợp lệ.");
                    continue;
                }

                DateTimeOffset? txDate = null;
                if (colDate < columns.Count && !string.IsNullOrWhiteSpace(columns[colDate]))
                {
                    if (DateTimeOffset.TryParse(columns[colDate], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    {
                        txDate = parsedDate;
                    }
                }

                string? desc = colDesc < columns.Count ? columns[colDesc] : null;

                long? orderCode = null;
                if (colOrderCode.HasValue && colOrderCode.Value < columns.Count && !string.IsNullOrWhiteSpace(columns[colOrderCode.Value]))
                {
                    string rawOrderCode = columns[colOrderCode.Value].Replace("#", string.Empty).Trim();
                    if (long.TryParse(rawOrderCode, out long parsedOrderCode))
                    {
                        orderCode = parsedOrderCode;
                    }
                }

                items.Add(new GatewayStatementItemDto
                {
                    TransactionId = txId,
                    OrderCode = orderCode,
                    Amount = amount,
                    TransactionDate = txDate,
                    Description = desc
                });
            }

            return (items, errors);
        }

        private static List<string> ParseCsvLine(string line, char separator)
        {
            var columns = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // Bỏ qua ký tự thoát nháy kép ("")
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == separator && !inQuotes)
                {
                    columns.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            columns.Add(sb.ToString().Trim());
            return columns;
        }

        private static long? GetReferencedOrderCode(GatewayStatementItemDto gw)
        {
            if (gw.OrderCode.HasValue && gw.OrderCode.Value > 0)
                return gw.OrderCode.Value;

            if (long.TryParse(gw.TransactionId, out long parsed) && parsed > 0)
                return parsed;

            return null;
        }

        public async Task<ReconciliationSummaryDto> ReconcileAsync(
            DateTimeOffset fromDate,
            DateTimeOffset toDate,
            List<GatewayStatementItemDto> gatewayItems,
            List<string>? parseWarnings = null,
            CancellationToken cancellationToken = default)
        {
            var internalMap = await GetInternalTransactionsAsync(fromDate, toDate, cancellationToken);

            // Tách các giao dịch đủ điều kiện và chưa đủ điều kiện (Requirement 6)
            var eligibleInternalMap = internalMap
                .Where(kvp => kvp.Value.IsEligible)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

            var ineligibleInternalList = internalMap.Values
                .Where(v => !v.IsEligible)
                .ToList();

            // Xây dựng nhóm giao dịch nội bộ theo OrderCode (chỉ cho giao dịch đủ điều kiện)
            var internalGroupsByOrderCode = new Dictionary<long, List<InternalTransactionRecord>>();
            foreach (var kvp in eligibleInternalMap)
            {
                if (kvp.Value.OrderCode.HasValue)
                {
                    long code = kvp.Value.OrderCode.Value;
                    if (!internalGroupsByOrderCode.TryGetValue(code, out var list))
                    {
                        list = new List<InternalTransactionRecord>();
                        internalGroupsByOrderCode[code] = list;
                    }
                    list.Add(kvp.Value);
                }
            }

            // Chuẩn hóa danh sách sao kê cổng theo TransactionId
            var gatewayMap = new Dictionary<string, GatewayStatementItemDto>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>(parseWarnings ?? Enumerable.Empty<string>());

            foreach (var gw in gatewayItems)
            {
                var txId = gw.TransactionId.Trim();
                if (gatewayMap.ContainsKey(txId))
                {
                    warnings.Add($"Sao kê cổng có mã giao dịch trùng lặp: '{txId}'. Giữ bản ghi đầu tiên.");
                    continue;
                }
                gatewayMap[txId] = gw;
            }

            var summary = new ReconciliationSummaryDto
            {
                FromDate = fromDate,
                ToDate = toDate,
                ReconciledAt = DateTimeOffset.UtcNow,
                TotalInternalCount = internalMap.Count,
                TotalInternalAmount = eligibleInternalMap.Values.Where(v => v.Amount.HasValue).Sum(v => (long)v.Amount!.Value),
                TotalGatewayCount = gatewayMap.Count,
                TotalGatewayAmount = gatewayMap.Values.Sum(v => (long)v.Amount),
                IneligibleCount = ineligibleInternalList.Count,
                Warnings = warnings
            };

            // Ghi nhận các giao dịch chưa đủ điều kiện vào danh sách kết quả đối soát
            foreach (var inelig in ineligibleInternalList)
            {
                summary.Items.Add(new ReconciliationItemDto
                {
                    TransactionId = inelig.TransactionId,
                    OrderCode = inelig.OrderCode,
                    OrderId = inelig.OrderId,
                    InternalAmount = inelig.Amount,
                    GatewayAmount = null,
                    Difference = null,
                    Group = ReconciliationGroups.Ineligible,
                    InternalDate = inelig.EventDate,
                    GatewayDate = null,
                    Status = inelig.OrderStatus ?? "INELIGIBLE",
                    Notes = inelig.IneligibilityReason ?? "Giao dịch chưa đủ điều kiện đối soát."
                });
                warnings.Add($"[Chưa đủ điều kiện] Giao dịch {inelig.TransactionId} (Đơn #{inelig.OrderCode ?? 0}): {inelig.IneligibilityReason}");
            }

            // Danh sách các cặp giao dịch đã được liên kết đối soát
            var matchedPairs = new List<(string TxId, InternalTransactionRecord Internal, GatewayStatementItemDto Gateway)>();
            var matchedInternalTxIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedGatewayTxIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Bước 1: ƯU TIÊN HÀNG ĐẦU - Khớp trực tiếp theo TransactionId duy nhất (Primary Key)
            foreach (var kvp in gatewayMap)
            {
                if (eligibleInternalMap.TryGetValue(kvp.Key, out var internalItem))
                {
                    matchedPairs.Add((kvp.Key, internalItem, kvp.Value));
                    matchedInternalTxIds.Add(kvp.Key);
                    matchedGatewayTxIds.Add(kvp.Key);
                }
            }

            // Bước 2: Khớp phụ trợ (Fallback) theo OrderCode - CHỈ ÁP DỤNG KHI XÁC ĐỊNH ĐƯỢC ỨNG VIÊN DUY NHẤT 1-1
            // Gom các dòng sao kê cổng chưa được ghép trực tiếp theo TransactionId
            var unmatchedGwByOrderCode = new Dictionary<long, List<GatewayStatementItemDto>>();
            foreach (var kvp in gatewayMap)
            {
                if (matchedGatewayTxIds.Contains(kvp.Key))
                    continue;

                long? refOrderCode = GetReferencedOrderCode(kvp.Value);
                if (refOrderCode.HasValue)
                {
                    if (!unmatchedGwByOrderCode.TryGetValue(refOrderCode.Value, out var list))
                    {
                        list = new List<GatewayStatementItemDto>();
                        unmatchedGwByOrderCode[refOrderCode.Value] = list;
                    }
                    list.Add(kvp.Value);
                }
            }

            foreach (var (orderCode, gwCandidates) in unmatchedGwByOrderCode)
            {
                if (!internalGroupsByOrderCode.TryGetValue(orderCode, out var internalCandidates))
                {
                    // Không có giao dịch nội bộ nào cho OrderCode này -> Giữ nguyên để ghi nhận GatewayOnly
                    continue;
                }

                // Lọc các giao dịch nội bộ chưa được ghép trực tiếp theo TransactionId ở Bước 1
                var remainingInternal = internalCandidates
                    .Where(ic => !matchedInternalTxIds.Contains(ic.TransactionId))
                    .ToList();

                if (remainingInternal.Count == 0)
                {
                    // Mọi giao dịch nội bộ của đơn hàng này đã được ghép chính xác theo TransactionId
                    continue;
                }

                // Kiểm tra tính mơ hồ (Ambiguity Check):
                // 1. Nếu đơn hàng có nhiều hơn 1 giao dịch nội bộ (ví dụ: nhiều lần thử thanh toán, nhiều TransactionId khác nhau, giao dịch thất bại kèm thành công)
                if (internalCandidates.Count > 1 || remainingInternal.Count > 1)
                {
                    var txDetails = string.Join(", ", internalCandidates.Select(c => $"{c.TransactionId} ({c.OrderStatus ?? "N/A"})"));
                    warnings.Add($"Đơn hàng #{orderCode} có {internalCandidates.Count} giao dịch/lần thử nội bộ ({txDetails}). Không thể tự động ghép theo OrderCode do có nhiều ứng viên; yêu cầu đối soát chính xác theo TransactionId.");
                    continue;
                }

                // 2. Nếu sao kê cổng có nhiều hơn 1 dòng cùng tham chiếu đến đơn hàng này
                if (gwCandidates.Count > 1)
                {
                    var gwDetails = string.Join(", ", gwCandidates.Select(g => g.TransactionId));
                    warnings.Add($"Sao kê cổng có {gwCandidates.Count} dòng giao dịch cùng tham chiếu đơn hàng #{orderCode} ({gwDetails}). Không thể tự động ghép do có nhiều ứng viên phía cổng.");
                    continue;
                }

                // 3. Trường hợp xác định được Ứng viên DUY NHẤT 1-1 (1 giao dịch nội bộ và 1 dòng sao kê cổng)
                var singleInternal = remainingInternal[0];
                var singleGw = gwCandidates[0];

                matchedPairs.Add((singleInternal.TransactionId, singleInternal, singleGw));
                matchedInternalTxIds.Add(singleInternal.TransactionId);
                matchedGatewayTxIds.Add(singleGw.TransactionId);
            }

            // Bước 3: Đánh giá số tiền cho các cặp giao dịch đã ghép (Khớp hoàn toàn hoặc Lệch tiền)
            foreach (var (txId, internalItem, gatewayItem) in matchedPairs)
            {
                int? intAmount = internalItem.Amount;
                int gwAmount = gatewayItem.Amount;
                bool isFallbackMatch = !string.Equals(txId, gatewayItem.TransactionId, StringComparison.OrdinalIgnoreCase);
                string matchMechanism = isFallbackMatch
                    ? $" [Khớp phụ trợ theo OrderCode #{gatewayItem.OrderCode ?? internalItem.OrderCode}; Mã cổng: {gatewayItem.TransactionId}]"
                    : string.Empty;

                if (!intAmount.HasValue)
                {
                    var item = new ReconciliationItemDto
                    {
                        TransactionId = txId,
                        OrderCode = internalItem.OrderCode ?? gatewayItem.OrderCode,
                        OrderId = internalItem.OrderId,
                        InternalAmount = null,
                        GatewayAmount = gwAmount,
                        Difference = gwAmount,
                        Group = ReconciliationGroups.AmountMismatch,
                        InternalDate = internalItem.EventDate,
                        GatewayDate = gatewayItem.TransactionDate,
                        Status = internalItem.OrderStatus ?? "RECORDED",
                        Notes = $"Cảnh báo: Hệ thống chưa xác định được số tiền nội bộ để so sánh.{matchMechanism}"
                    };
                    summary.AmountMismatchCount++;
                    summary.AmountMismatchGatewayAmount += gwAmount;
                    summary.AmountMismatchNetDifference += gwAmount;
                    summary.Items.Add(item);
                }
                else if (intAmount.Value == gwAmount)
                {
                    // 🟢 Khớp hoàn toàn
                    var item = new ReconciliationItemDto
                    {
                        TransactionId = txId,
                        OrderCode = internalItem.OrderCode ?? gatewayItem.OrderCode,
                        OrderId = internalItem.OrderId,
                        InternalAmount = intAmount.Value,
                        GatewayAmount = gwAmount,
                        Difference = 0,
                        Group = ReconciliationGroups.Matched,
                        InternalDate = internalItem.EventDate,
                        GatewayDate = gatewayItem.TransactionDate,
                        Status = internalItem.OrderStatus ?? "PAID",
                        Notes = $"Khớp hoàn toàn giữa hệ thống và cổng thanh toán.{matchMechanism}"
                    };
                    summary.MatchedCount++;
                    summary.MatchedInternalAmount += intAmount.Value;
                    summary.MatchedGatewayAmount += gwAmount;
                    summary.Items.Add(item);
                }
                else
                {
                    // 🟡 Lệch số tiền
                    int diff = gwAmount - intAmount.Value;
                    var item = new ReconciliationItemDto
                    {
                        TransactionId = txId,
                        OrderCode = internalItem.OrderCode ?? gatewayItem.OrderCode,
                        OrderId = internalItem.OrderId,
                        InternalAmount = intAmount.Value,
                        GatewayAmount = gwAmount,
                        Difference = diff,
                        Group = ReconciliationGroups.AmountMismatch,
                        InternalDate = internalItem.EventDate,
                        GatewayDate = gatewayItem.TransactionDate,
                        Status = internalItem.OrderStatus ?? "NEEDS_ATTENTION",
                        Notes = $"Lệch số tiền: Hệ thống ghi nhận {intAmount.Value:N0} đ, Cổng báo {gwAmount:N0} đ (Chênh lệch: {diff:+0;-#;0} đ).{matchMechanism}"
                    };
                    summary.AmountMismatchCount++;
                    summary.AmountMismatchInternalAmount += intAmount.Value;
                    summary.AmountMismatchGatewayAmount += gwAmount;
                    summary.AmountMismatchNetDifference += diff;
                    summary.Items.Add(item);
                }
            }

            // Bước 4: Các giao dịch chỉ có ở hệ thống (SystemOnly) - CHỈ ÁP DỤNG CHO CÁC GIAO DỊCH ĐỦ ĐIỀU KIỆN
            foreach (var kvp in eligibleInternalMap)
            {
                if (matchedInternalTxIds.Contains(kvp.Key))
                    continue;

                var internalItem = kvp.Value;
                int intAmount = internalItem.Amount ?? 0;
                var item = new ReconciliationItemDto
                {
                    TransactionId = kvp.Key,
                    OrderCode = internalItem.OrderCode,
                    OrderId = internalItem.OrderId,
                    InternalAmount = internalItem.Amount,
                    GatewayAmount = null,
                    Difference = -intAmount,
                    Group = ReconciliationGroups.SystemOnly,
                    InternalDate = internalItem.EventDate,
                    GatewayDate = null,
                    Status = internalItem.OrderStatus ?? "SYSTEM_ONLY",
                    Notes = "Có trong hệ thống nhưng không tìm thấy trong sao kê cổng thanh toán."
                };
                summary.SystemOnlyCount++;
                summary.SystemOnlyAmount += intAmount;
                summary.Items.Add(item);
            }

            // Bước 5: Các giao dịch chỉ có ở cổng (GatewayOnly)
            foreach (var kvp in gatewayMap)
            {
                if (matchedGatewayTxIds.Contains(kvp.Key))
                    continue;

                var gatewayItem = kvp.Value;
                int gwAmount = gatewayItem.Amount;
                var item = new ReconciliationItemDto
                {
                    TransactionId = kvp.Key,
                    OrderCode = gatewayItem.OrderCode,
                    OrderId = null,
                    InternalAmount = null,
                    GatewayAmount = gwAmount,
                    Difference = gwAmount,
                    Group = ReconciliationGroups.GatewayOnly,
                    InternalDate = null,
                    GatewayDate = gatewayItem.TransactionDate,
                    Status = "GATEWAY_ONLY",
                    Notes = "Có trong sao kê cổng thanh toán nhưng hệ thống chưa ghi nhận giao dịch."
                };
                summary.GatewayOnlyCount++;
                summary.GatewayOnlyAmount += gwAmount;
                summary.Items.Add(item);
            }

            // Sắp xếp ưu tiên: Các nhóm lệch hiển thị trước để kế toán xử lý, nhóm khớp hiển thị sau
            summary.Items = summary.Items
                .OrderBy(i => i.Group switch
                {
                    ReconciliationGroups.AmountMismatch => 1,
                    ReconciliationGroups.GatewayOnly => 2,
                    ReconciliationGroups.SystemOnly => 3,
                    ReconciliationGroups.Ineligible => 4,
                    ReconciliationGroups.Matched => 5,
                    _ => 6
                })
                .ThenByDescending(i => Math.Abs(i.Difference ?? 0))
                .ToList();

            return summary;
        }

        public async Task<ReconciliationSession> SaveReconciliationSessionAsync(
            DateTimeOffset fromDate,
            DateTimeOffset toDate,
            string sourceType,
            string rawCsv,
            ReconciliationSummaryDto summary,
            string? executedBy,
            CancellationToken cancellationToken = default)
        {
            var session = new ReconciliationSession
            {
                Id = Guid.NewGuid(),
                SessionCode = $"REC_{DateTime.UtcNow:yyyyMMdd}_{Guid.NewGuid():N}"[..18].ToUpperInvariant(),
                PeriodFrom = fromDate,
                PeriodTo = toDate,
                SourceType = sourceType,
                Status = "COMPLETED",
                TotalInternalCount = summary.TotalInternalCount,
                TotalInternalAmount = summary.TotalInternalAmount,
                TotalGatewayCount = summary.TotalGatewayCount,
                TotalGatewayAmount = summary.TotalGatewayAmount,
                MatchedCount = summary.MatchedCount,
                SystemOnlyCount = summary.SystemOnlyCount,
                GatewayOnlyCount = summary.GatewayOnlyCount,
                AmountMismatchCount = summary.AmountMismatchCount,
                IneligibleCount = summary.IneligibleCount,
                RawCsv = rawCsv,
                ExecutedAt = DateTimeOffset.UtcNow,
                ExecutedBy = executedBy ?? "Auditor"
            };

            // Đồng bộ định danh phiên và cờ IsMockStatement vào Summary TRƯỚC KHI serialize thành SummaryJson
            summary.SessionId = session.Id;
            summary.SessionCode = session.SessionCode;
            summary.IsMockStatement = string.Equals(sourceType, "MOCK_GATEWAY", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(sourceType, "MockStatement", StringComparison.OrdinalIgnoreCase);

            session.SummaryJson = JsonSerializer.Serialize(summary);

            bool isRelational = _context.Database.IsRelational();
            await using var tx = isRelational ? await _context.Database.BeginTransactionAsync(cancellationToken) : null;
            try
            {
                _context.ReconciliationSessions.Add(session);
                await _context.SaveChangesAsync(cancellationToken);
                if (tx != null) await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                throw;
            }

            return session;
        }

        public async Task<List<ReconciliationSessionDto>> GetRecentSessionsAsync(
            int limit = 20,
            CancellationToken cancellationToken = default)
        {
            var sessions = await _context.ReconciliationSessions
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            return sessions
                .OrderByDescending(s => s.ExecutedAt)
                .Take(limit)
                .Select(s => new ReconciliationSessionDto
                {
                    Id = s.Id,
                    SessionCode = s.SessionCode,
                    PeriodFrom = s.PeriodFrom,
                    PeriodTo = s.PeriodTo,
                    SourceType = s.SourceType,
                    Status = s.Status,
                    TotalInternalCount = s.TotalInternalCount,
                    TotalInternalAmount = s.TotalInternalAmount,
                    TotalGatewayCount = s.TotalGatewayCount,
                    TotalGatewayAmount = s.TotalGatewayAmount,
                    MatchedCount = s.MatchedCount,
                    SystemOnlyCount = s.SystemOnlyCount,
                    GatewayOnlyCount = s.GatewayOnlyCount,
                    AmountMismatchCount = s.AmountMismatchCount,
                    IneligibleCount = s.IneligibleCount,
                    ExecutedAt = s.ExecutedAt,
                    ExecutedBy = s.ExecutedBy
                })
                .ToList();
        }

        public async Task<ReconciliationSession?> GetSessionByIdAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            return await _context.ReconciliationSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        }

        public string ExportSummaryToCsv(ReconciliationSummaryDto summary)
        {
            var sb = new StringBuilder();

            // Metadata Header
            sb.AppendLine($"# BÁO CÁO ĐỐI SOÁT GIAO DỊCH VỚI SAO KÊ CỔNG THANH TOÁN (STORY S-51)");
            sb.AppendLine($"# Nguồn sao kê: {(summary.IsMockStatement ? "DỮ LIỆU MÔ PHỎNG (MOCK STATEMENT - SANDBOX DEV) - KHÔNG PHẢI SAO KÊ CỔNG THỰC TẾ" : "Sao kê kế toán thực tế (CSV tải lên)")}");
            if (!string.IsNullOrEmpty(summary.SessionCode))
            {
                sb.AppendLine($"# Mã phiên đối soát: {summary.SessionCode}");
            }
            sb.AppendLine($"# Kỳ đối soát: {summary.FromDate:yyyy-MM-dd} đến {summary.ToDate:yyyy-MM-dd}");
            sb.AppendLine($"# Thời điểm lập: {summary.ReconciledAt:yyyy-MM-dd HH:mm:ss UTC}");
            sb.AppendLine($"# Tổng giao dịch nội bộ: {summary.TotalInternalCount} ({summary.TotalInternalAmount:N0} đ)");
            sb.AppendLine($"# Tổng giao dịch sao kê cổng: {summary.TotalGatewayCount} ({summary.TotalGatewayAmount:N0} đ)");
            sb.AppendLine($"# Khớp hoàn toàn: {summary.MatchedCount} | Chỉ có ở hệ thống: {summary.SystemOnlyCount} | Chỉ có ở cổng: {summary.GatewayOnlyCount} | Lệch tiền: {summary.AmountMismatchCount} | Chưa đủ điều kiện: {summary.IneligibleCount}");
            if (summary.Warnings != null && summary.Warnings.Count > 0)
            {
                sb.AppendLine($"# Cảnh báo phát hiện ({summary.Warnings.Count} cảnh báo):");
                foreach (var warn in summary.Warnings)
                {
                    sb.AppendLine($"# - {warn.Replace("\r", string.Empty).Replace("\n", " ")}");
                }
            }
            sb.AppendLine();

            // Data Table
            sb.AppendLine("Mã Giao Dịch,Mã Đơn Hàng,Nhóm Đối Soát,Số Tiền Hệ Thống,Số Tiền Cổng,Chênh Lệch,Ngày Hệ Thống,Ngày Cổng,Trạng Thái,Ghi Chú");

            foreach (var item in summary.Items)
            {
                string groupLabel = item.Group switch
                {
                    ReconciliationGroups.Matched => "Khớp hoàn toàn",
                    ReconciliationGroups.SystemOnly => "Chỉ có ở hệ thống",
                    ReconciliationGroups.GatewayOnly => "Chỉ có ở cổng",
                    ReconciliationGroups.AmountMismatch => "Lệch số tiền",
                    ReconciliationGroups.Ineligible => "Chưa đủ điều kiện",
                    _ => item.Group
                };

                string intAmountStr = item.InternalAmount.HasValue ? item.InternalAmount.Value.ToString() : string.Empty;
                string gwAmountStr = item.GatewayAmount.HasValue ? item.GatewayAmount.Value.ToString() : string.Empty;
                string diffStr = item.Difference.HasValue ? item.Difference.Value.ToString() : string.Empty;
                string intDateStr = item.InternalDate.HasValue ? item.InternalDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;
                string gwDateStr = item.GatewayDate.HasValue ? item.GatewayDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;

                sb.AppendLine($"{EscapeCsv(item.TransactionId)},{item.OrderCode},{EscapeCsv(groupLabel)},{intAmountStr},{gwAmountStr},{diffStr},{intDateStr},{gwDateStr},{EscapeCsv(item.Status)},{EscapeCsv(item.Notes)}");
            }

            return sb.ToString();
        }

        private static int? TryExtractAmountFromPayload(string? rawPayload)
        {
            if (string.IsNullOrWhiteSpace(rawPayload))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(rawPayload);
                var root = doc.RootElement;

                // Thử tìm data.amount
                if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                {
                    if (dataEl.TryGetProperty("amount", out var amtProp))
                    {
                        if (amtProp.ValueKind == JsonValueKind.Number && amtProp.TryGetInt32(out var amt))
                            return amt;
                        if (amtProp.ValueKind == JsonValueKind.String && int.TryParse(amtProp.GetString(), out var amtParsed))
                            return amtParsed;
                    }
                }

                // Thử tìm amount ở root
                if (root.TryGetProperty("amount", out var rootAmt))
                {
                    if (rootAmt.ValueKind == JsonValueKind.Number && rootAmt.TryGetInt32(out var a))
                        return a;
                    if (rootAmt.ValueKind == JsonValueKind.String && int.TryParse(rootAmt.GetString(), out var aParsed))
                        return aParsed;
                }
            }
            catch
            {
                // Payload không phải JSON hợp lệ hoặc không có amount
            }

            return null;
        }

        private static long? TryExtractOrderCodeFromPayload(string? rawPayload)
        {
            if (string.IsNullOrWhiteSpace(rawPayload))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(rawPayload);
                var root = doc.RootElement;

                // Thử tìm data.orderCode
                if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                {
                    if (dataEl.TryGetProperty("orderCode", out var ocProp))
                    {
                        if (ocProp.ValueKind == JsonValueKind.Number && ocProp.TryGetInt64(out var oc))
                            return oc;
                        if (ocProp.ValueKind == JsonValueKind.String && long.TryParse(ocProp.GetString(), out var ocParsed))
                            return ocParsed;
                    }
                }

                // Thử tìm orderCode ở root
                if (root.TryGetProperty("orderCode", out var rootOc))
                {
                    if (rootOc.ValueKind == JsonValueKind.Number && rootOc.TryGetInt64(out var oc2))
                        return oc2;
                    if (rootOc.ValueKind == JsonValueKind.String && long.TryParse(rootOc.GetString(), out var oc2Parsed))
                        return oc2Parsed;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string EscapeCsv(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
            {
                return $"\"{text.Replace("\"", "\"\"")}\"";
            }
            return text;
        }
    }
}
