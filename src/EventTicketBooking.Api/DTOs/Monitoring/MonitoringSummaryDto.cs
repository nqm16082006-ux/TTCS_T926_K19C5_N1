using System;

namespace EventTicketBooking.Api.DTOs.Monitoring
{
    /// <summary>
    /// DTO tổng hợp chỉ số giám sát hệ thống thời gian thực từ PostgreSQL (Story S-43 No-Grafana Shared Real Monitoring).
    /// </summary>
    public class MonitoringSummaryDto
    {
        /// <summary>Tổng số HTTP request trong khoảng thời gian</summary>
        public long RequestCount { get; set; }

        /// <summary>Tổng số HTTP request bị lỗi (Status >= 500)</summary>
        public long ErrorCount { get; set; }

        /// <summary>Tỉ lệ lỗi HTTP 5xx (%) trên tổng lưu lượng</summary>
        public double? ErrorRatePercent { get; set; }

        /// <summary>Thời gian phản hồi phân vị 95 (P95) tính bằng mili-giây (ms)</summary>
        public double? P95Milliseconds { get; set; }

        /// <summary>Tổng số tranh chấp giữ ghế (seat hold conflicts) trong khoảng thời gian</summary>
        public long SeatConflictCount { get; set; }

        /// <summary>Tổng số webhook thanh toán bị từ chối trong khoảng thời gian</summary>
        public long RejectedWebhookCount { get; set; }

        /// <summary>Số lượng cảnh báo đang hoạt động (Active Alerts)</summary>
        public int ActiveAlertCount { get; set; }

        /// <summary>Thời điểm bắt đầu cửa sổ thời gian (UTC)</summary>
        public DateTimeOffset WindowStartUtc { get; set; }

        /// <summary>Thời điểm kết thúc cửa sổ thời gian (UTC)</summary>
        public DateTimeOffset WindowEndUtc { get; set; }

        /// <summary>Thời điểm tạo dữ liệu tóm tắt (UTC)</summary>
        public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        #region Backwards Compatibility with Legacy Dashboard

        /// <summary>Trạng thái kết nối đến hệ thống giám sát</summary>
        public bool PrometheusAvailable { get; set; } = true;

        public DateTimeOffset GeneratedAt
        {
            get => GeneratedAtUtc;
            set => GeneratedAtUtc = value;
        }

        public double? RequestRate { get; set; }

        public double? P95ResponseTimeMs { get; set; }

        public double? SeatConflicts { get; set; }

        public double? RejectedWebhooks { get; set; }

        public string? Message { get; set; }

        #endregion
    }
}
