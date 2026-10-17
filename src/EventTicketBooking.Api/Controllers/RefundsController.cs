using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// API quản trị cho bộ phận Kế toán / Quản trị viên quản lý và theo dõi các lệnh hoàn tiền (Jira Story S-48).
    /// </summary>
    [ApiController]
    [Route("api/v1/payments/refunds")]
    [RequireRole("Admin", "Auditor")]
    public class RefundsController : ControllerBase
    {
        private readonly IRefundService _refundService;
        private readonly AppDbContext _context;
        private readonly IOrderAuditService _orderAuditService;

        public RefundsController(
            IRefundService refundService,
            AppDbContext context,
            IOrderAuditService orderAuditService)
        {
            _refundService = refundService;
            _context = context;
            _orderAuditService = orderAuditService;
        }

        /// <summary>
        /// Lấy danh sách tất cả các yêu cầu hoàn tiền gần nhất kèm lịch sử các lần thử.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<List<RefundDetailDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetAllRefunds([FromQuery] int take = 50)
        {
            var refunds = await _refundService.GetAllRefundsAsync(take, HttpContext.RequestAborted);
            var dtos = new List<RefundDetailDto>();
            foreach (var r in refunds)
            {
                var dto = MapToDto(r);
                dto.AttemptHistory = await GetAttemptHistoryAsync(r.OrderId);
                dtos.Add(dto);
            }

            return Ok(ApiResponse<List<RefundDetailDto>>.SuccessResult(
                dtos,
                "Lấy danh sách yêu cầu hoàn tiền thành công."));
        }

        /// <summary>
        /// Kích hoạt vòng quét worker retry & recovery ngay lập tức (phục vụ test).
        /// </summary>
        [HttpPost("worker/trigger")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> TriggerWorker()
        {
            int processed = await _refundService.ProcessPendingAndRetryableRefundsAsync(HttpContext.RequestAborted);
            int notified = await _refundService.RetryFailedAccountingNotificationsAsync(HttpContext.RequestAborted);

            return Ok(ApiResponse<object>.SuccessResult(new
            {
                processedRefunds = processed,
                retriedNotifications = notified,
                triggeredAt = DateTimeOffset.UtcNow
            }, $"Đã kích hoạt quét worker thành công: {processed} lệnh hoàn tiền, {notified} thông báo kế toán."));
        }

        /// <summary>
        /// Tạo nhanh đơn hàng và giả lập kịch bản hoàn tiền (phục vụ kiểm thử UI Story S-48).
        /// </summary>
        [HttpPost("simulate")]
        [ProducesResponseType(typeof(ApiResponse<RefundDetailDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> SimulateRefund([FromBody] SimulateRefundDto? dto)
        {
            try
            {
                var mode = dto?.Mode?.Trim().ToLowerInvariant() ?? "failretry";
                string reasonTag = mode switch
                {
                    "failpermanent" => "[FAIL_PERMANENT] Tài khoản khách hàng bị phong tỏa",
                    "success" => "[SUCCESS] Yêu cầu hoàn tiền thử nghiệm",
                    _ => "[FAIL_RETRY] Lỗi kết nối Timeout giả lập"
                };

                Order? order = null;
                if (dto?.OrderId.HasValue == true)
                {
                    order = await _context.Orders
                        .Include(o => o.PaymentTransaction)
                        .FirstOrDefaultAsync(o => o.Id == dto.OrderId.Value, HttpContext.RequestAborted);
                }

                if (order == null)
                {
                    var user = await _context.Users.FirstOrDefaultAsync(HttpContext.RequestAborted);
                    if (user == null)
                    {
                        return BadRequest(ApiResponse<object>.FailureResult("Không tìm thấy người dùng trong hệ thống để tạo đơn mẫu."));
                    }

                    var showtime = await _context.Showtimes.FirstOrDefaultAsync(HttpContext.RequestAborted);
                    if (showtime == null)
                    {
                        return BadRequest(ApiResponse<object>.FailureResult("Không tìm thấy suất diễn trong hệ thống để tạo đơn mẫu."));
                    }

                    var newOrder = new Order
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        ShowtimeId = showtime.Id,
                        TotalAmount = dto?.Amount ?? 500000,
                        Status = OrderStatus.Paid,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
                    };
                    var tx = new PaymentTransaction
                    {
                        Id = Guid.NewGuid(),
                        OrderId = newOrder.Id,
                        OrderCode = Math.Abs(BitConverter.ToInt64(newOrder.Id.ToByteArray(), 0) % 9000000000L) + 1000000000L,
                        TransactionId = $"SIM_TX_{Guid.NewGuid():N}"[..20],
                        Amount = newOrder.TotalAmount,
                        Status = "PAID",
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    newOrder.PaymentTransaction = tx;
                    _context.Orders.Add(newOrder);
                    await _context.SaveChangesAsync(HttpContext.RequestAborted);
                    order = newOrder;
                }

                int refundAmount = dto?.Amount ?? (order.TotalAmount > 0 ? order.TotalAmount : 500000);
                var refund = await _refundService.CreateRefundRequestAsync(
                    order.Id,
                    refundAmount,
                    reasonTag,
                    HttpContext.RequestAborted);

                if (dto?.AutoProcess != false)
                {
                    await _refundService.ProcessRefundAsync(refund.Id, HttpContext.RequestAborted);
                    refund = await _refundService.GetRefundByIdAsync(refund.Id, HttpContext.RequestAborted) ?? refund;
                }

                return Ok(ApiResponse<RefundDetailDto>.SuccessResult(
                    MapToDto(refund),
                    $"Tạo và giả lập hoàn tiền thành công (Chế độ: {mode})."));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<object>.FailureResult(ex.Message));
            }
        }

        /// <summary>
        /// Tạo yêu cầu hoàn tiền cho đơn hàng.
        /// </summary>
        [HttpPost("orders/{orderId:guid}")]
        [ProducesResponseType(typeof(ApiResponse<RefundDetailDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CreateRefund(Guid orderId, [FromBody] CreateRefundDto? dto)
        {
            try
            {
                var refund = await _refundService.CreateRefundRequestAsync(
                    orderId,
                    dto?.Amount,
                    dto?.Reason,
                    HttpContext.RequestAborted);

                return Ok(ApiResponse<RefundDetailDto>.SuccessResult(
                    MapToDto(refund),
                    "Tạo yêu cầu hoàn tiền thành công."));
            }
            catch (ArgumentException ex)
            {
                return NotFound(ApiResponse<object>.FailureResult(ex.Message));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<object>.FailureResult(ex.Message));
            }
        }

        /// <summary>
        /// Tra cứu chi tiết một lệnh hoàn tiền theo ID.
        /// </summary>
        [HttpGet("{refundId:guid}")]
        [ProducesResponseType(typeof(ApiResponse<RefundDetailDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetRefundById(Guid refundId)
        {
            var refund = await _refundService.GetRefundByIdAsync(refundId, HttpContext.RequestAborted);
            if (refund == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy lệnh hoàn tiền."));
            }

            var dto = MapToDto(refund);
            dto.AttemptHistory = await GetAttemptHistoryAsync(refund.OrderId);

            return Ok(ApiResponse<RefundDetailDto>.SuccessResult(
                dto,
                "Lấy thông tin lệnh hoàn tiền thành công."));
        }

        /// <summary>
        /// Lấy danh sách các lệnh hoàn tiền cần Kế toán xử lý thủ công kèm lịch sử các lần thử (ManualReviewRequired).
        /// </summary>
        [HttpGet("manual-review")]
        [ProducesResponseType(typeof(ApiResponse<List<RefundDetailDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetManualReviewList()
        {
            var refunds = await _refundService.GetRefundsForManualReviewAsync(HttpContext.RequestAborted);
            var dtos = new List<RefundDetailDto>();
            foreach (var r in refunds)
            {
                var dto = MapToDto(r);
                dto.AttemptHistory = await GetAttemptHistoryAsync(r.OrderId);
                dtos.Add(dto);
            }

            return Ok(ApiResponse<List<RefundDetailDto>>.SuccessResult(
                dtos,
                "Danh sách các lệnh hoàn tiền cần kế toán xử lý thủ công."));
        }

        /// <summary>
        /// Kế toán tiếp nhận xử lý hồ sơ hoàn tiền thủ công (chuyển sang AccountingProcessing).
        /// </summary>
        [HttpPost("{refundId:guid}/assign-accounting")]
        [ProducesResponseType(typeof(ApiResponse<RefundDetailDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> AssignAccounting(Guid refundId)
        {
            var cancellationToken = HttpContext?.RequestAborted ?? default;
            var refund = await _refundService.GetRefundByIdAsync(refundId, cancellationToken);
            if (refund == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy lệnh hoàn tiền."));
            }

            if (refund.Status != RefundStatus.ManualReviewRequired)
            {
                return BadRequest(ApiResponse<object>.FailureResult($"Không thể tiếp nhận lệnh ở trạng thái '{refund.Status}'. Chỉ tiếp nhận lệnh chờ xử lý (ManualReviewRequired)."));
            }

            var actorName = User?.Identity?.Name ?? "Auditor";
            refund.Status = RefundStatus.AccountingProcessing;
            refund.UpdatedAt = DateTimeOffset.UtcNow;

            var refundEntity = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == refund.OrderId);
            if (refundEntity != null)
            {
                refundEntity.Status = "AccountingProcessing";
                refundEntity.UpdatedAt = DateTimeOffset.UtcNow;
            }

            _orderAuditService.Record(
                refund.OrderId,
                "REFUND",
                refund.Id.ToString(),
                "ACCOUNTING_PROCESSING",
                RefundStatus.ManualReviewRequired.ToString(),
                RefundStatus.AccountingProcessing.ToString(),
                "ACCOUNTING",
                actorName,
                null,
                $"Kế toán ({actorName}) đã tiếp nhận hồ sơ để xử lý hoàn tiền thủ công.");

            await _context.SaveChangesAsync();

            var dto = MapToDto(refund);
            dto.AttemptHistory = await GetAttemptHistoryAsync(refund.OrderId);
            return Ok(ApiResponse<RefundDetailDto>.SuccessResult(dto, "Kế toán đã tiếp nhận xử lý hồ sơ hoàn tiền."));
        }

        /// <summary>
        /// Kế toán hoàn tất xử lý hoàn tiền thủ công cho khách hàng (chống hoàn tiền trùng lặp).
        /// </summary>
        [HttpPost("{refundId:guid}/resolve-accounting")]
        [ProducesResponseType(typeof(ApiResponse<RefundDetailDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ResolveAccounting(Guid refundId, [FromBody] ResolveAccountingRefundDto? dto)
        {
            var cancellationToken = HttpContext?.RequestAborted ?? default;
            var refund = await _refundService.GetRefundByIdAsync(refundId, cancellationToken);
            if (refund == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy lệnh hoàn tiền."));
            }

            if (refund.Status == RefundStatus.Succeeded || refund.Status == RefundStatus.AccountingResolved)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Lệnh hoàn tiền này đã thành công hoặc đã được kế toán xử lý trước đó. Không được phép hoàn tiền trùng lặp!"));
            }

            if (refund.Status != RefundStatus.ManualReviewRequired && refund.Status != RefundStatus.AccountingProcessing)
            {
                return BadRequest(ApiResponse<object>.FailureResult($"Chỉ có thể xử lý thủ công các lệnh ở trạng thái chờ duyệt (hiện tại: '{refund.Status}')."));
            }

            var actorName = User?.Identity?.Name ?? "Auditor";
            var manualTx = string.IsNullOrWhiteSpace(dto?.ManualTransactionCode)
                ? $"MANUAL_BANK_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
                : dto.ManualTransactionCode.Trim();

            var oldStatus = refund.Status.ToString();
            refund.Status = RefundStatus.AccountingResolved;
            refund.RefundTransactionId = manualTx;
            refund.UpdatedAt = DateTimeOffset.UtcNow;

            var refundEntity = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == refund.OrderId);
            if (refundEntity != null)
            {
                refundEntity.Status = "Succeeded";
                refundEntity.ConfirmedByName = actorName;
                refundEntity.ConfirmedAt = DateTimeOffset.UtcNow;
                refundEntity.UpdatedAt = DateTimeOffset.UtcNow;
            }

            _orderAuditService.Record(
                refund.OrderId,
                "REFUND",
                refund.Id.ToString(),
                "ACCOUNTING_RESOLVED",
                oldStatus,
                RefundStatus.AccountingResolved.ToString(),
                "ACCOUNTING",
                actorName,
                null,
                $"Kế toán ({actorName}) đã hoàn tất xử lý hoàn tiền thủ công. Mã GD/tham chiếu: {manualTx}. Ghi chú: {dto?.Notes ?? "Không có"}");

            await _context.SaveChangesAsync();

            var resultDto = MapToDto(refund);
            resultDto.AttemptHistory = await GetAttemptHistoryAsync(refund.OrderId);
            return Ok(ApiResponse<RefundDetailDto>.SuccessResult(resultDto, $"Kế toán đã hoàn tất xử lý hoàn tiền thủ công cho đơn hàng #{refund.OrderCode}."));
        }

        private async Task<List<RefundAttemptHistoryDto>> GetAttemptHistoryAsync(Guid orderId)
        {
            var logs = await _context.OrderAuditLogs
                .AsNoTracking()
                .Where(l => l.OrderId == orderId && (l.EntityType == "REFUND" || l.Action.StartsWith("REFUND_") || l.Action.StartsWith("ACCOUNTING_")))
                .OrderBy(l => l.Timestamp)
                .ToListAsync();

            var history = new List<RefundAttemptHistoryDto>();
            int attemptIdx = 1;
            foreach (var log in logs)
            {
                bool isAttempt = log.Action == "REFUND_RETRY_SCHEDULED" || log.Action == "REFUND_FAILED" || log.Action == "REFUND_SUCCEEDED" || log.Action == "REFUND_MANUAL_REVIEW_REQUIRED";
                history.Add(new RefundAttemptHistoryDto
                {
                    AttemptNumber = isAttempt ? attemptIdx++ : attemptIdx,
                    Action = log.Action,
                    Status = log.NewStatus ?? log.Action,
                    ErrorCode = null,
                    Notes = log.Note,
                    Timestamp = log.Timestamp
                });
            }
            return history;
        }

        /// <summary>
        /// Kích hoạt thực thi hoặc thử lại lệnh hoàn tiền ngay lập tức (dành cho Admin/Kế toán).
        /// </summary>
        [HttpPost("{refundId:guid}/process")]
        [ProducesResponseType(typeof(ApiResponse<RefundExecutionResult>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ProcessRefund(Guid refundId)
        {
            var result = await _refundService.ProcessRefundAsync(refundId, HttpContext.RequestAborted);
            if (!result.Success)
            {
                return BadRequest(ApiResponse<RefundExecutionResult>.FailureResult(
                    result.ErrorMessage ?? "Thực thi hoàn tiền thất bại.",
                    result));
            }

            return Ok(ApiResponse<RefundExecutionResult>.SuccessResult(
                result,
                "Lệnh hoàn tiền đã được xử lý thành công."));
        }

        private static RefundDetailDto MapToDto(RefundRequest r) => new()
        {
            Id = r.Id,
            OrderId = r.OrderId,
            OrderCode = r.OrderCode,
            TransactionId = r.TransactionId,
            RefundTransactionId = r.RefundTransactionId,
            IdempotencyKey = r.IdempotencyKey,
            Amount = r.Amount,
            Currency = r.Currency,
            Reason = r.Reason,
            Status = r.Status.ToString(),
            AttemptCount = r.AttemptCount,
            RetryCount = r.RetryCount,
            MaxRetries = r.MaxRetries,
            LastAttemptedAt = r.LastAttemptedAt,
            NextRetryAt = r.NextRetryAt,
            FailedAt = r.FailedAt,
            ErrorCode = r.ErrorCode,
            ErrorMessage = r.ErrorMessage,
            IsRetryable = r.IsRetryable,
            AccountingNotified = r.AccountingNotified,
            AccountingNotifiedAt = r.AccountingNotifiedAt,
            AccountingNotificationError = r.AccountingNotificationError,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        };
    }
}
