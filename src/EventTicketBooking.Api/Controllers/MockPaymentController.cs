using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/mock-payment")]
    public class MockPaymentController : ControllerBase
    {
        public record MockPaymentCallbackDto(string OrderId, string Status);

        /// <summary>
        /// Tiếp nhận phản hồi giả lập từ trang thanh toán (Task T-43)
        /// </summary>
        [HttpPost("callback")]
        public IActionResult HandleCallback([FromBody] MockPaymentCallbackDto request)
        {
            if (request.Status == "SUCCESS")
            {
                // Xử lý đơn hàng đã trả tiền thành công
                return Ok(ApiResponse<object>.SuccessResult(
                    new { redirectUrl = $"/order-success.html?orderId={request.OrderId}" },
                    "Thanh toán thành công! Đơn hàng đã chuyển sang trạng thái ĐÃ THẮNG/ĐÃ TRẢ."));
            }

            // Theo S-24: Thanh toán thất bại -> Đơn giữ nguyên PENDING, ghế tiếp tục giữ tới hết hạn
            return Ok(ApiResponse<object>.SuccessResult(
                new { redirectUrl = $"/order-detail.html?orderId={request.OrderId}" },
                "Thanh toán thất bại! Đơn hàng vẫn được giữ nguyên ở trạng thái chờ và có thể bấm thanh toán lại."));
        }
    }
}