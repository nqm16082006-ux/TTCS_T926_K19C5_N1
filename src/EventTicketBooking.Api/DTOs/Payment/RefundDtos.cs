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
