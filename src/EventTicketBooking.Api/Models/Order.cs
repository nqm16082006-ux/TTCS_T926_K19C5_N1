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
        Expired
    }

    public class Order
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public Guid ShowtimeId { get; set; }
        public OrderStatus Status { get; set; } = OrderStatus.Pending;
        public int TotalAmount { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public User User { get; set; } = null!;
        public Showtime Showtime { get; set; } = null!;
        public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
        public PaymentTransaction? PaymentTransaction { get; set; }

        public void CalculateTotal()
        {
            TotalAmount = OrderItems?.Sum(oi => oi.Price) ?? 0;
        }
    }
}
