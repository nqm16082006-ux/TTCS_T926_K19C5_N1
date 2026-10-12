using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller quản lý và xem lịch sử cảnh báo hệ thống dành riêng cho Admin (Story S-43).
    /// </summary>
    [ApiController]
    [Route("api/system-alerts")]
    [RequireRole("Admin")]
    public class SystemAlertsController : ControllerBase
    {
        private readonly AppDbContext _dbContext;

        public SystemAlertsController(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// Lấy danh sách lịch sử cảnh báo hệ thống gần nhất (chỉ dành cho Admin).
        /// GET /api/system-alerts?limit=50&status=firing
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<List<SystemAlertResponseDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetAlerts(
            [FromQuery] int limit = 50,
            [FromQuery] string? status = null,
            CancellationToken cancellationToken = default)
        {
            // Clamp limit 1..100
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            IQueryable<SystemAlert> query = _dbContext.SystemAlerts.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(status))
            {
                var normalizedStatus = status.Trim().ToLowerInvariant();
                if (normalizedStatus != "firing" && normalizedStatus != "resolved")
                {
                    return BadRequest(ApiResponse<object>.FailureResult("Trạng thái cảnh báo không hợp lệ. Chỉ chấp nhận 'firing' hoặc 'resolved'."));
                }

                query = query.Where(a => a.Status == normalizedStatus);
            }

            var alerts = await query
                .OrderByDescending(a => a.LastUpdatedAt)
                .Take(limit)
                .Select(a => new SystemAlertResponseDto
                {
                    Id = a.Id,
                    Fingerprint = a.Fingerprint,
                    AlertName = a.AlertName,
                    Severity = a.Severity,
                    Summary = a.Summary,
                    Status = a.Status,
                    StartedAt = a.StartedAt,
                    ResolvedAt = a.ResolvedAt,
                    CreatedAt = a.CreatedAt,
                    LastUpdatedAt = a.LastUpdatedAt
                })
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<SystemAlertResponseDto>>.SuccessResult(alerts, "Lấy lịch sử cảnh báo thành công."));
        }
    }
}
