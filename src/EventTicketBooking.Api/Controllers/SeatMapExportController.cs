using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Story S-60 (Jira TTKN-127): Xuất sơ đồ ghế đã bán dạng PDF.
    /// Gói gọn trong đúng 1 trang A4 Landscape, hiển thị trạng thái thực tế tại thời điểm xuất (SOLD, HELD, AVAILABLE),
    /// thanh định hướng Sân khấu, lưới ghế và Chú giải & Thống kê.
    /// </summary>
    [ApiController]
    [RequireRole("Admin", "Organizer", "Staff")]
    public class SeatMapExportController : ControllerBase
    {
        private readonly ISeatMapPdfExportService _seatMapPdfExportService;
        private readonly AppDbContext _context;
        private readonly ILogger<SeatMapExportController> _logger;

        public SeatMapExportController(
            ISeatMapPdfExportService seatMapPdfExportService,
            AppDbContext context,
            ILogger<SeatMapExportController> logger)
        {
            _seatMapPdfExportService = seatMapPdfExportService ?? throw new ArgumentNullException(nameof(seatMapPdfExportService));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Xuất sơ đồ ghế của suất diễn dạng file PDF 1 trang.
        /// Route 1: GET /api/v1/showtimes/{showtimeId}/seats/export-pdf
        /// Route 2: GET /api/shows/{showtimeId}/seats/export-pdf
        /// Route 3: GET /api/showtimes/{showtimeId}/export-seatmap-pdf
        /// </summary>
        [HttpGet("api/v1/showtimes/{showtimeId:guid}/seats/export-pdf")]
        [HttpGet("api/shows/{showtimeId:guid}/seats/export-pdf")]
        [HttpGet("api/showtimes/{showtimeId:guid}/export-seatmap-pdf")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ExportSeatMapPdf(
            [FromRoute] Guid showtimeId,
            [FromQuery] DateTime? nowOverride = null,
            CancellationToken cancellationToken = default)
        {
            if (showtimeId == Guid.Empty)
            {
                return BadRequest(new { message = "Mã suất diễn không hợp lệ." });
            }

            var showtime = await _context.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null)
            {
                return NotFound(new { message = $"Không tìm thấy suất diễn với mã {showtimeId}." });
            }

            // Kiểm tra phân quyền: Nếu không phải Admin thì chỉ có Organizer là chủ sự kiện mới được xuất
            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                var currentUserId = GetCurrentUserId();
                if (!currentUserId.HasValue || showtime.Event?.OwnerId != currentUserId.Value)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền truy cập sơ đồ ghế của suất diễn này." });
                }
            }

            try
            {
                var pdfBytes = await _seatMapPdfExportService.ExportSeatMapPdfAsync(showtimeId, nowOverride, cancellationToken);

                var sanitizedEventTitle = string.Join("_", (showtime.Event?.Title ?? "Event").Split(System.IO.Path.GetInvalidFileNameChars())).Replace(" ", "_");
                var fileName = $"so-do-ghe-{sanitizedEventTitle}-{showtimeId.ToString().Substring(0, 8)}.pdf";

                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra khi xuất PDF sơ đồ ghế cho suất diễn {ShowtimeId}.", showtimeId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Đã xảy ra lỗi khi tạo tệp PDF sơ đồ ghế.", detail = ex.Message });
            }
        }

        /// <summary>
        /// Lấy dữ liệu thống kê trạng thái sơ đồ ghế (Real-time JSON metadata).
        /// Route: GET /api/v1/showtimes/{showtimeId}/seats/export-data
        /// Route: GET /api/shows/{showtimeId}/seats/export-data
        /// </summary>
        [HttpGet("api/v1/showtimes/{showtimeId:guid}/seats/export-data")]
        [HttpGet("api/shows/{showtimeId:guid}/seats/export-data")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSeatMapData(
            [FromRoute] Guid showtimeId,
            [FromQuery] DateTime? nowOverride = null,
            CancellationToken cancellationToken = default)
        {
            if (showtimeId == Guid.Empty)
            {
                return BadRequest(new { message = "Mã suất diễn không hợp lệ." });
            }

            var showtime = await _context.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null)
            {
                return NotFound(new { message = $"Không tìm thấy suất diễn với mã {showtimeId}." });
            }

            if (!User.IsInRole("Admin") && !User.IsInRole("Staff"))
            {
                var currentUserId = GetCurrentUserId();
                if (!currentUserId.HasValue || showtime.Event?.OwnerId != currentUserId.Value)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new { message = "Bạn không có quyền truy cập sơ đồ ghế của suất diễn này." });
                }
            }

            try
            {
                var data = await _seatMapPdfExportService.GetSeatMapDataAsync(showtimeId, nowOverride, cancellationToken);
                return Ok(data);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        private Guid? GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? User.FindFirst("id")?.Value
                              ?? User.FindFirst("sub")?.Value;

            if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
            {
                return userId;
            }
            return null;
        }
    }
}
