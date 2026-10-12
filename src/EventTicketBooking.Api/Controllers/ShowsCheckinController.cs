using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller phục vụ quản lý và xuất danh sách soát vé theo suất diễn (Story S-62).
    /// </summary>
    [ApiController]
    [Route("api/shows/{showId}/checkins")]
    [RequireRole("Admin", "Staff", "Organizer")]
    public class ShowsCheckinController : ControllerBase
    {
        private readonly ICheckinExportService _checkinExportService;
        private readonly ILogger<ShowsCheckinController> _logger;

        public ShowsCheckinController(
            ICheckinExportService checkinExportService,
            ILogger<ShowsCheckinController> logger)
        {
            _checkinExportService = checkinExportService ?? throw new ArgumentNullException(nameof(checkinExportService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Story S-62: Xuất danh sách khách vào cửa theo suất diễn ra tệp CSV chuẩn UTF-8 có BOM.
        /// Cho phép lọc theo GateName / DoorName / GateId / DoorId và OnlyConflicts.
        /// Trả về file download với filename: danh-sach-checkin-suat-{showId}.csv.
        /// </summary>
        [HttpGet("export-csv")]
        public async Task<IActionResult> ExportCheckinListCsv(
            [FromRoute] Guid showId,
            [FromQuery] string? gateName = null,
            [FromQuery] string? doorName = null,
            [FromQuery] string? gateId = null,
            [FromQuery] string? doorId = null,
            [FromQuery] bool? onlyConflicts = null,
            [FromQuery] bool includeNotCheckedIn = false,
            CancellationToken cancellationToken = default)
        {
            if (showId == Guid.Empty)
            {
                return BadRequest(new { Message = "Mã suất diễn không hợp lệ." });
            }

            var query = new ExportCheckinListQuery
            {
                ShowId = showId,
                GateName = gateName,
                DoorName = doorName,
                GateId = gateId,
                DoorId = doorId,
                OnlyConflicts = onlyConflicts,
                IncludeNotCheckedIn = includeNotCheckedIn
            };

            try
            {
                var csvBytes = await _checkinExportService.ExportCheckinCsvAsync(query, cancellationToken);
                var filename = $"danh-sach-checkin-suat-{showId}.csv";
                return File(csvBytes, "text/csv; charset=utf-8", filename);
            }
            catch (System.Collections.Generic.KeyNotFoundException ex)
            {
                return NotFound(new { Message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xuất danh sách soát vé cho suất diễn {ShowId}", showId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Đã xảy ra lỗi khi tạo tệp CSV xuất danh sách soát vé." });
            }
        }
    }
}
