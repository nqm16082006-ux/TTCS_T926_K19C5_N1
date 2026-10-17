using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// API đối soát giao dịch hệ thống với sao kê cổng thanh toán (Story S-51).
    /// Dành riêng cho Kế toán sự kiện (Accounting) và Quản trị viên (Admin).
    /// </summary>
    [ApiController]
    [Route("api/v1/reconciliation")]
    [RequireRole(EventTicketBooking.Api.Models.SystemRoles.Admin, EventTicketBooking.Api.Models.SystemRoles.Auditor)]
    public class ReconciliationController : ControllerBase
    {
        private readonly IReconciliationService _reconciliationService;
        private readonly ILogger<ReconciliationController> _logger;

        public ReconciliationController(
            IReconciliationService reconciliationService,
            ILogger<ReconciliationController> logger)
        {
            _reconciliationService = reconciliationService;
            _logger = logger;
        }

        public class ReconcileRequestForm
        {
            public DateTimeOffset FromDate { get; set; }
            public DateTimeOffset ToDate { get; set; }
            public IFormFile? File { get; set; }
            public string? CsvContent { get; set; }
        }

        /// <summary>
        /// Nạp tệp sao kê CSV và thực thi đối soát theo kỳ.
        /// </summary>
        [HttpPost("run")]
        [ProducesResponseType(typeof(ApiResponse<ReconciliationSummaryDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> RunReconciliation([FromForm] ReconcileRequestForm form)
        {
            if (form.FromDate > form.ToDate)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thời gian bắt đầu (FromDate) không được lớn hơn thời gian kết thúc (ToDate)."));
            }

            string csvContent = string.Empty;

            if (form.File != null && form.File.Length > 0)
            {
                using var reader = new StreamReader(form.File.OpenReadStream(), Encoding.UTF8);
                csvContent = await reader.ReadToEndAsync();
            }
            else if (!string.IsNullOrWhiteSpace(form.CsvContent))
            {
                csvContent = form.CsvContent;
            }
            else
            {
                return BadRequest(ApiResponse<object>.FailureResult("Vui lòng cung cấp tệp CSV sao kê hoặc chuỗi nội dung sao kê."));
            }

            // 1. Phân tích file CSV
            var (items, errors) = _reconciliationService.ParseCsvStatement(csvContent);

            if (items.Count == 0 && errors.Count > 0)
            {
                return BadRequest(ApiResponse<object>.FailureResult(
                    $"Không thể đọc giao dịch nào từ tệp sao kê. Lỗi: {string.Join(" | ", errors)}"));
            }

            // 2. Chạy đối soát với dữ liệu nội bộ
            var summary = await _reconciliationService.ReconcileAsync(
                form.FromDate,
                form.ToDate,
                items,
                errors,
                HttpContext?.RequestAborted ?? default);

            // 3. Ghi nhận phiên đối soát bất biến vào CSDL
            string username = User?.Identity?.Name ?? "Auditor";
            string sourceType = form.File != null ? "USER_FILE_CSV" : "USER_TEXT_CSV";
            summary.IsMockStatement = false;

            var session = await _reconciliationService.SaveReconciliationSessionAsync(
                form.FromDate,
                form.ToDate,
                sourceType,
                csvContent,
                summary,
                username,
                HttpContext?.RequestAborted ?? default);

            summary.SessionId = session.Id;
            summary.SessionCode = session.SessionCode;

            return Ok(ApiResponse<ReconciliationSummaryDto>.SuccessResult(
                summary,
                $"Đối soát hoàn tất (Mã phiên: {session.SessionCode}): Khớp {summary.MatchedCount}, Chỉ ở hệ thống {summary.SystemOnlyCount}, Chỉ ở cổng {summary.GatewayOnlyCount}, Lệch tiền {summary.AmountMismatchCount}, Chưa đủ điều kiện {summary.IneligibleCount}."));
        }

        /// <summary>
        /// Xuất kết quả đối soát thành tệp CSV báo cáo tải về máy.
        /// </summary>
        [HttpPost("export-csv")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult ExportCsvReport([FromBody] ReconciliationSummaryDto summary)
        {
            if (summary == null || summary.Items == null)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Dữ liệu báo cáo đối soát không hợp lệ."));
            }

            string csvData = _reconciliationService.ExportSummaryToCsv(summary);
            byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csvData)).ToArray();

            string fileName = $"Reconciliation_Report_{summary.FromDate:yyyyMMdd}_{summary.ToDate:yyyyMMdd}.csv";
            return File(bytes, "text/csv; charset=utf-8", fileName);
        }

        /// <summary>
        /// Lấy danh sách các phiên đối soát lịch sử gần đây.
        /// </summary>
        [HttpGet("sessions")]
        [ProducesResponseType(typeof(ApiResponse<System.Collections.Generic.List<ReconciliationSessionDto>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetRecentSessions([FromQuery] int limit = 20)
        {
            var sessions = await _reconciliationService.GetRecentSessionsAsync(limit, HttpContext?.RequestAborted ?? default);
            return Ok(ApiResponse<System.Collections.Generic.List<ReconciliationSessionDto>>.SuccessResult(sessions));
        }

        /// <summary>
        /// Lấy chi tiết một phiên đối soát lịch sử theo ID.
        /// </summary>
        [HttpGet("sessions/{id:guid}")]
        [ProducesResponseType(typeof(ApiResponse<ReconciliationSessionDetailDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSessionById(Guid id)
        {
            var session = await _reconciliationService.GetSessionByIdAsync(id, HttpContext?.RequestAborted ?? default);
            if (session == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy phiên đối soát với ID đã cung cấp."));
            }

            ReconciliationSummaryDto? summaryObj = null;
            if (!string.IsNullOrWhiteSpace(session.SummaryJson))
            {
                try
                {
                    summaryObj = System.Text.Json.JsonSerializer.Deserialize<ReconciliationSummaryDto>(
                        session.SummaryJson,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể deserialize SummaryJson cho phiên {SessionId}", session.Id);
                }
            }

            var detail = new ReconciliationSessionDetailDto
            {
                Id = session.Id,
                SessionCode = session.SessionCode,
                PeriodFrom = session.PeriodFrom,
                PeriodTo = session.PeriodTo,
                SourceType = session.SourceType,
                Status = session.Status,
                TotalInternalCount = session.TotalInternalCount,
                TotalInternalAmount = session.TotalInternalAmount,
                TotalGatewayCount = session.TotalGatewayCount,
                TotalGatewayAmount = session.TotalGatewayAmount,
                MatchedCount = session.MatchedCount,
                SystemOnlyCount = session.SystemOnlyCount,
                GatewayOnlyCount = session.GatewayOnlyCount,
                AmountMismatchCount = session.AmountMismatchCount,
                IneligibleCount = session.IneligibleCount,
                RawCsv = session.RawCsv,
                SummaryJson = session.SummaryJson,
                Summary = summaryObj,
                ExecutedAt = session.ExecutedAt,
                ExecutedBy = session.ExecutedBy
            };

            return Ok(ApiResponse<ReconciliationSessionDetailDto>.SuccessResult(detail));
        }
    }
}
