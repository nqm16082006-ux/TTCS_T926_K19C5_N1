using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller Quản trị Người dùng dành riêng cho Admin:
    /// - Xem danh sách tất cả tài khoản trong hệ thống.
    /// - Tìm kiếm người dùng theo email, username, họ tên.
    /// - Phân quyền / Cập nhật vai trò (Customer, Organizer, Admin).
    /// - Khóa / Kích hoạt tài khoản.
    /// </summary>
    [ApiController]
    [Route("api/admin/users")]
    [RequireRole("Admin")]
    public class AdminUsersController : ControllerBase
    {
        private readonly AppDbContext _context;

        public AdminUsersController(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Lấy danh sách tất cả tài khoản người dùng trong hệ thống kèm vai trò và thống kê.
        /// GET /api/admin/users?search=...&role=...
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<List<AdminUserResponseDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetUsers([FromQuery] string? search, [FromQuery] string? role)
        {
            var query = _context.Users
                .AsNoTracking()
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .Include(u => u.Events)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                query = query.Where(u => u.Email.ToLower().Contains(s)
                                      || u.Username.ToLower().Contains(s)
                                      || (u.FullName != null && u.FullName.ToLower().Contains(s)));
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                var targetRole = role.Trim().ToLower();
                query = query.Where(u => u.UserRoles.Any(ur => ur.Role.Name.ToLower() == targetRole));
            }

            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .ToListAsync();

            var result = users.Select(u => new AdminUserResponseDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                FullName = u.FullName,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                Roles = u.UserRoles.Select(ur => ur.Role.Name).OrderBy(r => r).ToList(),
                TotalEvents = u.Events.Count
            }).ToList();

            return Ok(ApiResponse<List<AdminUserResponseDto>>.SuccessResult(result, "Lấy danh sách người dùng thành công."));
        }

        /// <summary>
        /// Lấy danh sách các vai trò (roles) hiện có trong hệ thống.
        /// GET /api/admin/users/roles
        /// </summary>
        [HttpGet("roles")]
        [ProducesResponseType(typeof(ApiResponse<List<string>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAvailableRoles()
        {
            var roles = await _context.Roles
                .AsNoTracking()
                .Select(r => r.Name)
                .Distinct()
                .ToListAsync();

            return Ok(ApiResponse<List<string>>.SuccessResult(roles, "Lấy danh sách vai trò thành công."));
        }

        /// <summary>
        /// Phân quyền / Cập nhật vai trò cho người dùng.
        /// PUT /api/admin/users/{id}/roles
        /// </summary>
        [HttpPut("{id:guid}/roles")]
        [ProducesResponseType(typeof(ApiResponse<AdminUserResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateUserRoles(Guid id, [FromBody] UpdateUserRolesDto dto)
        {
            if (dto.Roles == null || dto.Roles.Count == 0)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Người dùng phải có ít nhất một vai trò."));
            }

            var user = await _context.Users
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .Include(u => u.Events)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy người dùng."));
            }

            var currentUserId = GetCurrentUserId();

            // Bảo vệ an toàn: Admin không được tự xóa quyền Admin của chính mình
            if (currentUserId.HasValue && currentUserId.Value == id)
            {
                var hasAdmin = dto.Roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
                if (!hasAdmin)
                {
                    return BadRequest(ApiResponse<object>.FailureResult("Bạn không thể tự gỡ quyền Admin của chính tài khoản đang đăng nhập."));
                }
            }

            // Lấy các vai trò hợp lệ trong DB
            var requestedRoleNames = dto.Roles.Select(r => r.Trim()).Distinct().ToList();
            var matchedRoles = await _context.Roles
                .Where(r => requestedRoleNames.Contains(r.Name))
                .ToListAsync();

            if (matchedRoles.Count != requestedRoleNames.Count)
            {
                var foundNames = matchedRoles.Select(r => r.Name).ToList();
                var invalidNames = requestedRoleNames.Except(foundNames).ToList();
                return BadRequest(ApiResponse<object>.FailureResult($"Các vai trò không tồn tại trong hệ thống: {string.Join(", ", invalidNames)}"));
            }

            // Cập nhật quan hệ UserRoles
            _context.UserRoles.RemoveRange(user.UserRoles);

            foreach (var role in matchedRoles)
            {
                _context.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = role.Id,
                    AssignedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            // Reload user để trả về dữ liệu mới nhất
            await _context.Entry(user).Collection(u => u.UserRoles).Query().Include(ur => ur.Role).LoadAsync();

            var responseDto = new AdminUserResponseDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                Roles = user.UserRoles.Select(ur => ur.Role.Name).OrderBy(r => r).ToList(),
                TotalEvents = user.Events.Count
            };

            return Ok(ApiResponse<AdminUserResponseDto>.SuccessResult(responseDto, "Cập nhật phân quyền người dùng thành công."));
        }

        /// <summary>
        /// Khóa hoặc kích hoạt tài khoản người dùng.
        /// PATCH /api/admin/users/{id}/status
        /// </summary>
        [HttpPatch("{id:guid}/status")]
        [ProducesResponseType(typeof(ApiResponse<AdminUserResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateUserStatus(Guid id, [FromBody] UpdateUserStatusDto dto)
        {
            var user = await _context.Users
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .Include(u => u.Events)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy người dùng."));
            }

            var currentUserId = GetCurrentUserId();
            if (currentUserId.HasValue && currentUserId.Value == id && !dto.IsActive)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Bạn không thể tự khóa tài khoản Admin của chính mình."));
            }

            user.IsActive = dto.IsActive;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var responseDto = new AdminUserResponseDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                Roles = user.UserRoles.Select(ur => ur.Role.Name).OrderBy(r => r).ToList(),
                TotalEvents = user.Events.Count
            };

            var msg = dto.IsActive ? "Đã kích hoạt tài khoản thành công." : "Đã khóa tài khoản thành công.";
            return Ok(ApiResponse<AdminUserResponseDto>.SuccessResult(responseDto, msg));
        }

        /// <summary>
        /// Xóa tài khoản người dùng khỏi hệ thống.
        /// DELETE /api/admin/users/{id}
        /// </summary>
        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteUser(Guid id)
        {
            var user = await _context.Users
                .Include(u => u.UserRoles)
                .Include(u => u.Events)
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy người dùng."));
            }

            var currentUserId = GetCurrentUserId();
            if (currentUserId.HasValue && currentUserId.Value == id)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Bạn không thể tự xóa tài khoản Admin của chính mình."));
            }

            if (user.Events.Any())
            {
                return BadRequest(ApiResponse<object>.FailureResult("Không thể xóa người dùng này vì họ đã tạo sự kiện. Vui lòng chuyển quyền hoặc xóa sự kiện trước."));
            }

            _context.UserRoles.RemoveRange(user.UserRoles);
            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return Ok(ApiResponse<object>.SuccessResult(null, "Đã xóa tài khoản người dùng thành công."));
        }

        private Guid? GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? User.FindFirst("id")?.Value
                              ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
            {
                return userId;
            }
            return null;
        }
    }
}
