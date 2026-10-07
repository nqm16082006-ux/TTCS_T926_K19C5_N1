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
        private readonly Services.Interfaces.IPasswordHasher _passwordHasher;

        public AdminUsersController(AppDbContext context, Services.Interfaces.IPasswordHasher passwordHasher)
        {
            _context = context;
            _passwordHasher = passwordHasher;
        }

        /// <summary>
        /// Tạo tài khoản nhân viên mới và gán vai trò (S-28).
        /// Password được băm bằng Argon2id ở Backend và không bao giờ được trả về trong response.
        /// POST /api/admin/users
        /// </summary>
        [HttpPost]
        [ProducesResponseType(typeof(ApiResponse<AdminUserResponseDto>), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateUser([FromBody] CreateAdminUserDto dto)
        {
            var username = dto.Username?.Trim() ?? string.Empty;
            var email = dto.Email?.Trim() ?? string.Empty;

            if (username.Length == 0 || username.Length > 50)
                return BadRequest(ApiResponse<object>.FailureResult("Tên đăng nhập là bắt buộc và tối đa 50 ký tự."));
            if (email.Length == 0 || !email.Contains('@'))
                return BadRequest(ApiResponse<object>.FailureResult("Email không hợp lệ."));
            if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 8)
                return BadRequest(ApiResponse<object>.FailureResult("Mật khẩu phải có ít nhất 8 ký tự."));
            if (dto.Roles == null || dto.Roles.Count == 0 || dto.Roles.Any(string.IsNullOrWhiteSpace))
                return BadRequest(ApiResponse<object>.FailureResult("Tài khoản nhân viên phải có ít nhất một vai trò."));

            if (await _context.Users.AnyAsync(u => u.Username == username))
                return BadRequest(ApiResponse<object>.FailureResult("Tên đăng nhập đã tồn tại."));
            if (await _context.Users.AnyAsync(u => u.Email == email))
                return BadRequest(ApiResponse<object>.FailureResult("Email đã tồn tại."));

            var requestedRoleNames = dto.Roles.Select(r => r.Trim()).Distinct().ToList();
            var matchedRoles = await _context.Roles
                .Where(r => requestedRoleNames.Contains(r.Name))
                .ToListAsync();

            if (matchedRoles.Count != requestedRoleNames.Count)
            {
                var invalidNames = requestedRoleNames.Except(matchedRoles.Select(r => r.Name)).ToList();
                return BadRequest(ApiResponse<object>.FailureResult($"Các vai trò không tồn tại trong hệ thống: {string.Join(", ", invalidNames)}"));
            }

            // Tài khoản do Admin tạo: kích hoạt ngay để nhân viên đăng nhập được (không cần OTP).
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = email,
                PasswordHash = _passwordHasher.Hash(dto.Password),
                FullName = string.IsNullOrWhiteSpace(dto.FullName) ? null : dto.FullName.Trim(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Users.Add(user);

            foreach (var role in matchedRoles)
            {
                _context.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = role.Id,
                    AssignedAt = DateTime.UtcNow
                });
            }

            // Ghi audit cho việc gán role ban đầu khi tạo tài khoản (S-28).
            var actorId = GetCurrentUserId();
            if (!actorId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.FailureResult("Không xác định được danh tính người thao tác."));
            }

            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = actorId.Value,
                ActorUsername = await GetActorUsernameAsync(actorId.Value),
                TargetUserId = user.Id,
                OldRoles = string.Empty,
                NewRoles = string.Join(",", matchedRoles.Select(r => r.Name).OrderBy(n => n)),
                Action = "ROLE_CHANGED",
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            var responseDto = new AdminUserResponseDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                Roles = matchedRoles.Select(r => r.Name).OrderBy(n => n).ToList(),
                TotalEvents = 0
            };

            return CreatedAtAction(nameof(GetUsers), new { id = user.Id },
                ApiResponse<AdminUserResponseDto>.SuccessResult(responseDto, "Tạo tài khoản nhân viên thành công."));
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
            if (dto.Roles == null || dto.Roles.Count == 0 || dto.Roles.Any(string.IsNullOrWhiteSpace))
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

            // S-28: Actor phải lấy từ identity đã xác thực; không nhận actor từ body request.
            if (!currentUserId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.FailureResult("Không xác định được danh tính người thao tác."));
            }

            // Bảo vệ an toàn: Admin không được tự xóa quyền Admin của chính mình
            if (currentUserId.Value == id)
            {
                var hasAdmin = dto.Roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
                if (!hasAdmin)
                {
                    return BadRequest(ApiResponse<object>.FailureResult("Bạn không thể tự gỡ quyền Admin của chính tài khoản đang đăng nhập."));
                }
            }

            // Vai trò cũ trước khi thay đổi (dùng cho Audit Log S-28).
            var oldRoleNames = user.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => n).ToList();

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
            var targetRoleIds = matchedRoles.Select(r => r.Id).ToHashSet();
            _context.UserRoles.RemoveRange(user.UserRoles.Where(ur => !targetRoleIds.Contains(ur.RoleId)).ToList());

            foreach (var role in matchedRoles)
            {
                if (user.UserRoles.Any(ur => ur.RoleId == role.Id)) continue;
                _context.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = role.Id,
                    AssignedAt = DateTime.UtcNow
                });
            }

            // S-28: Ghi Audit Log cho mọi thay đổi role (Actor lấy từ identity đã xác thực).
            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = currentUserId.Value,
                ActorUsername = await GetActorUsernameAsync(currentUserId.Value),
                TargetUserId = user.Id,
                OldRoles = string.Join(",", oldRoleNames),
                NewRoles = string.Join(",", matchedRoles.Select(r => r.Name).OrderBy(n => n)),
                Action = "ROLE_CHANGED",
                CreatedAt = DateTime.UtcNow
            });

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
            if (!dto.IsActive)
            {
                user.VerificationCode = null;
                user.VerificationCodeExpiresAt = null;
            }

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
            if (await _context.Orders.AnyAsync(o => o.UserId == id))
                return BadRequest(ApiResponse<object>.FailureResult("Không thể xóa tài khoản đã có đơn hàng. Vui lòng khóa tài khoản."));
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

        /// <summary>
        /// Lấy username của Actor (từ CSDL) để lưu snapshot vào Audit Log.
        /// </summary>
        private async Task<string> GetActorUsernameAsync(Guid actorId)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == actorId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync() ?? string.Empty;
        }
    }
}
