using System;

namespace EventTicketBooking.Api.DTOs.Monitoring
{
    /// <summary>
    /// DTO tổng hợp chỉ số giám sát hệ thống thời gian thực (Story S-43 No-Grafana).
    /// </summary>
    public class MonitoringSummaryDto
    {
        /// <summary>Trạng thái kết nối đến Prometheus local (true: khả dụng, false: không kết nối được)</summary>
        public bool PrometheusAvailable { get; set; }

        /// <summary>Thời điểm tạo dữ liệu tóm tắt</summary>
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Lưu lượng HTTP request/s hiện tại (sum rate 1m)</summary>
        public double? RequestRate { get; set; }

        /// <summary>Tỉ lệ lỗi HTTP 5xx (%) trên tổng lưu lượng</summary>
        public double? ErrorRatePercent { get; set; }

        /// <summary>Thời gian phản hồi phân vị 95 (p95) tính bằng mili-giây (ms)</summary>
        public double? P95ResponseTimeMs { get; set; }

        /// <summary>Tổng số tranh chấp giữ ghế (seat hold conflicts) trong 5 phút qua</summary>
        public double? SeatConflicts { get; set; }

        /// <summary>Tổng số webhook thanh toán bị từ chối trong 5 phút qua</summary>
        public double? RejectedWebhooks { get; set; }

        /// <summary>Thông báo trạng thái hoặc hướng dẫn khi Prometheus không khả dụng</summary>
        public string? Message { get; set; }
    }
}
