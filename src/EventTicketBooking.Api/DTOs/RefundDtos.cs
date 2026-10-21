using System;
using System.Text.Json.Serialization;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// Kết quả xem trước điều kiện huỷ và số tiền hoàn (Story S-45).
    /// Server luôn tự tính; client không bao giờ tự quyết định số tiền.
    /// </summary>
    public class RefundPreviewDto
    {
        public Guid OrderId { get; set; }
        public bool Eligible { get; set; }
        public string Reason { get; set; } = "OK";
        public string Message { get; set; } = string.Empty;
        public int TotalAmount { get; set; }
        public int EstimatedRefundAmount { get; set; }
        public int CutoffHours { get; set; }
        public int RefundPercent { get; set; }
        public DateTime? ShowtimeStartTime { get; set; }
        public DateTimeOffset? RefundDeadline { get; set; }
        public string PolicyNote { get; set; } = "Chính sách demo: huỷ trước 24 giờ và hoàn 90% — chưa được Product phê duyệt chính thức.";
    }

    /// <summary>
    /// Thông tin yêu cầu hoàn tiền trả về cho khách hàng (Story S-45).
    /// </summary>
    public class RefundDto
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
        public int Amount { get; set; }
        public string Status { get; set; } = "Requested";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FailureReason { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RetryCount { get; set; }
    }

    /// <summary>
    /// Kết quả thực thi xử lý hoàn tiền của RefundService (Story S-47 & S-45).
    /// </summary>
    public class RefundExecutionResult
    {
        public Guid OrderId { get; set; }
        public Guid? RefundId { get; set; }
        public int Amount { get; set; }
        public string Status { get; set; } = "Requested";
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public string? FailureReason { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RetryCount { get; set; }
        public bool IsManualReview { get; set; }

        public static RefundExecutionResult CreateSuccess(Guid orderId, Guid refundId, int amount, string message) => new()
        {
            OrderId = orderId,
            RefundId = refundId,
            Amount = amount,
            Status = "Succeeded",
            Success = true,
            Message = message
        };

        public static RefundExecutionResult CreateManualPending(Guid orderId, Guid refundId, int amount, string? reason, string message) => new()
        {
            OrderId = orderId,
            RefundId = refundId,
            Amount = amount,
            Status = "ManualPending",
            Success = true,
            FailureReason = reason,
            Message = message
        };

        public static RefundExecutionResult CreateFailed(Guid orderId, Guid? refundId, int amount, string failureReason, string message) => new()
        {
            OrderId = orderId,
            RefundId = refundId,
            Amount = amount,
            Status = "Failed",
            Success = false,
            FailureReason = failureReason,
            Message = message
        };

        public static RefundExecutionResult CreateSkipped(Guid orderId, Guid? refundId, string message) => new()
        {
            OrderId = orderId,
            RefundId = refundId,
            Status = "Skipped",
            Skipped = true,
            Success = true,
            Message = message
        };
    }
}
