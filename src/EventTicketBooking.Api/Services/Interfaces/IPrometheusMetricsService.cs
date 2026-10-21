using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Monitoring;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service truy vấn chỉ số thời gian thực từ Prometheus HTTP API (Story S-43 No-Grafana).
    /// </summary>
    public interface IPrometheusMetricsService
    {
        Task<MonitoringSummaryDto> GetMonitoringSummaryAsync(CancellationToken cancellationToken = default);

        Task<MonitoringTimeSeriesDto> GetMonitoringTimeSeriesAsync(
            int minutes = 15,
            int stepSeconds = 15,
            CancellationToken cancellationToken = default);
    }
}
