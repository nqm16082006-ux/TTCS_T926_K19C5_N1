using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi lưu trữ lịch sử cảnh báo hệ thống từ Alertmanager (Story S-43).
    /// </summary>
    public class SystemAlert
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Fingerprint định danh cảnh báo từ Alertmanager</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Tên cảnh báo (alertname, ví dụ Http5xxErrorRateWarning)</summary>
        public string AlertName { get; set; } = string.Empty;

        /// <summary>Mức độ cảnh báo (warning, critical)</summary>
        public string Severity { get; set; } = "warning";

        /// <summary>Nội dung tóm tắt cảnh báo</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>Trạng thái hiện tại: firing hoặc resolved</summary>
        public string Status { get; set; } = "firing";

        /// <summary>Thời điểm bắt đầu cảnh báo (StartsAt từ Alertmanager hoặc UtcNow)</summary>
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Thời điểm cảnh báo được khắc phục (EndsAt từ Alertmanager hoặc UtcNow)</summary>
        public DateTimeOffset? ResolvedAt { get; set; }

        /// <summary>Thời điểm tạo bản ghi trong hệ thống</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Thời điểm cập nhật lần cuối</summary>
        public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
