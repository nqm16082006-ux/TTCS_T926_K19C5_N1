using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi lưu vết lưu lượng HTTP request vào PostgreSQL (Story S-43 Shared Real Monitoring).
    /// Tuyệt đối không lưu secret, query string nhạy cảm, JWT, cookie, payment payload hoặc PII.
    /// </summary>
    public class RequestTelemetry
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public string Method { get; set; } = "GET";

        /// <summary>
        /// Normalized route template (ví dụ: GET /api/v1/orders/{id}). Không chứa query string hay secret.
        /// </summary>
        public string Route { get; set; } = string.Empty;

        public int StatusCode { get; set; }

        public double DurationMs { get; set; }

        public string Environment { get; set; } = "Development";

        public string? InstanceId { get; set; }
    }
}
