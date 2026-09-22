using EventTicketBooking.Api.Middlewares;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller kiểm thử phân quyền tối thiểu phục vụ nghiệm thu TTKN-26.
    /// Không chứa logic nghiệp vụ, không nhận bất kỳ tham số giả lập quyền nào từ client.
    /// </summary>
    [ApiController]
    [Route("api/test-auth")]
    public class RoleAuthTestController : ControllerBase
    {
        /// <summary>
        /// Endpoint công khai (Public) - Không yêu cầu đăng nhập.
        /// </summary>
        [HttpGet("public")]
        public IActionResult PublicEndpoint()
        {
            return Ok(new
            {
                message = "Public: Bất kỳ ai cũng có thể truy cập endpoint này."
            });
        }

        /// <summary>
        /// Endpoint yêu cầu xác thực - Yêu cầu người dùng đã đăng nhập (bất kể role nào).
        /// Trả về 401 Unauthorized nếu chưa đăng nhập.
        /// </summary>
        [HttpGet("authenticated")]
        [RequireRole]
        public IActionResult AuthenticatedEndpoint()
        {
            return Ok(new
            {
                message = "Authenticated: Bạn đã đăng nhập thành công."
            });
        }

        /// <summary>
        /// Endpoint yêu cầu vai trò Admin.
        /// Trả về 401 Unauthorized nếu chưa đăng nhập.
        /// Trả về 403 Forbidden nếu đã đăng nhập nhưng không có vai trò Admin.
        /// </summary>
        [HttpGet("admin")]
        [RequireRole("Admin")]
        public IActionResult AdminEndpoint()
        {
            return Ok(new
            {
                message = "Admin: Bạn có vai trò Admin và được phép truy cập endpoint này."
            });
        }
    }
}
