using System;

namespace EventTicketBooking.Api.DTOs
{
    public class OrderAuditLogDto
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public string? EntityId { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? OldStatus { get; set; }
        public string NewStatus { get; set; } = string.Empty;
        public string ActorType { get; set; } = string.Empty;
        public string Actor { get; set; } = string.Empty;
        public Guid? ActorUserId { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string? Note { get; set; }
    }
}
