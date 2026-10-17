using System;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Story S-52: Dịch vụ thống kê soát vé thời gian thực theo suất diễn cho Ban tổ chức.
    /// </summary>
    public interface ICheckinStatisticsService
    {
        Task<ShowtimeLiveCheckinDto> GetLiveCheckinStatsAsync(Guid showtimeId, CancellationToken cancellationToken = default);
    }
}
