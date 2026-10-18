using System;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO trả về thông tin cảnh báo hệ thống cho Admin API (Story S-43).
    /// </summary>
    public class SystemAlertResponseDto
    {
        public Guid Id { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public string AlertName { get; set; } = string.Empty;
        public string Severity { get; set; } = "warning";
        public string Summary { get; set; } = string.Empty;
        public string Status { get; set; } = "firing";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }

        /// <summary>Nguồn phát cảnh báo (Alertmanager source / job / instance)</summary>
        public string Source { get; set; } = "job=event-ticket-api, instance=host.docker.internal:5012";
    }
}
