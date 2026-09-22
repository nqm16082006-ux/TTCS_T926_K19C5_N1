using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace EventTicketBooking.Api.Middlewares
{
    /// <summary>
    /// Middleware kiểm tra quyền truy cập dựa trên vai trò (Role) của người dùng.
    /// Tuân thủ nghiêm ngặt ClaimsPrincipal chuẩn của ASP.NET Core:
    /// - Chưa đăng nhập (IsAuthenticated == false): trả về 401 Unauthorized.
    /// - Đã đăng nhập nhưng không có role yêu cầu: trả về 403 Forbidden.
    /// - Có role hợp lệ: cho phép request đi tiếp.
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
            if (endpoint == null)
            {
                await _next(context);
                return;
            }

            var requireRoleAttr = endpoint.Metadata.GetMetadata<RequireRoleAttribute>();
            if (requireRoleAttr == null)
            {
                // Endpoint không yêu cầu phân quyền theo role -> cho phép request đi tiếp
                await _next(context);
                return;
            }

            // 1. Kiểm tra xác thực (User Authentication)
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

            // 2. Kiểm tra vai trò (Role Authorization)
            var allowedRoles = requireRoleAttr.Roles;
            if (allowedRoles != null && allowedRoles.Length > 0)
            {
                bool hasRole = allowedRoles.Any(role =>
                    context.User.IsInRole(role) ||
                    context.User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == role));

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

            // 3. Đã xác thực và có role phù hợp -> cho phép request đi tiếp
            await _next(context);
        }
    }
}
