using System;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.DTOs.Payment
{
    /// <summary>
    /// Yêu cầu thực thi lệnh hoàn tiền gửi tới cổng thanh toán.
    /// </summary>
    public class RefundExecutionRequest
    {
        public Guid RefundId { get; set; }
        public Guid OrderId { get; set; }
        public long OrderCode { get; set; }
        public string? TransactionId { get; set; }
        public int Amount { get; set; }
        public string Currency { get; set; } = "VND";
        public string IdempotencyKey { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Kết quả thực thi lệnh hoàn tiền từ cổng thanh toán.
    /// </summary>
    public class RefundExecutionResult
    {
        public bool Success { get; set; }
        public string? RefundTransactionId { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsRetryable { get; set; }

        public static RefundExecutionResult CreateSuccess(string refundTransactionId) =>
            new()
            {
                Success = true,
                RefundTransactionId = refundTransactionId,
                IsRetryable = false
            };

        public static RefundExecutionResult CreateFailure(
            string errorCode,
            string errorMessage,
            bool isRetryable = false,
            string? refundTransactionId = null) =>
            new()
            {
                Success = false,
                RefundTransactionId = refundTransactionId,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                IsRetryable = isRetryable
            };
    }

    /// <summary>
    /// Kết quả truy vấn trạng thái hoàn tiền trực tiếp từ cổng thanh toán.
    /// </summary>
    public class RefundStatusDto
    {
        public bool Success { get; set; }
        public string? RefundTransactionId { get; set; }
        public string Status { get; set; } = "PENDING"; // SUCCESS, FAILED, PENDING
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// DTO yêu cầu tạo lệnh hoàn tiền thủ công / qua API.
    /// </summary>
    public class CreateRefundDto
    {
        public int? Amount { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Thông tin lịch sử một lần thử hoàn tiền để Kế toán theo dõi chi tiết (Story S-48).
    /// </summary>
    public class RefundAttemptHistoryDto
    {
        public int AttemptNumber { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? ErrorCode { get; set; }
        public string? Notes { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }

    /// <summary>
    /// DTO trả về thông tin chi tiết lệnh hoàn tiền cho Kế toán / Quản trị viên.
    /// </summary>
    public class RefundDetailDto
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
        public long OrderCode { get; set; }
        public string? TransactionId { get; set; }
        public string? RefundTransactionId { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public int Amount { get; set; }
        public string Currency { get; set; } = "VND";
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int AttemptCount { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; }
        public DateTimeOffset? LastAttemptedAt { get; set; }
        public DateTimeOffset? NextRetryAt { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public bool? IsRetryable { get; set; }
        public bool AccountingNotified { get; set; }
        public DateTimeOffset? AccountingNotifiedAt { get; set; }
        public string? AccountingNotificationError { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>
        /// Lịch sử chi tiết các lần thử hoàn tiền phục vụ đối soát của Kế toán.
        /// </summary>
        public List<RefundAttemptHistoryDto> AttemptHistory { get; set; } = new();
    }

    /// <summary>
    /// DTO khi kế toán hoàn tất xử lý hoàn tiền thủ công cho khách hàng.
    /// </summary>
    public class ResolveAccountingRefundDto
    {
        /// <summary>Mã tham chiếu chuyển tiền qua ngân hàng hoặc giao dịch thủ công.</summary>
        public string? ManualTransactionCode { get; set; }
        /// <summary>Ghi chú của kế toán khi xử lý.</summary>
        public string? Notes { get; set; }
    }

    /// <summary>
    /// DTO giả lập kịch bản hoàn tiền cho UI test (Story S-48).
    /// </summary>
    public class SimulateRefundDto
    {
        public Guid? OrderId { get; set; }
        /// <summary>
        /// Chế độ giả lập: "FailRetry", "FailPermanent", hoặc "Success".
        /// </summary>
        public string Mode { get; set; } = "FailRetry";
        public int? Amount { get; set; }
        public bool AutoProcess { get; set; } = true;
    }
}
