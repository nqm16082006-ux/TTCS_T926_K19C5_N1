using System;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/mock-payment")]
    public class MockPaymentController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPaymentService _paymentService;

        public MockPaymentController(AppDbContext context, IPaymentService paymentService)
        {
            _context = context;
            _paymentService = paymentService;
        }

        public record MockPaymentCallbackDto(string OrderId, string Status);

        /// <summary>
        /// Tiếp nhận phản hồi giả lập từ trang thanh toán (Task T-43)
        /// Cập nhật trực tiếp kết quả thanh toán vào database thông qua PaymentService
        /// </summary>
        [HttpPost("callback")]
        public async Task<IActionResult> HandleCallback([FromBody] MockPaymentCallbackDto request)
        {
            if (Guid.TryParse(request.OrderId, out var orderId))
            {
                var order = await _context.Orders
                    .Include(o => o.OrderItems)
                    .Include(o => o.PaymentTransaction)
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                if (order != null)
                {
                    if (request.Status == "SUCCESS")
                    {
                        var resultDto = new PaymentResultDto
                        {
                            OrderId = order.Id.ToString(),
                            OrderCode = order.PaymentTransaction?.OrderCode,
                            Amount = order.TotalAmount,
                            Status = PaymentStatus.Success,
                            TransactionId = "MOCK-" + Guid.NewGuid().ToString("N")[..8].ToUpper()
                        };

                        await _paymentService.HandlePaymentResultAsync(resultDto);

                        return Ok(ApiResponse<object>.SuccessResult(
                            new { redirectUrl = $"/order-success.html?orderId={request.OrderId}" },
                            "Thanh toán thành công! Vé của bạn đã được xuất."));
                    }
                    else
                    {
                        var resultDto = new PaymentResultDto
                        {
                            OrderId = order.Id.ToString(),
                            OrderCode = order.PaymentTransaction?.OrderCode,
                            Amount = order.TotalAmount,
                            Status = PaymentStatus.Failed,
                            TransactionId = "MOCK-FAIL-" + Guid.NewGuid().ToString("N")[..8].ToUpper()
                        };

                        await _paymentService.HandlePaymentResultAsync(resultDto);

                        return Ok(ApiResponse<object>.SuccessResult(
                            new { redirectUrl = $"/order-detail.html?orderId={request.OrderId}" },
                            "Thanh toán thất bại! Đơn hàng vẫn được giữ nguyên để thử lại."));
                    }
                }
            }

            return BadRequest(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng cần thanh toán."));
        }
    }
}