
using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Api.Middlewares;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/payments")]
    public class PaymentsController : ControllerBase
    {
        private readonly IPaymentService _paymentService;
        private readonly IPaymentGateway _paymentGateway;
        private readonly ILogger<PaymentsController> _logger;
        private readonly EventTicketBooking.Api.Data.AppDbContext _context;

        [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
        public PaymentsController(
            IPaymentService paymentService,
            IPaymentGateway paymentGateway,
            ILogger<PaymentsController> logger,
            EventTicketBooking.Api.Data.AppDbContext? context = null)
        {
            _paymentService = paymentService;
            _paymentGateway = paymentGateway;
            _logger = logger;
            _context = context!;
        }

        public PaymentsController(
            IPaymentService paymentService,
            ILogger<PaymentsController> logger,
            EventTicketBooking.Api.Data.AppDbContext context)
            : this(paymentService, null!, logger, context)
        {
        }

        public PaymentsController(
            IPaymentService paymentService,
            IPaymentGateway paymentGateway,
            ILogger<PaymentsController> logger)
            : this(paymentService, paymentGateway, logger, null)
        {
        }

        /// <summary>
        /// Tạo link thanh toán cho đơn hàng (Task T-40 & T-41).
        /// </summary>
        [HttpPost("orders/{orderId:guid}")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<PaymentCreationResult>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CreatePayment(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));
            }

            var result = await _paymentService.CreatePaymentForOrderAsync(
                orderId,
                userId,
                HttpContext.RequestAborted);

            if (!result.Success)
            {
                if (result.ErrorMessage == "Không tìm thấy đơn hàng.")
                {
                    return NotFound(ApiResponse<object>.FailureResult(result.ErrorMessage));
                }

                if (result.ErrorMessage == "Bạn không có quyền thanh toán cho đơn hàng này.")
                {
                    return StatusCode(
                        StatusCodes.Status403Forbidden,
                        ApiResponse<object>.FailureResult(result.ErrorMessage));
                }

                return BadRequest(
                    ApiResponse<object>.FailureResult(
                        result.ErrorMessage ?? "Không thể tạo yêu cầu thanh toán."));
            }

            return Ok(
                ApiResponse<PaymentCreationResult>.SuccessResult(
                    result,
                    "Tạo link thanh toán thành công."));
        }

        /// <summary>
        /// Webhook tiếp nhận kết quả thanh toán từ cổng thanh toán (Task T-40).
        /// </summary>
        [HttpPost("webhook")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> HandlePaymentWebhook()
        {
            try
            {
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                string payload = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(payload))
                {
                    return BadRequest(
                        ApiResponse<object>.FailureResult("Webhook payload rỗng."));
                }

                // Lấy chữ ký từ Header (x-signature hoặc webhook-signature)
                // hoặc từ body payload.
                string? signature = Request.Headers["x-signature"].ToString();

                if (string.IsNullOrEmpty(signature))
                {
                    signature = Request.Headers["webhook-signature"].ToString();
                }

                // Nếu không có trong header, thử trích xuất trường "signature"
                // trong JSON payload.
                if (string.IsNullOrEmpty(signature))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);

                        if (doc.RootElement.TryGetProperty("signature", out var sigEl))
                        {
                            signature = sigEl.GetString();
                        }
                    }
                    catch
                    {
                        // Bỏ qua lỗi parse nếu payload không phải JSON.
                    }
                }

                // T-48: từ chối webhook nếu không có chữ ký.
                if (string.IsNullOrEmpty(signature))
                {
                    _logger.LogWarning(
                        "Webhook rejected. StatusCode={StatusCode}, SourceAddress={SourceAddress}, OrderCode={OrderCode}, Timestamp={Timestamp}, Reason={Reason}",
                        StatusCodes.Status401Unauthorized,
                        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        null,
                        DateTimeOffset.UtcNow,
                        "MissingSignature");

                    return Unauthorized(
                        ApiResponse<object>.FailureResult(
                            "Webhook thiếu chữ ký xác thực."));
                }

                // T-48: Verify signature BEFORE any business processing.
                bool isSignatureValid =
                    _paymentGateway.VerifyWebhookSignature(payload, signature);

                if (!isSignatureValid)
                {
                    _logger.LogWarning(
                        "Webhook rejected. StatusCode={StatusCode}, SourceAddress={SourceAddress}, OrderCode={OrderCode}, Timestamp={Timestamp}, Reason={Reason}",
                        StatusCodes.Status401Unauthorized,
                        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        null,
                        DateTimeOffset.UtcNow,
                        "InvalidSignature");

                    return Unauthorized(
                        ApiResponse<object>.FailureResult(
                            "Chữ ký webhook không hợp lệ."));
                }

                bool isProcessed =
                    await _paymentService.ProcessPaymentWebhookAsync(
                        payload,
                        signature,
                        HttpContext.RequestAborted);

                if (!isProcessed)
                {
                    return BadRequest(
                        ApiResponse<object>.FailureResult(
                            "Xử lý webhook thất bại."));
                }

                return Ok(
                    new
                    {
                        success = true,
                        message = "Webhook đã được xử lý thành công."
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Ngoại lệ khi nhận webhook thanh toán");

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<object>.FailureResult(
                        "Lỗi hệ thống khi xử lý webhook."));
            }
        }

        /// <summary>
        /// Tiếp nhận chuyển hướng quay về từ cổng thanh toán (Task T-42).
        /// Không tin tưởng query string từ client, thực hiện đối soát server-side với gateway trước khi cập nhật.
        /// </summary>
        [HttpGet("return")]
        [AllowAnonymous]
        [ProducesResponseType(
            typeof(ApiResponse<PaymentExecutionResult>),
            StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> HandlePaymentReturn(
            [FromQuery] PaymentReturnQueryDto query)
        {
            var cancellationToken = HttpContext?.RequestAborted ?? default;

            var result =
                await _paymentService.VerifyAndProcessPaymentReturnAsync(
                    query,
                    cancellationToken);

            if (!result.Success)
            {
                return BadRequest(
                    ApiResponse<PaymentExecutionResult>.FailureResult(
                        result.Message ?? "Xác minh kết quả thanh toán thất bại.",
                        result));
            }

            return Ok(
                ApiResponse<PaymentExecutionResult>.SuccessResult(
                    result,
                    result.Message ?? "Xác minh kết quả thanh toán thành công."));
        }

        /// <summary>
        /// Truy vấn và đối soát trạng thái thanh toán của đơn hàng (Task T-42).
        /// Dành cho trang thanh toán frontend kiểm tra trạng thái thực tế từ máy chủ.
        /// </summary>
        [HttpGet("orders/{orderId:guid}/status")]
        [RequireRole]
        [ProducesResponseType(
            typeof(ApiResponse<PaymentExecutionResult>),
            StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAndVerifyPaymentStatus(Guid orderId)
        {
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value;
            if (!Guid.TryParse(userIdValue, out var userId)) return Unauthorized();
            if (_context != null)
            {
                var order = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(_context.Orders, o => o.Id == orderId);
                if (order == null) return NotFound();
                if (order.UserId != userId && !User.IsInRole("Admin")) return StatusCode(403);
            }
            var cancellationToken = HttpContext?.RequestAborted ?? default;

            var result =
                await _paymentService.VerifyAndProcessPaymentReturnAsync(
                    new PaymentReturnQueryDto
                    {
                        OrderId = orderId
                    },
                    cancellationToken);

            if (!result.Success &&
                result.Message == "Không tìm thấy đơn hàng cần xác minh.")
            {
                return NotFound(
                    ApiResponse<object>.FailureResult(result.Message));
            }

            return Ok(
                ApiResponse<PaymentExecutionResult>.SuccessResult(
                    result,
                    result.Message ?? "Trạng thái thanh toán của đơn hàng."));
        }
    }
}



