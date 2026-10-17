using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ xuất danh sách khách vào cửa theo suất diễn (Story S-62).
    /// Hỗ trợ phát hiện xung đột và xuất CSV UTF-8 với BOM chuẩn hóa cho Excel.
    /// </summary>
    public class CheckinExportService : ICheckinExportService
    {
        private readonly AppDbContext _context;

        public CheckinExportService(AppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<List<CheckinExportItemDto>> GetCheckinExportDataAsync(ExportCheckinListQuery query, CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            var showtimeExists = await _context.Showtimes
                .AsNoTracking()
                .AnyAsync(s => s.Id == query.ShowId, cancellationToken);

            if (!showtimeExists)
            {
                throw new KeyNotFoundException($"Không tìm thấy suất diễn với ID {query.ShowId}.");
            }

            // 1. Lấy tất cả các vé thuộc về suất diễn này
            var tickets = await _context.Tickets
                .AsNoTracking()
                .Include(t => t.OrderItem)
                    .ThenInclude(oi => oi.Seat)
                .Include(t => t.OrderItem)
                    .ThenInclude(oi => oi.Order)
                .Where(t => t.OrderItem.Order.ShowtimeId == query.ShowId)
                .ToListAsync(cancellationToken);

            var orderItemIds = tickets.Select(t => t.OrderItemId).Distinct().ToList();
            var orderIds = tickets.Select(t => t.OrderItem.OrderId).Distinct().ToList();
            var orderHistory = (await _context.OrderAuditLogs.AsNoTracking()
                .Where(a => orderIds.Contains(a.OrderId) && a.EntityType == "ORDER")
                .OrderBy(a => a.Timestamp).ToListAsync(cancellationToken))
                .ToLookup(a => a.OrderId);

            // 2. Lấy các bản ghi cho vào bổ sung (Story S-31)
            var readmissions = await _context.TicketReadmissions
                .AsNoTracking()
                .Where(r => orderItemIds.Contains(r.OrderItemId))
                .OrderBy(r => r.AdmittedAt)
                .ToListAsync(cancellationToken);

            var readmissionLookup = readmissions
                .GroupBy(r => r.OrderItemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // 3. Lấy các bản ghi đồng bộ soát vé ngoại tuyến (Story S-35 & S-36)
            var offlineRecords = await _context.OfflineCheckInRecords
                .AsNoTracking()
                .Where(r => r.ShowtimeId == query.ShowId)
                .OrderBy(r => r.ScannedAtDevice)
                .ToListAsync(cancellationToken);

            var offlineLookupByCode = offlineRecords
                .GroupBy(r => (r.TicketCode ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var result = new List<CheckinExportItemDto>();

            // 4. Xử lý từng vé để xác định lượt vào cửa và phát hiện xung đột
            foreach (var t in tickets)
            {
                var oi = t.OrderItem;
                var seat = oi?.Seat;
                var order = oi?.Order;

                string seatCode = seat != null
                    ? (!string.IsNullOrWhiteSpace(seat.Row) ? $"{seat.Row}{seat.SeatNumber}" : $"{seat.SeatNumber}")
                    : "N/A";

                readmissionLookup.TryGetValue(t.OrderItemId, out var itemReadmissions);
                offlineLookupByCode.TryGetValue((t.TicketCode ?? string.Empty).Trim(), out var itemOfflineRecords);

                int readmissionCount = itemReadmissions?.Count ?? 0;
                bool hasOfflineConflicts = itemOfflineRecords != null && itemOfflineRecords.Any(r => r.IsConflict);
                bool isOrderPaid = order != null && order.Status == OrderStatus.Paid;

                bool isMainCheckedIn = oi != null && (oi.IsCheckedIn || oi.CheckInTime != null);

                // Bản ghi soát vé lần đầu (Main check-in)
                if (isMainCheckedIn)
                {
                    bool isConflict = false;
                    var conflictNotes = new List<string>();

                    // A later cancellation/refund does not invalidate an earlier paid admission.
                    var admissionStatus = order == null ? (OrderStatus?)null : StatusAt(order, oi!.CheckInTime, orderHistory[order.Id]);
                    if (admissionStatus != OrderStatus.Paid && order != null)
                    {
                        isConflict = true;
                        conflictNotes.Add($"Đơn hàng trạng thái {admissionStatus} tại thời điểm vào cửa");
                    }
                    else if (order?.Status == OrderStatus.Cancelled)
                    {
                        conflictNotes.Add("Đơn hàng đã được hủy sau lượt vào; lượt vào được ghi nhận khi đơn đã thanh toán");
                    }

                    // Kiểm tra xung đột 3: Có bản ghi xung đột ngoại tuyến cho mã vé này
                    if (hasOfflineConflicts && itemOfflineRecords != null)
                    {
                        isConflict = true;
                        var offlineReasons = string.Join("; ", itemOfflineRecords.Where(r => r.IsConflict).Select(r => ConflictDescription(r.ConflictReason)).Distinct());
                        conflictNotes.Add($"Xung đột ngoại tuyến: {offlineReasons}");
                    }

                    result.Add(new CheckinExportItemDto
                    {
                        TicketCode = t.TicketCode ?? string.Empty,
                        SeatNumber = seatCode,
                        CheckinTime = oi!.CheckInTime,
                        GateName = oi.CheckInGate ?? string.Empty,
                        EntryType = itemOfflineRecords?.Any(r => !r.IsConflict && r.Status == "SYNCED") == true ? "Offline" : "Lần đầu",
                        IsConflict = isConflict,
                        ConflictNote = conflictNotes.Count > 0 ? string.Join(" | ", conflictNotes) : null,
                        OrderStatus = order?.Status.ToString()
                    });
                }

                // Ghi nhận các lượt vào bổ sung (Readmissions) thành từng dòng cụ thể
                if (itemReadmissions != null && itemReadmissions.Count > 0)
                {
                    int index = 1;
                    foreach (var readm in itemReadmissions)
                    {
                        var admissionStatus = order == null ? (OrderStatus?)null : StatusAt(order, readm.AdmittedAt, orderHistory[order.Id]);
                        var statusNote = admissionStatus != OrderStatus.Paid
                            ? $" | Đơn hàng trạng thái {admissionStatus} tại thời điểm vào lại"
                            : order?.Status == OrderStatus.Cancelled ? " | Đơn hàng đã được hủy sau lượt vào" : "";
                        result.Add(new CheckinExportItemDto
                        {
                            TicketCode = t.TicketCode ?? string.Empty,
                            SeatNumber = seatCode,
                            CheckinTime = readm.AdmittedAt,
                            GateName = readm.Gate,
                            IsConflict = admissionStatus != OrderStatus.Paid,
                            EntryType = "Vào lại được duyệt",
                            Result = "Đã vào",
                            ConflictNote = $"Vào bổ sung lần {index++}: {readm.Reason} (NV: {readm.StaffName}){statusNote}",
                            OrderStatus = order?.Status.ToString()
                        });
                    }
                }

                // Nếu có tùy chọn bao gồm vé chưa vào cửa
                if (query.IncludeNotCheckedIn && !isMainCheckedIn && readmissionCount == 0 && (itemOfflineRecords == null || itemOfflineRecords.Count == 0))
                {
                    result.Add(new CheckinExportItemDto
                    {
                        TicketCode = t.TicketCode ?? string.Empty,
                        SeatNumber = seatCode,
                        CheckinTime = null,
                        GateName = string.Empty,
                        EntryType = "Chưa vào",
                        Result = "Chưa vào",
                        IsConflict = !isOrderPaid,
                        ConflictNote = !isOrderPaid && order != null ? $"Đơn hàng {order.Status}" : null,
                        OrderStatus = order?.Status.ToString()
                    });
                }
            }

            // Rejected offline scans are audit attempts, never successful admissions.
            if (query.IncludeScanLog)
            {
                var seatsByCode = tickets.ToDictionary(t => t.TicketCode.Trim(),
                    t => t.OrderItem.Seat == null ? "N/A" : $"{t.OrderItem.Seat.Row}{t.OrderItem.Seat.SeatNumber}",
                    StringComparer.OrdinalIgnoreCase);
                foreach (var record in offlineRecords.Where(r => r.IsConflict).DistinctBy(r => r.Id))
                {
                    result.Add(new CheckinExportItemDto
                    {
                        TicketCode = record.TicketCode,
                        SeatNumber = seatsByCode.GetValueOrDefault(record.TicketCode.Trim(), "N/A"),
                        CheckinTime = record.ScannedAtDevice,
                        GateName = record.GateName,
                        EntryType = "Quét offline",
                        Result = "Xung đột",
                        IsConflict = true,
                        ConflictNote = ConflictDescription(record.ConflictReason)
                    });
                }
            }

            // 6. Lọc theo cổng nếu có
            var gateFilter = query.EffectiveGateFilter;
            if (!string.IsNullOrWhiteSpace(gateFilter))
            {
                result = result
                    .Where(item => string.Equals(item.GateName?.Trim(), gateFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // 7. Lọc chỉ lấy bản ghi xung đột nếu được yêu cầu
            if (query.OnlyConflicts == true)
            {
                result = result.Where(item => item.IsConflict).ToList();
            }

            // 8. Sắp xếp theo thời gian vào (tăng dần) rồi đến mã vé
            return result
                .OrderBy(item => item.CheckinTime ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.SeatNumber)
                .ThenBy(item => item.TicketCode)
                .ToList();
        }

        public byte[] GenerateCsvBytes(IEnumerable<CheckinExportItemDto> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            TimeZoneInfo vnTimeZone;
            try
            {
                vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Ho_Chi_Minh");
            }
            catch
            {
                vnTimeZone = TimeZoneInfo.Utc;
            }

            using var memoryStream = new MemoryStream();
            // Sử dụng UTF8Encoding(encoderShouldEmitUTF8Identifier: true) để tự động ghi byte order mark (BOM) UTF-8 (\uFEFF)
            // Nhờ đó Excel trên Windows/macOS sẽ mở tệp hiển thị chuẩn 100% tiếng Việt có dấu mà không vỡ font
            using (var writer = new StreamWriter(memoryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.NewLine = "\r\n";
                // Dòng tiêu đề chuẩn hóa
                writer.WriteLine("Mã vé,Số ghế,Thời gian vào (UTC+7),Cửa vào,Cờ xung đột,Ghi chú xung đột,Loại lượt,Kết quả");

                foreach (var item in items)
                {
                    string formattedTime = "";
                    if (item.CheckinTime.HasValue)
                    {
                        var localTime = TimeZoneInfo.ConvertTime(item.CheckinTime.Value, vnTimeZone);
                        formattedTime = localTime.ToString("yyyy-MM-dd HH:mm:ss");
                    }

                    var line = string.Join(",",
                        EscapeCsv(item.TicketCode),
                        EscapeCsv(item.SeatNumber),
                        EscapeCsv(formattedTime),
                        EscapeCsv(item.GateName),
                        item.IsConflict ? "Có" : "Không",
                        EscapeCsv(item.ConflictNote),
                        EscapeCsv(item.EntryType),
                        EscapeCsv(item.Result)
                    );

                    writer.WriteLine(line);
                }
            }

            return memoryStream.ToArray();
        }

        public async Task<byte[]> ExportCheckinCsvAsync(ExportCheckinListQuery query, CancellationToken cancellationToken = default)
        {
            var data = await GetCheckinExportDataAsync(query, cancellationToken);
            return GenerateCsvBytes(data);
        }

        private static OrderStatus StatusAt(Order order, DateTimeOffset? time, IEnumerable<OrderAuditLog> history)
        {
            if (!time.HasValue) return order.Status;
            var transitions = history.Where(a => Enum.TryParse<OrderStatus>(a.NewStatus, true, out _)).ToList();
            var preceding = transitions.LastOrDefault(a => a.Timestamp <= time.Value);
            if (preceding != null && Enum.TryParse<OrderStatus>(preceding.NewStatus, true, out var status)) return status;
            var following = transitions.FirstOrDefault(a => a.Timestamp > time.Value);
            if (following != null && Enum.TryParse<OrderStatus>(following.OldStatus, true, out status)) return status;
            // Legacy data without history retains the conservative current-state check.
            return order.Status;
        }

        private static string ConflictDescription(string? reason) => reason switch
        {
            "DIFFERENT_GATE_CONFLICT" => "Vé được quét tại nhiều cửa",
            "SHOWTIME_MISMATCH" => "Vé không thuộc suất diễn này",
            "TICKET_NOT_FOUND" => "Không tìm thấy vé",
            null or "" => "Xung đột ngoại tuyến",
            _ => reason
        };

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var trimmed = value.TrimStart();
            if (trimmed.Length > 0 && ("=+-@".Contains(trimmed[0]) || value[0] is '\t' or '\r' or '\n'))
                value = "'" + value;

            // Nếu giá trị chứa dấu phẩy, nháy kép, xuống dòng -> bọc trong nháy kép và nhân đôi ký tự nháy kép (RFC 4180)
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }
    }
}
