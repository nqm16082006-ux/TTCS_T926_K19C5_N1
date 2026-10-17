using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs.Monitoring
{
    /// <summary>
    /// Điểm dữ liệu chuỗi thời gian thực từ Prometheus (S-43 Real Monitoring).
    /// </summary>
    public sealed class MetricDataPointDto
    {
        public double Timestamp { get; set; }
        public double? Value { get; set; }
    }

    /// <summary>
    /// DTO dữ liệu chuỗi thời gian thực từ Prometheus phục vụ biểu đồ (Story S-43 No-Grafana).
    /// </summary>
    public sealed class MonitoringTimeSeriesDto
    {
        public bool PrometheusAvailable { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public List<MetricDataPointDto> RequestRateSeries { get; set; } = new();
        public List<MetricDataPointDto> ErrorRateSeries { get; set; } = new();
        public List<MetricDataPointDto> P95LatencySeries { get; set; } = new();
        public string? Message { get; set; }
    }
}
