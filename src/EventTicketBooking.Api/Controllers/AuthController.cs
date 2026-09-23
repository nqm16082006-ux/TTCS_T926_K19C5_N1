using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Auth;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IAuthService _authService;

        public AuthController(AppDbContext context, IPasswordHasher passwordHasher, IAuthService authService)
        {
            _context = context;
            _passwordHasher = passwordHasher;
            _authService = authService;
        }

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
                return BadRequest(new { message = "Email này đã được sử dụng." });
            }

            // Lấy Role "Người mua" từ Database (Tạo nếu chưa có để tránh lỗi khi không seed Data)
            var buyerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "Người mua");
            if (buyerRole == null)
            {
                // Tạm đặt role là "Người mua" nếu chưa có sẵn trong Database
                buyerRole = new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "Người mua",
                    Description = "Khách hàng mua vé"
                };
                _context.Roles.Add(buyerRole);
                await _context.SaveChangesAsync();
            }

            // Hash mật khẩu với Argon2id
            string hashedPassword = _passwordHasher.HashPassword(request.Password);

            // Dùng phần trước @ của Email để làm Username (để thỏa mãn NOT NULL unique của Username)
            string baseUsername = request.Email.Split('@')[0];
            string username = baseUsername;
            
            // Xử lý trùng lặp Username nếu cần (tuy hiếm nhưng đề phòng)
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
                Username = username,
                Email = request.Email,
                PasswordHash = hashedPassword,
                FullName = request.FullName,
                RoleId = buyerRole.Id,
                IsActive = false
            };

            _context.Users.Add(newUser);
            await _context.SaveChangesAsync();

            // Trả về kết quả thành công, không tiết lộ mật khẩu hay hash
            return Created("", new
            {
                message = "Đăng ký thành công. Vui lòng kiểm tra email để kích hoạt tài khoản.",
                userId = newUser.Id,
                email = newUser.Email
            });
        }

        /// <summary>
        /// Đăng nhập tài khoản bằng Email và Mật khẩu (Task TTKN-25).
        /// </summary>
        /// <param name="request">Thông tin đăng nhập gồm email và password.</param>
        /// <returns>JWT Token và thông tin User khi đăng nhập thành công.</returns>
        [HttpPost("login")]
        [ProducesResponseType(typeof(ApiResponse<LoginResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<LoginResponseDto>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<LoginResponseDto>), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<LoginResponseDto>.FailureResult(
                    "Dữ liệu yêu cầu không hợp lệ.",
                    ModelState
                ));
            }

            var result = await _authService.LoginAsync(request);

            if (!result.Success)
            {
                return Unauthorized(result);
            }

            return Ok(result);
        }
    }
}
