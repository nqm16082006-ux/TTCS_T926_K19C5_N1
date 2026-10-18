using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Monitoring;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// API giám sát hệ thống dành riêng cho Quản trị viên (Story S-43 No-Grafana).
    /// </summary>
    [ApiController]
    [Route("api/admin/monitoring")]
    [RequireRole("Admin")]
    public class AdminMonitoringController : ControllerBase
    {
        private readonly IPrometheusMetricsService _metricsService;
        private readonly AppDbContext _dbContext;

        public AdminMonitoringController(
            IPrometheusMetricsService metricsService,
            AppDbContext dbContext)
        {
            _metricsService = metricsService;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Lấy dữ liệu tóm tắt 5 chỉ số giám sát cốt lõi từ Prometheus local.
        /// GET /api/admin/monitoring/summary
        /// </summary>
        [HttpGet("summary")]
        [ProducesResponseType(typeof(ApiResponse<MonitoringSummaryDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
        {
            var summary = await _metricsService.GetMonitoringSummaryAsync(cancellationToken);
            return Ok(ApiResponse<MonitoringSummaryDto>.SuccessResult(summary, "Lấy dữ liệu giám sát thành công."));
        }

        /// <summary>
        /// Lấy chuỗi dữ liệu thời gian thực (time-series) từ Prometheus query_range phục vụ biểu đồ xu hướng.
        /// GET /api/admin/monitoring/timeseries?minutes=15&stepSeconds=15
        /// </summary>
        [HttpGet("timeseries")]
        [ProducesResponseType(typeof(ApiResponse<MonitoringTimeSeriesDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTimeSeries(
            [FromQuery] int minutes = 15,
            [FromQuery] int stepSeconds = 15,
            CancellationToken cancellationToken = default)
        {
            var timeSeries = await _metricsService.GetMonitoringTimeSeriesAsync(minutes, stepSeconds, cancellationToken);
            return Ok(ApiResponse<MonitoringTimeSeriesDto>.SuccessResult(timeSeries, "Lấy dữ liệu chuỗi thời gian thành công."));
        }

        /// <summary>
        /// Lấy lịch sử cảnh báo hệ thống từ cơ sở dữ liệu kèm bộ lọc.
        /// GET /api/admin/monitoring/alerts?limit=50&status=firing&severity=critical&search=5xx
        /// </summary>
        [HttpGet("alerts")]
        [ProducesResponseType(typeof(ApiResponse<List<SystemAlertResponseDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetAlerts(
            [FromQuery] int limit = 50,
            [FromQuery] string? status = null,
            [FromQuery] string? severity = null,
            [FromQuery] string? search = null,
            CancellationToken cancellationToken = default)
        {
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

            if (!string.IsNullOrWhiteSpace(severity))
            {
                var normalizedSeverity = severity.Trim().ToLowerInvariant();
                if (normalizedSeverity != "warning" && normalizedSeverity != "critical")
                {
                    return BadRequest(ApiResponse<object>.FailureResult("Mức độ cảnh báo không hợp lệ. Chỉ chấp nhận 'warning' hoặc 'critical'."));
                }
                query = query.Where(a => a.Severity == normalizedSeverity);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(a => EF.Functions.Like(a.AlertName, $"%{term}%") || EF.Functions.Like(a.Summary, $"%{term}%"));
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
                    LastUpdatedAt = a.LastUpdatedAt,
                    Source = "job=event-ticket-api, instance=host.docker.internal:5012"
                })
                .ToListAsync(cancellationToken);

            return Ok(ApiResponse<List<SystemAlertResponseDto>>.SuccessResult(alerts, "Lấy lịch sử cảnh báo thành công."));
        }
    }
}
