using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Services.Interfaces;
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
        private readonly ILogger<PaymentsController> _logger;

        public PaymentsController(
            IPaymentService paymentService,
            ILogger<PaymentsController> logger)
        {
            _paymentService = paymentService;
            _logger = logger;
        }

        /// <summary>
        /// Tạo link thanh toán cho đơn hàng (Task T-40 & T-41).
        /// </summary>
        [HttpPost("orders/{orderId:guid}")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<PaymentCreationResult>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CreatePayment(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var result = await _paymentService.CreatePaymentForOrderAsync(orderId, userId, HttpContext.RequestAborted);
            if (!result.Success)
            {
                if (result.ErrorMessage == "Không tìm thấy đơn hàng.")
                    return NotFound(ApiResponse<object>.FailureResult(result.ErrorMessage));

                if (result.ErrorMessage == "Bạn không có quyền thanh toán cho đơn hàng này.")
                    return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult(result.ErrorMessage));

                return BadRequest(ApiResponse<object>.FailureResult(result.ErrorMessage ?? "Không thể tạo yêu cầu thanh toán."));
            }

            return Ok(ApiResponse<PaymentCreationResult>.SuccessResult(result, "Tạo link thanh toán thành công."));
        }

        /// <summary>
        /// Webhook tiếp nhận kết quả thanh toán từ cổng thanh toán (Task T-40).
        /// </summary>
        [HttpPost("webhook")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> HandlePaymentWebhook()
        {
            try
            {
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                string payload = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(payload))
                {
                    return BadRequest(ApiResponse<object>.FailureResult("Webhook payload rỗng."));
                }

                // Lấy chữ ký từ Header (x-signature hoặc webhook-signature) hoặc từ body payload
                string? signature = Request.Headers["x-signature"].ToString();
                if (string.IsNullOrEmpty(signature))
                {
                    signature = Request.Headers["webhook-signature"].ToString();
                }

                // Nếu không có trong header, thử trích xuất trường "signature" trong JSON payload (chuẩn PayOS)
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
                        // Bỏ qua lỗi parse nếu payload không phải JSON
                    }
                }

                if (string.IsNullOrEmpty(signature))
                {
                    _logger.LogWarning("Webhook thiếu chữ ký xác thực.");
                    return BadRequest(ApiResponse<object>.FailureResult("Webhook thiếu chữ ký xác thực."));
                }

                bool isProcessed = await _paymentService.ProcessPaymentWebhookAsync(payload, signature, HttpContext.RequestAborted);
                if (!isProcessed)
                {
                    return BadRequest(ApiResponse<object>.FailureResult("Xử lý webhook thất bại hoặc chữ ký không hợp lệ."));
                }

                return Ok(new { success = true, message = "Webhook đã được xử lý thành công." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ngoại lệ khi nhận webhook thanh toán");
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.FailureResult("Lỗi hệ thống khi xử lý webhook."));
            }
        }
    }
}
