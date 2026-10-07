using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi mọi webhook đã nhận từ cổng thanh toán (Task T-45).
    /// Mã giao dịch từ cổng (TransactionId) là khóa unique chống xử lý trùng.
    /// Nguồn sự thật cho đối soát S-51.
    /// </summary>
    public class PaymentEvent
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string TransactionId { get; set; } = string.Empty;
        public string RawPayload { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
