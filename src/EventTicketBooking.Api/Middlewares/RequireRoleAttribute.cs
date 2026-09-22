using System;

namespace EventTicketBooking.Api.Middlewares
{
    /// <summary>
    /// Attribute khai báo metadata vai trò (Role) cố định trong code cho endpoint.
    /// - [RequireRole] : Yêu cầu người dùng phải xác thực (đã đăng nhập).
    /// - [RequireRole("Admin")] : Yêu cầu người dùng phải đăng nhập và có vai trò Admin.
    /// - [RequireRole("Admin", "Staff")] : Yêu cầu có một trong các vai trò được liệt kê.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class RequireRoleAttribute : Attribute
    {
        public string[] Roles { get; }

        public RequireRoleAttribute(params string[] roles)
        {
            Roles = roles ?? Array.Empty<string>();
        }
    }
}