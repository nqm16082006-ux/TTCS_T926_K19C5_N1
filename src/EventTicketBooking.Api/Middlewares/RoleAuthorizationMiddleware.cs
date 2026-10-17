using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Middlewares
{
    /// <summary>
    /// Middleware kiểm tra phân quyền (RBAC) và Single Role dựa trên cơ sở dữ liệu (Story S-02):
    /// - [AllowAnonymous]: Cho phép request công khai đi tiếp mà không bắt buộc xác thực.
    /// - Default Deny: Tuyến đường không có AllowAnonymous và không có RequireRole -> từ chối với HTTP 403 Forbidden.
    /// - Chưa đăng nhập / Token hết hạn -> 401 Unauthorized.
    /// - Single Role Fail-Closed: Người dùng có != 1 vai trò trong DB -> từ chối với HTTP 403 Forbidden.
    /// - Không đủ quyền -> 403 Forbidden và ghi Security Audit Log có cấu trúc.
    /// </summary>
    public class RoleAuthorizationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RoleAuthorizationMiddleware>? _logger;

        public RoleAuthorizationMiddleware(RequestDelegate next, ILogger<RoleAuthorizationMiddleware>? logger = null)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, AppDbContext dbContext, IConfiguration configuration)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint == null)
            {
                await _next(context);
                return;
            }

            // 1. Endpoint có thẻ [AllowAnonymous] -> Cho phép đi tiếp (AC5 Bypass)
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
            {
                await _next(context);
                return;
            }

            // 2. Tuyến đường đặc biệt (/metrics, SignalR hubs ĐÃ audit)
            // S-02 Hardening: KHÔNG wildcard /hubs/* để tránh vô tình bypass các hub nhạy cảm mới trong tương lai.
            var path = context.Request.Path;
            bool isAuditedSpecialRoute = path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) ||
                                         path.StartsWithSegments("/api/public", StringComparison.OrdinalIgnoreCase) ||
                                         path.Equals("/hubs/seat-status", StringComparison.OrdinalIgnoreCase) ||
                                         path.StartsWithSegments("/hubs/seat-status", StringComparison.OrdinalIgnoreCase) ||
                                         path.Equals("/hubs/system-alerts", StringComparison.OrdinalIgnoreCase) ||
                                         path.StartsWithSegments("/hubs/system-alerts", StringComparison.OrdinalIgnoreCase);

            if (isAuditedSpecialRoute)
            {
                await _next(context);
                return;
            }

            var requiresEmailConfirmed = endpoint.Metadata.GetMetadata<RequireEmailConfirmedAttribute>();
            var requireRoleAttr = endpoint.Metadata.GetMetadata<RequireRoleAttribute>();

            // 3. DEFAULT DENY: Tuyến đường không được khai báo AllowAnonymous và cũng không có RequireRole -> Chặn 403
            if (requiresEmailConfirmed == null && requireRoleAttr == null)
            {
                LogSecurity403(context, "Anonymous", "None", "DefaultDeny: Tuyến đường được bảo vệ nhưng chưa khai báo quyền truy cập.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    message = "Forbidden: Tuyến đường được bảo vệ nhưng chưa khai báo quyền truy cập."
                });
                return;
            }

            // 4. Kiểm tra xác thực (User Authentication)
            if (context.User?.Identity == null || !context.User.Identity.IsAuthenticated)
            {
                // Hỗ trợ đọc Bearer token từ Authorization header nếu pipeline chưa chạy qua JWT bearer middleware
                var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var token = authHeader.Substring("Bearer ".Length).Trim();
                    try
                    {
                        var handler = new JwtSecurityTokenHandler();
                        if (handler.CanReadToken(token))
                        {
                            context.User = handler.ValidateToken(token,
                                EventTicketBooking.Api.Services.JwtValidation.Parameters(configuration), out _);
                        }
                    }
                    catch
                    {
                        // Token không hợp lệ hoặc đã hết hạn -> Tiếp tục xuống xử lý 401
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

            var userIdValue = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? context.User.FindFirst("id")?.Value
                              ?? context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            var user = Guid.TryParse(userIdValue, out var authenticatedUserId)
                ? await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == authenticatedUserId)
                : null;

            if (user == null || !user.IsActive)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    statusCode = StatusCodes.Status401Unauthorized,
                    message = "Tài khoản không tồn tại hoặc đã bị khóa."
                });
                return;
            }

            // 5. Nạp danh sách vai trò realtime từ CSDL (Authoritative RBAC)
            var currentRoles = await dbContext.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == authenticatedUserId)
                .Select(ur => ur.Role.Name)
                .ToListAsync();

            // 6. SINGLE ROLE - BACKEND: Kiểm tra tài khoản phải có đúng 1 vai trò duy nhất khi truy cập tài nguyên yêu cầu quyền (hoặc chặn nếu >1 role)
            if (currentRoles.Count > 1 || (requireRoleAttr != null && currentRoles.Count != 1))
            {
                LogSecurity403(context, user.Id.ToString(), string.Join(",", currentRoles),
                    $"InvalidRoleCount: Tài khoản có {currentRoles.Count} vai trò trong CSDL. Vi phạm nguyên tắc Single Role.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    message = "Forbidden: Dữ liệu phân quyền của tài khoản không hợp lệ."
                });
                return;
            }

            var currentIdentity = new ClaimsIdentity(context.User.Claims.Where(c => c.Type != ClaimTypes.Role && c.Type != "role"), "Bearer");
            currentIdentity.AddClaims(currentRoles.Select(role => new Claim(ClaimTypes.Role, role)));
            context.User = new ClaimsPrincipal(currentIdentity);

            // 7. Kiểm tra RequireEmailConfirmed
            if (requiresEmailConfirmed != null)
            {
                if (!user.IsActive)
                {
                    LogSecurity403(context, user.Id.ToString(), currentRoles[0], "RequireEmailConfirmed: Tài khoản chưa kích hoạt email.");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        statusCode = StatusCodes.Status403Forbidden,
                        message = "Tài khoản chưa được kích hoạt. Vui lòng xác thực email để sử dụng tính năng này."
                    });
                    return;
                }
            }

            // 8. Kiểm tra vai trò từ Database (RBAC) nếu có danh sách roles cụ thể
            var allowedRoles = requireRoleAttr?.Roles;
            if (allowedRoles != null && allowedRoles.Length > 0)
            {
                bool hasRole = allowedRoles.Any(allowed =>
                    currentRoles.Any(r => string.Equals(r, allowed, StringComparison.OrdinalIgnoreCase)) ||
                    context.User.IsInRole(allowed) ||
                    context.User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") &&
                                               string.Equals(c.Value, allowed, StringComparison.OrdinalIgnoreCase)));

                if (!hasRole)
                {
                    LogSecurity403(context, user.Id.ToString(), currentRoles[0],
                        $"MissingRequiredRole: Yêu cầu [{string.Join(", ", allowedRoles)}] nhưng người dùng có [{currentRoles[0]}].");
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

            // 9. Đã xác thực và có role hợp lệ -> Cho phép đi tiếp
            await _next(context);
        }

        private void LogSecurity403(HttpContext context, string userId, string role, string reason)
        {
            var logger = _logger ?? context.RequestServices?.GetService<ILogger<RoleAuthorizationMiddleware>>();
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? string.Empty;
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            var traceId = context.TraceIdentifier;

            logger?.LogWarning(
                "[SECURITY AUDIT 403] Từ chối truy cập do phân quyền. Thời gian: {Timestamp} | UserId: {UserId} | Role: {Role} | Method: {Method} | Path: {Path} | Reason: {Reason} | IP: {IP} | TraceId: {TraceId}",
                DateTime.UtcNow.ToString("o"), userId, role, method, path, reason, ip, traceId);
        }
    }
}
