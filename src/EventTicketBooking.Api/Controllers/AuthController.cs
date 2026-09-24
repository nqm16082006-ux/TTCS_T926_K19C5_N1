using System;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IEmailService _emailService;

        public AuthController(
            IAuthService authService,
            AppDbContext context,
            IPasswordHasher passwordHasher,
            IEmailService emailService)
        {
            _authService = authService;
            _context = context;
            _passwordHasher = passwordHasher;
            _emailService = emailService;
        }

        /// <summary>
        /// API Đăng nhập bằng Email và Mật khẩu.
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

        /// <summary>
        /// API Xác thực Email thông qua Link (Task T-08).
        /// </summary>
        [HttpGet("verify-email")]
        public async Task<IActionResult> VerifyEmail([FromQuery] string email, [FromQuery] string token)
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token))
            {
                return BadRequest("Thiếu thông tin xác thực.");
            }

            var success = await _authService.VerifyEmailCodeAsync(email, token);

            if (!success)
            {
                return BadRequest("Liên kết xác nhận không hợp lệ, đã hết hạn hoặc tài khoản đã được xác nhận.");
            }

            // Trả về HTML thân thiện cho người dùng click từ Email
            var html = @"
                <html>
                <body style='font-family: sans-serif; text-align: center; padding: 50px;'>
                    <h2 style='color: green;'>✅ Xác thực Email thành công!</h2>
                    <p>Tài khoản của bạn đã được kích hoạt. Bạn có thể đăng nhập ngay bây giờ.</p>
                </body>
                </html>
            ";
            return Content(html, "text/html");
        }

        /// <summary>
        /// API Đăng ký tài khoản mới (Task T-07).
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Kiểm tra email trùng lặp
            bool emailExists = await _context.Users.AnyAsync(u => u.Email == request.Email);
            if (emailExists)
            {
                // Yêu cầu: "thông báo lỗi không phân biệt email đã tồn tại hay chưa"
                return BadRequest(new { message = "Đăng ký không thành công. Thông tin không hợp lệ hoặc email đã tồn tại." });
            }

            // Lấy Role ""Customer"" từ Database (Tạo nếu chưa có để tránh lỗi khi không seed Data)
            var customerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "Customer");
            if (customerRole == null)
            {
                customerRole = new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "Customer",
                    Description = "Khách hàng và người tham dự",
                    CreatedAt = DateTime.UtcNow
                };
                _context.Roles.Add(customerRole);
                await _context.SaveChangesAsync();
            }

            // Hash mật khẩu với Argon2id
            string hashedPassword = _passwordHasher.Hash(request.Password);

            // Dùng phần trước @ của Email để làm Username
            string baseUsername = request.Email.Split('@')[0];
            string username = baseUsername;

            // Xử lý trùng lặp Username
            int counter = 1;
            while (await _context.Users.AnyAsync(u => u.Username == username))
            {
                username = $"{baseUsername}{counter}";
                counter++;
            }

            // Tạo User mới (Cờ chưa kích hoạt IsActive = false)
            var newUser = new User
            {
                Id = Guid.NewGuid(),
                FullName = request.FullName,
                Email = request.Email,
                Username = username,
                PasswordHash = hashedPassword,
                IsActive = false
            };

            // Tạo quan hệ N-N trong UserRoles
            var userRole = new UserRole
            {
                UserId = newUser.Id,
                RoleId = customerRole.Id
            };
            newUser.UserRoles.Add(userRole);

            _context.Users.Add(newUser);
            await _context.SaveChangesAsync();

            // T-08: Sinh mã xác thực và lưu vào DB (Hạn 24 giờ)
            var token = await _authService.GenerateAndSaveVerificationCodeAsync(newUser);

            // T-08: Tạo link xác thực chứa token và gửi qua Email Service
            var scheme = Request.Scheme ?? "https";
            var host = Request.Host.Value ?? "localhost";
            var verificationLink = $"{scheme}://{host}/api/auth/verify-email?email={newUser.Email}&token={token}";

            await _emailService.SendConfirmationEmailAsync(newUser.Email, newUser.FullName, verificationLink);

            // Trả về kết quả thành công
            return Created("", new
            {
                message = "Đăng ký thành công. Vui lòng kiểm tra email để kích hoạt tài khoản.",
                userId = newUser.Id,
                email = newUser.Email
            });
        }
    }
}
