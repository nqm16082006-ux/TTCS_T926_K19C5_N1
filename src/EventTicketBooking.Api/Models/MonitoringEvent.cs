using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi lưu trữ các sự kiện nghiệp vụ giám sát hệ thống (Story S-43: SeatConflict, WebhookRejected).
    /// </summary>
    public class MonitoringEvent
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Loại sự kiện: SeatConflict, WebhookRejected,...
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Phân loại chi tiết (ví dụ: redis, db_active, db_constraint, payos)
        /// </summary>
        public string? Category { get; set; }

        /// <summary>
        /// Mã định danh tham chiếu an toàn (ví dụ: ShowtimeId, reason code). Không lưu PII/secret.
        /// </summary>
        public string? ReferenceId { get; set; }

        public string Environment { get; set; } = "Development";
    }
}
