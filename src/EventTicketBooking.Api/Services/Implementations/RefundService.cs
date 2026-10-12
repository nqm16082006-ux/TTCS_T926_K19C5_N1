using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ hoàn tiền, retry tự động với exponential backoff,
    /// chống hoàn tiền trùng lặp và thông báo kế toán (Jira Story S-48).
    /// </summary>
    public class RefundService : IRefundService
    {
        private readonly AppDbContext _context;
        private readonly IPaymentGateway _paymentGateway;
        private readonly IAccountingNotificationService _accountingNotificationService;
        private readonly IOrderAuditService _orderAuditService;
        private readonly RefundOptions _options;
        private readonly ILogger<RefundService> _logger;

        private static readonly HashSet<string> NonRetryableErrorCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "INVALID_ACCOUNT",
            "ACCOUNT_NOT_FOUND",
            "ACCOUNT_FROZEN",
            "INSUFFICIENT_FUNDS",
            "DUPLICATE_REFUND",
            "TRANSACTION_NOT_FOUND",
            "INVALID_AMOUNT",
            "CARD_EXPIRED",
            "UNSUPPORTED_OPERATION",
            "GATEWAY_NOT_SUPPORTED",
            "PAYOS_NOT_SUPPORTED",
            "UNAUTHORIZED",
            "FORBIDDEN",
            "BAD_REQUEST",
            "INVALID_REQUEST",
            "TRANSACTION_ALREADY_REFUNDED",
            "HTTP_400", "HTTP_401", "HTTP_403", "HTTP_404", "HTTP_422",
            "400", "401", "403", "404", "422"
        };

        public RefundService(
            AppDbContext context,
            IPaymentGateway paymentGateway,
            IAccountingNotificationService accountingNotificationService,
            IOrderAuditService orderAuditService,
            IOptions<RefundOptions> options,
            ILogger<RefundService> logger)
        {
            _context = context;
            _paymentGateway = paymentGateway;
            _accountingNotificationService = accountingNotificationService;
            _orderAuditService = orderAuditService;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<RefundRequest> CreateRefundRequestAsync(
            Guid orderId,
            int? amount = null,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            var order = await _context.Orders
                .Include(o => o.PaymentTransaction)
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

            if (order == null)
            {
                throw new ArgumentException($"Không tìm thấy đơn hàng với ID {orderId}.");
            }

            // 1. Chống duplicate: Kiểm tra xem đã có yêu cầu hoàn tiền đang hoạt động chưa
            var existingRefund = await _context.RefundRequests
                .FirstOrDefaultAsync(r => r.OrderId == orderId &&
                    (r.Status == RefundStatus.Pending ||
                     r.Status == RefundStatus.Processing ||
                     r.Status == RefundStatus.RetryScheduled ||
                     r.Status == RefundStatus.Succeeded ||
                     r.Status == RefundStatus.ManualReviewRequired), cancellationToken);

            if (existingRefund != null)
            {
                _logger.LogInformation("Tái sử dụng yêu cầu hoàn tiền hiện có cho OrderId {OrderId} (RefundId: {RefundId}, Status: {Status}).",
                    orderId, existingRefund.Id, existingRefund.Status);
                return existingRefund;
            }

            int refundAmount = amount ?? (order.TotalAmount > 0 ? order.TotalAmount : 0);
            if (refundAmount <= 0)
            {
                order.CalculateTotal();
                refundAmount = order.TotalAmount;
            }

            long orderCode = order.PaymentTransaction?.OrderCode ??
                Math.Abs(BitConverter.ToInt64(order.Id.ToByteArray(), 0) % 9000000000L) + 1000000000L;

            string idempotencyKey = $"ref_{order.Id}_{Guid.NewGuid():N}";
            string refundReason = string.IsNullOrWhiteSpace(reason) ? "ShowtimeCancelledOrOverdue" : reason;

            var refund = new RefundRequest
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = orderCode,
                TransactionId = order.PaymentTransaction?.TransactionId,
                Amount = refundAmount,
                Currency = "VND",
                Reason = refundReason,
                Status = RefundStatus.Pending,
                AttemptCount = 0,
                RetryCount = 0,
                MaxRetries = _options.MaxRetries,
                IdempotencyKey = idempotencyKey,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _context.RefundRequests.Add(refund);

            _orderAuditService.Record(
                order.Id,
                "REFUND",
                refund.Id.ToString(),
                "REFUND_REQUESTED",
                null,
                RefundStatus.Pending.ToString(),
                "SYSTEM",
                "system:RefundService",
                null,
                $"Tạo yêu cầu hoàn tiền {refundAmount:N0} VNĐ. Lý do: {refundReason}.");

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Đã tạo RefundRequest {RefundId} cho Order {OrderId}, OrderCode: {OrderCode}, Amount: {Amount}",
                refund.Id, order.Id, orderCode, refundAmount);

            return refund;
        }

        public async Task<RefundExecutionResult> ProcessRefundAsync(Guid refundId, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;

            // Bước 1: Khóa và chuyển trạng thái sang Processing một cách an toàn
            RefundRequest? refund;
            if (_context.Database.IsRelational() && _context.Database.CurrentTransaction == null)
            {
                await using var lockTx = await _context.Database.BeginTransactionAsync(cancellationToken);
                if (_context.Database.IsNpgsql())
                {
                    refund = await _context.RefundRequests
                        .FromSqlRaw("SELECT * FROM refund_requests WHERE \"Id\" = {0} FOR UPDATE", refundId)
                        .FirstOrDefaultAsync(cancellationToken);
                }
                else
                {
                    refund = await _context.RefundRequests
                        .FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken);
                }

                if (refund == null || IsTerminalState(refund.Status))
                {
                    await lockTx.RollbackAsync(cancellationToken);
                    if (refund != null && refund.Status == RefundStatus.Succeeded)
                    {
                        return RefundExecutionResult.CreateSuccess(refund.RefundTransactionId ?? "ALREADY_SUCCEEDED");
                    }
                    return RefundExecutionResult.CreateFailure("INVALID_STATE", "Lệnh hoàn tiền không tồn tại hoặc đã ở trạng thái kết thúc.");
                }

                // Nếu đang Processing và lock chưa hết hạn -> Worker khác đang xử lý, bỏ qua
                if (refund.Status == RefundStatus.Processing && refund.LockedUntil > now)
                {
                    await lockTx.RollbackAsync(cancellationToken);
                    return RefundExecutionResult.CreateFailure("CONCURRENT_LOCK", "Yêu cầu hoàn tiền đang được xử lý bởi worker khác.");
                }

                refund.Status = RefundStatus.Processing;
                refund.LockedUntil = now.AddSeconds(_options.LockLeaseSeconds);
                refund.UpdatedAt = now;
                await _context.SaveChangesAsync(cancellationToken);
                await lockTx.CommitAsync(cancellationToken);
            }
            else
            {
                refund = await _context.RefundRequests
                    .FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken);

                if (refund == null || IsTerminalState(refund.Status))
                {
                    if (refund != null && refund.Status == RefundStatus.Succeeded)
                    {
                        return RefundExecutionResult.CreateSuccess(refund.RefundTransactionId ?? "ALREADY_SUCCEEDED");
                    }
                    return RefundExecutionResult.CreateFailure("INVALID_STATE", "Lệnh hoàn tiền không tồn tại hoặc đã ở trạng thái kết thúc.");
                }

                if (refund.Status == RefundStatus.Processing && refund.LockedUntil > now)
                {
                    return RefundExecutionResult.CreateFailure("CONCURRENT_LOCK", "Yêu cầu hoàn tiền đang được xử lý bởi worker khác.");
                }

                refund.Status = RefundStatus.Processing;
                refund.LockedUntil = now.AddSeconds(_options.LockLeaseSeconds);
                refund.UpdatedAt = now;
                await _context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation("Bắt đầu xử lý RefundRequest {RefundId} (Attempt={Attempt}, Retry={Retry})",
                refund.Id, refund.AttemptCount + 1, refund.RetryCount);

            // Bước 2: Kiểm tra trạng thái trên cổng thanh toán trước nếu lần thử trước kết quả chưa rõ (Timeout/Network Error)
            if (refund.AttemptCount > 0 && refund.IsRetryable == true)
            {
                try
                {
                    var statusCheck = await _paymentGateway.QueryRefundStatusAsync(
                        refund.RefundTransactionId,
                        refund.OrderCode,
                        cancellationToken);

                    if (statusCheck != null && (statusCheck.Success || statusCheck.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogInformation("Đối soát cổng xác nhận hoàn tiền đã thành công trước đó cho RefundId {RefundId}.", refund.Id);

                        refund.Status = RefundStatus.Succeeded;
                        refund.RefundTransactionId = statusCheck.RefundTransactionId ?? refund.RefundTransactionId ?? $"VERIFIED_{refund.OrderCode}";
                        refund.NextRetryAt = null;
                        refund.LockedUntil = null;
                        refund.UpdatedAt = DateTimeOffset.UtcNow;
                        await _context.SaveChangesAsync(cancellationToken);

                        _orderAuditService.Record(
                            refund.OrderId,
                            "REFUND",
                            refund.Id.ToString(),
                            "REFUND_VERIFIED_SUCCESS",
                            RefundStatus.Processing.ToString(),
                            RefundStatus.Succeeded.ToString(),
                            "SYSTEM",
                            "system:RefundService",
                            null,
                            $"Cổng thanh toán xác nhận lệnh hoàn tiền đã thành công (Mã: {refund.RefundTransactionId}).");

                        await _context.SaveChangesAsync(cancellationToken);
                        return RefundExecutionResult.CreateSuccess(refund.RefundTransactionId);
                    }
                    else if (statusCheck != null && (statusCheck.Status.Equals("PENDING", StringComparison.OrdinalIgnoreCase) ||
                             statusCheck.Status.Equals("PROCESSING", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogInformation("Cổng thanh toán đang xử lý lệnh hoàn tiền trước đó cho RefundId {RefundId}. Giữ lịch đối soát, không gọi hoàn tiền trùng.", refund.Id);
                        refund.RetryCount++;
                        var delay = GetBackoffDelay(refund.RetryCount);
                        refund.NextRetryAt = DateTimeOffset.UtcNow.Add(delay);
                        refund.Status = RefundStatus.RetryScheduled;
                        refund.LockedUntil = null;
                        refund.UpdatedAt = DateTimeOffset.UtcNow;
                        await _context.SaveChangesAsync(cancellationToken);
                        return RefundExecutionResult.CreateFailure("GATEWAY_PENDING", "Cổng thanh toán đang xử lý lệnh hoàn tiền trước đó. Tiếp tục chờ đối soát.", isRetryable: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể truy vấn trạng thái refund từ cổng cho RefundId {RefundId}. Tiếp tục quy trình bình thường.", refund.Id);
                }
            }

            // Bước 3: Thực thi gọi cổng thanh toán
            refund.AttemptCount++;
            refund.LastAttemptedAt = DateTimeOffset.UtcNow;

            var executionRequest = new RefundExecutionRequest
            {
                RefundId = refund.Id,
                OrderId = refund.OrderId,
                OrderCode = refund.OrderCode,
                TransactionId = refund.TransactionId,
                Amount = refund.Amount,
                Currency = refund.Currency,
                IdempotencyKey = refund.IdempotencyKey,
                Reason = refund.Reason
            };

            RefundExecutionResult gatewayResult;
            try
            {
                gatewayResult = await _paymentGateway.ExecuteRefundAsync(executionRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ngoại lệ khi gọi cổng thanh toán thực hiện hoàn tiền cho RefundId {RefundId}", refund.Id);
                gatewayResult = RefundExecutionResult.CreateFailure(
                    "NETWORK_EXCEPTION",
                    SanitizeText(ex.Message),
                    isRetryable: true);
            }

            // Bước 4: Xử lý kết quả trả về từ gateway
            if (gatewayResult.Success)
            {
                // Hoàn tiền thành công
                refund.Status = RefundStatus.Succeeded;
                refund.RefundTransactionId = gatewayResult.RefundTransactionId ?? $"REF_{refund.OrderCode}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                refund.NextRetryAt = null;
                refund.ErrorCode = null;
                refund.ErrorMessage = null;
                refund.IsRetryable = false;
                refund.LockedUntil = null;
                refund.UpdatedAt = DateTimeOffset.UtcNow;

                _orderAuditService.Record(
                    refund.OrderId,
                    "REFUND",
                    refund.Id.ToString(),
                    "REFUND_SUCCEEDED",
                    RefundStatus.Processing.ToString(),
                    RefundStatus.Succeeded.ToString(),
                    "SYSTEM",
                    "system:RefundService",
                    null,
                    $"Hoàn tiền thành công qua cổng thanh toán. Mã giao dịch: {refund.RefundTransactionId}.");

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Hoàn tiền thành công cho RefundId {RefundId}, RefundTransactionId: {RefundTxId}",
                    refund.Id, refund.RefundTransactionId);

                return gatewayResult;
            }
            else
            {
                // Hoàn tiền thất bại
                string sanitizedCode = SanitizeText(gatewayResult.ErrorCode ?? "GATEWAY_ERROR");
                string sanitizedMessage = SanitizeText(gatewayResult.ErrorMessage ?? "Cổng thanh toán từ chối hoàn tiền.");

                refund.ErrorCode = sanitizedCode;
                refund.ErrorMessage = sanitizedMessage;
                refund.FailedAt = DateTimeOffset.UtcNow;
                refund.LockedUntil = null;

                bool isRetryable = gatewayResult.IsRetryable && IsRetryableError(sanitizedCode, sanitizedMessage);
                refund.IsRetryable = isRetryable;

                if (isRetryable && refund.RetryCount < refund.MaxRetries)
                {
                    // Lên lịch retry với exponential backoff (RetryCount tăng từ 1 -> 2 -> 3)
                    refund.RetryCount++;
                    var delay = GetBackoffDelay(refund.RetryCount);
                    refund.NextRetryAt = DateTimeOffset.UtcNow.Add(delay);
                    refund.Status = RefundStatus.RetryScheduled;
                    refund.UpdatedAt = DateTimeOffset.UtcNow;

                    _orderAuditService.Record(
                        refund.OrderId,
                        "REFUND",
                        refund.Id.ToString(),
                        "REFUND_RETRY_SCHEDULED",
                        RefundStatus.Processing.ToString(),
                        RefundStatus.RetryScheduled.ToString(),
                        "SYSTEM",
                        "system:RefundService",
                        null,
                        $"Lần thử {refund.AttemptCount} thất bại ({sanitizedCode}). Đã lên lịch thử lại lần {refund.RetryCount}/{refund.MaxRetries} vào {refund.NextRetryAt:HH:mm:ss dd/MM/yyyy}.");

                    await _context.SaveChangesAsync(cancellationToken);

                    _logger.LogWarning("Hoàn tiền thất bại tạm thời cho RefundId {RefundId}. Lên lịch retry {RetryCount}/{MaxRetries} sau {Delay} giây.",
                        refund.Id, refund.RetryCount, refund.MaxRetries, delay.TotalSeconds);

                    return gatewayResult;
                }
                else
                {
                    // Hết số lần retry hoặc lỗi không retryable -> Chuyển sang ManualReviewRequired và báo kế toán
                    refund.Status = RefundStatus.ManualReviewRequired;
                    refund.NextRetryAt = null;
                    refund.UpdatedAt = DateTimeOffset.UtcNow;

                    string reasonFailure = !isRetryable
                        ? $"Lỗi không thể thử lại ({sanitizedCode}: {sanitizedMessage})."
                        : $"Đã hết {refund.MaxRetries} lần thử lại tối đa nhưng vẫn thất bại.";

                    _orderAuditService.Record(
                        refund.OrderId,
                        "REFUND",
                        refund.Id.ToString(),
                        "REFUND_MANUAL_REVIEW_REQUIRED",
                        RefundStatus.Processing.ToString(),
                        RefundStatus.ManualReviewRequired.ToString(),
                        "SYSTEM",
                        "system:RefundService",
                        null,
                        $"Chuyển sang xử lý thủ công và thông báo cho kế toán. Lý do: {reasonFailure}");

                    await _context.SaveChangesAsync(cancellationToken);

                    _logger.LogError("Hoàn tiền thất bại hoàn toàn cho RefundId {RefundId}. Chuyển sang ManualReviewRequired. Thông báo kế toán...", refund.Id);

                    // Gửi thông báo cho Kế toán
                    await _accountingNotificationService.NotifyFailedRefundAsync(refund, cancellationToken);

                    return gatewayResult;
                }
            }
        }

        public async Task<int> ProcessPendingAndRetryableRefundsAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;

            var candidateIds = await _context.RefundRequests
                .Where(r => (r.Status == RefundStatus.Pending ||
                            (r.Status == RefundStatus.RetryScheduled && r.NextRetryAt <= now) ||
                            (r.Status == RefundStatus.Processing && r.LockedUntil != null && r.LockedUntil <= now)) &&
                            (r.LockedUntil == null || r.LockedUntil <= now))
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.Id)
                .Take(20)
                .ToListAsync(cancellationToken);

            int processedCount = 0;
            foreach (var refundId in candidateIds)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    await ProcessRefundAsync(refundId, cancellationToken);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi xử lý hàng loạt RefundId {RefundId}", refundId);
                }
            }

            return processedCount;
        }

        public async Task<int> RetryFailedAccountingNotificationsAsync(CancellationToken cancellationToken = default)
        {
            var pendingNotificationRefunds = await _context.RefundRequests
                .Where(r => r.Status == RefundStatus.ManualReviewRequired && !r.AccountingNotified)
                .Take(10)
                .ToListAsync(cancellationToken);

            int notifiedCount = 0;
            foreach (var refund in pendingNotificationRefunds)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    bool success = await _accountingNotificationService.NotifyFailedRefundAsync(refund, cancellationToken);
                    if (success)
                    {
                        notifiedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi thử gửi lại thông báo kế toán cho RefundId {RefundId}", refund.Id);
                }
            }

            return notifiedCount;
        }

        public async Task<RefundRequest?> GetRefundByIdAsync(Guid refundId, CancellationToken cancellationToken = default)
        {
            return await _context.RefundRequests
                .Include(r => r.Order)
                .FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken);
        }

        public async Task<List<RefundRequest>> GetRefundsForManualReviewAsync(CancellationToken cancellationToken = default)
        {
            return await _context.RefundRequests
                .Include(r => r.Order)
                .Where(r => r.Status == RefundStatus.ManualReviewRequired)
                .OrderByDescending(r => r.FailedAt)
                .ToListAsync(cancellationToken);
        }

        public async Task<List<RefundRequest>> GetAllRefundsAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return await _context.RefundRequests
                .Include(r => r.Order)
                .OrderByDescending(r => r.CreatedAt)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync(cancellationToken);
        }

        private TimeSpan GetBackoffDelay(int retryCount)
        {
            if (_options.BackoffSeconds != null && _options.BackoffSeconds.Length > 0)
            {
                int index = Math.Clamp(retryCount - 1, 0, _options.BackoffSeconds.Length - 1);
                return TimeSpan.FromSeconds(_options.BackoffSeconds[index]);
            }

            int[] backoffMinutes = _options.BackoffMinutes ?? new[] { 1, 2, 4 };
            int minIndex = Math.Clamp(retryCount - 1, 0, backoffMinutes.Length - 1);
            return TimeSpan.FromMinutes(backoffMinutes[minIndex]);
        }

        private static bool IsRetryableError(string? errorCode, string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorCode)) return true;

            if (NonRetryableErrorCodes.Contains(errorCode))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                foreach (var nonRetryable in NonRetryableErrorCodes)
                {
                    if (errorMessage.Contains(nonRetryable, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsTerminalState(RefundStatus status)
        {
            return status == RefundStatus.Succeeded ||
                   status == RefundStatus.Failed ||
                   status == RefundStatus.ManualReviewRequired;
        }

        private static string SanitizeText(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string sanitized = Regex.Replace(input, @"\b(?:\d[ -]*?){13,19}\b", "****-****-****-****");
            sanitized = Regex.Replace(sanitized, @"(?i)(api[-_]?key|secret|password|token)\s*[:=]\s*[^\s,;]+", "$1=********");
            return sanitized;
        }
    }
}
