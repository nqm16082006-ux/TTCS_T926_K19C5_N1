using System;

namespace EventTicketBooking.Api.Models
{
    public class DiscountCode
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid EventId { get; set; }
        public string Code { get; set; } = string.Empty;
        public DiscountType DiscountType { get; set; }
        public int DiscountValue { get; set; }
        public int UsageLimit { get; set; }
        public int UsedCount { get; set; } = 0;
        public DateTimeOffset ExpiresAt { get; set; }
        public bool IsActive { get; set; } = true;
        public Guid CreatedByUserId { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        // Navigations
        public Event Event { get; set; } = null!;
        public User CreatedByUser { get; set; } = null!;
    }
}
