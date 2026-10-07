using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi giao dịch thanh toán cho Đơn hàng (Task T-41).
    /// Đảm bảo mỗi đơn hàng chỉ gắn với duy nhất 1 giao dịch và 1 mã tham chiếu thanh toán (Idempotency).
    /// </summary>
    public class PaymentTransaction
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OrderId { get; set; }
        public long OrderCode { get; set; }
        public int Amount { get; set; }
        public string? PaymentUrl { get; set; }
        public string? TransactionId { get; set; }
        public string Status { get; set; } = "PENDING"; // PENDING, PAID, CANCELLED, FAILED
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Order Order { get; set; } = null!;
    }
}
