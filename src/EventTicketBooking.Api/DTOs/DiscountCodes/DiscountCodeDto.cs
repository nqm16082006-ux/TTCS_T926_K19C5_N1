using System;

namespace EventTicketBooking.Api.DTOs.DiscountCodes
{
    public class DiscountCodeDto
    {
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string DiscountType { get; set; } = string.Empty;
        public int DiscountValue { get; set; }
        public int UsageLimit { get; set; }
        public int UsedCount { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public bool IsActive { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
