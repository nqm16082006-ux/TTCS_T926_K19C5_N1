using System;

namespace EventTicketBooking.Api.DTOs.Monitoring
{
    public class MonitoringStatusDto
    {
        public bool IsHealthy { get; set; } = true;
        public string StorageType { get; set; } = "PostgreSQL";
        public long TotalPersistedRequests { get; set; }
        public long TotalPersistedEvents { get; set; }
        public int ActiveAlertsCount { get; set; }
        public long DroppedRequestsCount { get; set; }
        public long DroppedEventsCount { get; set; }
        public DateTimeOffset CheckedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
