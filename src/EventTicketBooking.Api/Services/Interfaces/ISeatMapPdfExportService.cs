using System;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface ISeatMapPdfExportService
    {
        Task<SeatMapExportDataDto> GetSeatMapDataAsync(Guid showtimeId, DateTime? nowOverride = null, CancellationToken cancellationToken = default);

        byte[] GenerateSeatMapPdf(SeatMapExportDataDto data);

        Task<byte[]> ExportSeatMapPdfAsync(Guid showtimeId, DateTime? nowOverride = null, CancellationToken cancellationToken = default);
    }
}
