using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Monitoring;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface IMonitoringService
    {
        Task<MonitoringSummaryDto> GetSummaryAsync(string window = "15m", CancellationToken cancellationToken = default);
        Task<MonitoringTimeSeriesDto> GetTimeSeriesAsync(string window = "1h", int stepSeconds = 60, CancellationToken cancellationToken = default);
        Task<List<MonitoringAlert>> GetAlertsAsync(bool? isActive = null, int limit = 50, CancellationToken cancellationToken = default);
        Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    }
}
