using System;
using System.Collections.Generic;
using System.Linq;

namespace EventTicketBooking.Api.Models
{
    public enum OrderStatus
    {
        Pending,
        Paid,
        Cancelled,
        Expired,
        NeedsAttention
    }

    public class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public Guid ShowtimeId { get; set; }
        public OrderStatus Status { get; set; } = OrderStatus.Pending;
        public int TotalAmount { get; set; }
        public int OriginalAmount { get; set; }
        public int DiscountAmount { get; set; }
        public Guid? DiscountCodeId { get; set; }
        public string? DiscountCodeSnapshot { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Lý do huỷ đơn (Story S-45): PAYMENT_CANCELLED (huỷ trong luồng thanh toán)
        /// hoặc CUSTOMER_REFUND (khách chủ động huỷ sau thanh toán). Null khi chưa huỷ.
        /// </summary>
        public string? CancelReason { get; set; }

        public User User { get; set; } = null!;
        public Showtime Showtime { get; set; } = null!;
        public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
        public PaymentTransaction? PaymentTransaction { get; set; }
        public ICollection<Refund> Refunds { get; set; } = new List<Refund>();
        public DiscountCode? DiscountCode { get; set; }

        public void CalculateTotal()
        {
            if (OriginalAmount == 0 && OrderItems != null && OrderItems.Count > 0)
            {
                OriginalAmount = OrderItems.Sum(oi => oi.Price);
            }
            TotalAmount = Math.Max(0, OriginalAmount - DiscountAmount);
        }
    }
}
