using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Monitoring;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// API giám sát hệ thống thời gian thực dựa trên dữ liệu PostgreSQL chia sẻ (Story S-43 No-Grafana).
    /// Dành riêng cho Quản trị viên (Admin). Customer truy cập sẽ nhận HTTP 403 Forbidden.
    /// </summary>
    [ApiController]
    [Route("api/v1/monitoring")]
    [RequireRole("Admin")]
    public class MonitoringController : ControllerBase
    {
        private readonly IMonitoringService _monitoringService;

        public MonitoringController(IMonitoringService monitoringService)
        {
            _monitoringService = monitoringService;
        }

        /// <summary>
        /// Lấy dữ liệu tổng hợp 5 chỉ số cốt lõi từ bảng PostgreSQL thật.
        /// GET /api/v1/monitoring/summary?window=15m
        /// </summary>
        [HttpGet("summary")]
        [ProducesResponseType(typeof(ApiResponse<MonitoringSummaryDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSummary(
            [FromQuery] string window = "15m",
            CancellationToken cancellationToken = default)
        {
            var summary = await _monitoringService.GetSummaryAsync(window, cancellationToken);
            return Ok(ApiResponse<MonitoringSummaryDto>.SuccessResult(summary, "Lấy dữ liệu giám sát thành công."));
        }

        /// <summary>
        /// Lấy chuỗi dữ liệu thời gian thực (time-series) theo cửa sổ và bước nhảy phục vụ biểu đồ.
        /// GET /api/v1/monitoring/timeseries?window=1h&stepSeconds=60
        /// </summary>
        [HttpGet("timeseries")]
        [ProducesResponseType(typeof(ApiResponse<MonitoringTimeSeriesDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTimeSeries(
            [FromQuery] string window = "1h",
            [FromQuery] int stepSeconds = 60,
            CancellationToken cancellationToken = default)
        {
            var timeSeries = await _monitoringService.GetTimeSeriesAsync(window, stepSeconds, cancellationToken);
            return Ok(ApiResponse<MonitoringTimeSeriesDto>.SuccessResult(timeSeries, "Lấy chuỗi thời gian thành công."));
        }

        /// <summary>
        /// Lấy danh sách cảnh báo hệ thống (MonitoringAlert) đã được đánh giá tự động và lưu trong PostgreSQL.
        /// GET /api/v1/monitoring/alerts?isActive=true&limit=50
        /// </summary>
        [HttpGet("alerts")]
        [ProducesResponseType(typeof(ApiResponse<List<MonitoringAlert>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAlerts(
            [FromQuery] bool? isActive = null,
            [FromQuery] int limit = 50,
            CancellationToken cancellationToken = default)
        {
            var alerts = await _monitoringService.GetAlertsAsync(isActive, limit, cancellationToken);
            return Ok(ApiResponse<List<MonitoringAlert>>.SuccessResult(alerts, "Lấy danh sách cảnh báo thành công."));
        }

        /// <summary>
        /// Lấy trạng thái hoạt động của kênh telemetry và kho lưu trữ PostgreSQL.
        /// GET /api/v1/monitoring/status
        /// </summary>
        [HttpGet("status")]
        [ProducesResponseType(typeof(ApiResponse<MonitoringStatusDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
        {
            var status = await _monitoringService.GetStatusAsync(cancellationToken);
            return Ok(ApiResponse<MonitoringStatusDto>.SuccessResult(status, "Lấy trạng thái giám sát thành công."));
        }
    }
}
