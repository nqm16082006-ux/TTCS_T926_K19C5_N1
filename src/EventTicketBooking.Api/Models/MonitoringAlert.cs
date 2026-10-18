using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi cảnh báo hệ thống được đánh giá tự động dựa trên metrics trong PostgreSQL (Story S-43).
    /// </summary>
    public class MonitoringAlert
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Chỉ số giám sát kích hoạt cảnh báo: ErrorRatePercent, P95Milliseconds, SeatConflicts15m, RejectedWebhooks15m,...
        /// </summary>
        public string Metric { get; set; } = string.Empty;

        /// <summary>
        /// Mức độ cảnh báo: Warning, Critical
        /// </summary>
        public string Severity { get; set; } = "Warning";

        /// <summary>
        /// Ngưỡng cấu hình kích hoạt cảnh báo
        /// </summary>
        public double Threshold { get; set; }

        /// <summary>
        /// Giá trị thực tế đo được tại thời điểm cảnh báo
        /// </summary>
        public double ObservedValue { get; set; }

        /// <summary>
        /// Khoảng thời gian cửa sổ đánh giá (phút)
        /// </summary>
        public int WindowMinutes { get; set; }

        public DateTimeOffset FirstTriggeredAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastTriggeredAtUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Cảnh báo đang hoạt động (true) hay đã khắc phục (false)
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Thời điểm cảnh báo trở về bình thường và được giải quyết
        /// </summary>
        public DateTimeOffset? ResolvedAtUtc { get; set; }
    }
}
