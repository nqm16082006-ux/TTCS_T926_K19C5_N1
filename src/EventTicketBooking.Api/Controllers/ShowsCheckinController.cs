using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services;
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

            // Story S-02: Kiểm tra Event Ownership cho vai trò Organizer
            if (User.IsInRole("Organizer") && !User.IsInRole("Admin"))
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                  ?? User.FindFirst("id")?.Value
                                  ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

                if (!Guid.TryParse(userIdClaim, out var currentUserId))
                {
                    return Unauthorized(new { Message = "Vui lòng đăng nhập để tiếp tục." });
                }

                var db = HttpContext.RequestServices.GetRequiredService<EventTicketBooking.Api.Data.AppDbContext>();
                var showtime = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                    db.Showtimes.Include(s => s.Event), s => s.Id == showId, cancellationToken);

                if (showtime == null)
                {
                    return NotFound(new { Message = $"Không tìm thấy suất diễn với ID {showId}." });
                }

                if (showtime.Event == null || showtime.Event.OwnerId != currentUserId)
                {
                    SecurityAuditLogger.LogOwnershipDenied(HttpContext, "ShowsCheckinExport", showId.ToString());
                    return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Bạn không có quyền xuất dữ liệu soát vé của sự kiện này." });
                }
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
