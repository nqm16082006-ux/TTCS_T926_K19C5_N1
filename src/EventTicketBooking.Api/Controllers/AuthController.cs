using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller phụ trách xác thực người dùng (Task TTKN-25 - Story S-02).
    /// </summary>
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        /// <summary>
        /// API Đăng nhập bằng Email và Mật khẩu.
        /// - Đăng nhập đúng: Trả về 200 OK kèm JWT Access Token và thông tin User.
        /// - Nhập sai: Trả về 401 Unauthorized kèm thông báo bảo mật không tiết lộ email tồn tại.
        /// - Nhập sai 6 lần: Trả về 423 Locked với thông báo tài khoản bị khóa tạm 15 phút trên Redis.
        /// </summary>
        [HttpPost("login")]
        [ProducesResponseType(typeof(LoginResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status423Locked)]
        public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var result = await _authService.LoginAsync(request);

            if (result.Success && result.Data != null)
            {
                return Ok(result.Data);
            }

            if (result.IsLocked)
            {
                return StatusCode(StatusCodes.Status423Locked, new
                {
                    statusCode = StatusCodes.Status423Locked,
                    message = result.Message,
                    isLocked = true
                });
            }

            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                statusCode = StatusCodes.Status401Unauthorized,
                message = result.Message
            });
        }
    }
}
