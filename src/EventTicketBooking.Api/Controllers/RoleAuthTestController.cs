using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Middlewares;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller kiểm thử phân quyền RBAC và kích hoạt Email (T-08 & TTKN-26).
    /// </summary>
    [ApiController]
    [Route("api/test-auth")]
    public class RoleAuthTestController : ControllerBase
    {
        /// <summary>
        /// 1. Endpoint công khai (Public) - Không yêu cầu đăng nhập hay xác thực email.
        /// </summary>
        [HttpGet("public")]
        public IActionResult PublicEndpoint()
        {
            return Ok(new
            {
                status = "Success",
                message = "Public: Bất kỳ ai cũng có thể truy cập endpoint này."
            });
        }

        /// <summary>
        /// 2. Endpoint yêu cầu tài khoản ĐÃ KÍCH HOẠT / XÁC THỰC EMAIL (Đáp ứng T-08 - AC4).
        /// </summary>
        [HttpGet("require-email-confirmed")]
        [RequireRole]
        [RequireEmailConfirmed] // Gắn thẻ chặn người dùng chưa kích hoạt email tại đây
        public IActionResult RequireEmailConfirmedEndpoint()
        {
            return Ok(new
            {
                status = "Success",
                message = "Bạn đã kích hoạt email thành công và được phép truy cập tính năng này!",
                user = User.Identity?.Name ?? User.FindFirst("email")?.Value
            });
        }

        /// <summary>
        /// 3. Endpoint yêu cầu xác thực - Bất kỳ ai đã đăng nhập (bất kể role nào).
        /// </summary>
        [HttpGet("authenticated")]
        [RequireRole]
        public IActionResult AuthenticatedEndpoint()
        {
            return Ok(new
            {
                status = "Success",
                message = "Authenticated: Bạn đã đăng nhập thành công vào hệ thống.",
                user = User.Identity?.Name ?? User.FindFirst("email")?.Value
            });
        }

        /// <summary>
        /// 4. Endpoint chỉ dành cho Quản trị viên (Admin).
        /// </summary>
        [HttpGet("admin-only")]
        [RequireRole("Admin")]
        public IActionResult AdminOnlyEndpoint()
        {
            return Ok(new
            {
                status = "Success",
                message = "Admin: Bạn có vai trò Admin và được phép truy cập tài nguyên này."
            });
        }

        /// <summary>
        /// 5. Endpoint dành cho Ban tổ chức sự kiện (Organizer) hoặc Quản trị viên (Admin).
        /// </summary>
        [HttpGet("organizer-only")]
        [RequireRole("Organizer", "Admin")]
        public IActionResult OrganizerOnlyEndpoint()
        {
            return Ok(new
            {
                status = "Success",
                message = "Organizer: Bạn có vai trò Organizer/Admin và được phép quản lý sự kiện."
            });
        }

        /// <summary>
        /// Endpoint hỗ trợ sinh test token nhanh theo email.
        /// </summary>
        [HttpPost("dev-token")]
        public async Task<IActionResult> GenerateDevToken(
            [FromQuery] string email,
            [FromServices] AppDbContext dbContext)
        {
            var user = await dbContext.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email.Trim().ToLower());

            if (user == null)
            {
                return NotFound(new { message = $"Không tìm thấy user với email: {email}" });
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim("id", user.Id.ToString()),
                new Claim("IsActive", user.IsActive.ToString()) // Thêm claim IsActive để Middleware đọc được
            };

            foreach (var ur in user.UserRoles)
            {
                claims.Add(new Claim(ClaimTypes.Role, ur.Role.Name));
                claims.Add(new Claim("role", ur.Role.Name));
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("EventTicketBooking_Super_Secret_Key_For_Jwt_Security_2026_!"));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: "EventTicketBooking.Api",
                audience: "EventTicketBooking.Client",
                claims: claims,
                expires: DateTime.UtcNow.AddHours(2),
                signingCredentials: creds
            );

            return Ok(new
            {
                email = user.Email,
                isActive = user.IsActive,
                roles = user.UserRoles.Select(ur => ur.Role.Name).ToList(),
                token = new JwtSecurityTokenHandler().WriteToken(token)
            });
        }
    }
}
