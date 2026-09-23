using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Middlewares
{
    /// <summary>
    /// Middleware kiểm tra phân quyền (RBAC) dựa trên vai trò trong cơ sở dữ liệu (Task TTKN-26):
    /// - Chỉ kiểm tra khi endpoint có gắn [RequireRoleAttribute], không làm ảnh hưởng đến các API khác của nhóm.
    /// - Chưa đăng nhập -> Trả về 401 Unauthorized kèm JSON thông báo chuẩn.
    /// - Truy vấn realtime từ bảng UserRoles & Roles theo UserId để kiểm tra quyền hạn.
    /// - Không đủ quyền -> Trả về 403 Forbidden kèm JSON thông báo chuẩn.
    /// - Đủ quyền -> Tiếp tục request (await _next(context)).
    /// </summary>
    public class RoleAuthorizationMiddleware
    {
        private readonly RequestDelegate _next;

        public RoleAuthorizationMiddleware(RequestDelegate next)
        {
            _next = next;
        }
public async Task InvokeAsync(HttpContext context)
{
    var endpoint = context.GetEndpoint();
    if (endpoint != null)
    {
        // 1. Kiểm tra xem Controller / Action có gắn thẻ [RequireEmailConfirmed] hay không
        var requiresEmailConfirmed = endpoint.Metadata.GetMetadata<RequireEmailConfirmedAttribute>();

        if (requiresEmailConfirmed != null)
        {
            // Lấy claim IsActive / IsEmailConfirmed từ User Token (sau khi đã decode JWT)
            var isActiveClaim = context.User.FindFirst("IsActive")?.Value;

            if (isActiveClaim == null || !bool.TryParse(isActiveClaim, out bool isActive) || !isActive)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new 
                { 
                    message = "Tài khoản chưa được kích hoạt. Vui lòng xác thực email để sử dụng tính năng này." 
                });
                return;
            }
        }
    }

    await _next(context);
}
        public async Task InvokeAsync(HttpContext context, AppDbContext dbContext)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint == null)
            {
                await _next(context);
                return;
            }

            var requireRoleAttr = endpoint.Metadata.GetMetadata<RequireRoleAttribute>();
            if (requireRoleAttr == null)
            {
                // Endpoint không yêu cầu phân quyền theo role -> Cho phép request đi tiếp
                await _next(context);
                return;
            }

            // 1. Kiểm tra xác thực (User Authentication)
            if (context.User?.Identity == null || !context.User.Identity.IsAuthenticated)
            {
                // Hỗ trợ đọc Bearer token từ Authorization header nếu pipeline chưa cấu hình JWT middleware
                var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var token = authHeader.Substring("Bearer ".Length).Trim();
                    try
                    {
                        var handler = new JwtSecurityTokenHandler();
                        if (handler.CanReadToken(token))
                        {
                            var jwt = handler.ReadJwtToken(token);
                            var identity = new ClaimsIdentity(jwt.Claims, "Bearer");
                            context.User = new ClaimsPrincipal(identity);
                        }
                    }
                    catch
                    {
                        // Token không hợp lệ -> Tiếp tục xuống xử lý 401
                    }
                }
            }

            if (context.User?.Identity == null || !context.User.Identity.IsAuthenticated)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    statusCode = StatusCodes.Status401Unauthorized,
                    message = "Unauthorized: Vui lòng đăng nhập để thực hiện thao tác này."
                });
                return;
            }

            // 2. Kiểm tra vai trò từ Database (RBAC) nếu có danh sách roles cụ thể
            var allowedRoles = requireRoleAttr.Roles;
            if (allowedRoles != null && allowedRoles.Length > 0)
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                  ?? context.User.FindFirst("id")?.Value
                                  ?? context.User.FindFirst("sub")?.Value;
                var emailClaim = context.User.FindFirst(ClaimTypes.Email)?.Value
                                 ?? context.User.FindFirst("email")?.Value;

                List<string> userRoles = new();

                // Truy vấn realtime từ CSDL bảng UserRoles và Roles đã tạo ở TTKN-20
                if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
                {
                    userRoles = await dbContext.UserRoles
                        .AsNoTracking()
                        .Where(ur => ur.UserId == userId)
                        .Select(ur => ur.Role.Name)
                        .ToListAsync();
                }
                else if (!string.IsNullOrEmpty(emailClaim))
                {
                    userRoles = await dbContext.UserRoles
                        .AsNoTracking()
                        .Where(ur => ur.User.Email == emailClaim)
                        .Select(ur => ur.Role.Name)
                        .ToListAsync();
                }

                // Kiểm tra xem user có ít nhất một trong các role được yêu cầu không
                bool hasRole = allowedRoles.Any(allowed =>
                    userRoles.Any(r => string.Equals(r, allowed, StringComparison.OrdinalIgnoreCase)) ||
                    context.User.IsInRole(allowed) ||
                    context.User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") &&
                                               string.Equals(c.Value, allowed, StringComparison.OrdinalIgnoreCase)));

                if (!hasRole)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        statusCode = StatusCodes.Status403Forbidden,
                        message = "Forbidden: Bạn không có quyền truy cập chức năng này."
                    });
                    return;
                }
            }

            // 3. Đã xác thực và có role hợp lệ -> Cho phép đi tiếp
            await _next(context);
        }
    }
}
