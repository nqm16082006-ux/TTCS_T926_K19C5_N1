using System;

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
        public string? FailureReason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
