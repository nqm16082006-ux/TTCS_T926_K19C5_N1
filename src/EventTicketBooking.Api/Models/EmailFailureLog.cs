using System;

namespace EventTicketBooking.Api.Models
{
    public class EmailFailureLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OrderId { get; set; }
        public string TargetEmail { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public int Attempts { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool Resolved { get; set; } = false;
    }
}
