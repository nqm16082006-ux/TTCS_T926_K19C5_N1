using System;

namespace EventTicketBooking.Api.Middlewares
{
    /// <summary>
    /// Attribute khai báo yêu cầu xác thực và phân quyền theo Vai trò (Roles).
    /// - [RequireRole] : Yêu cầu đã xác thực (đã đăng nhập, bất kể vai trò gì).
    /// - [RequireRole("Admin")] : Yêu cầu người dùng phải có vai trò Admin.
    /// - [RequireRole("Organizer", "Admin")] : Yêu cầu người dùng có ít nhất một trong các vai trò được chỉ định.
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
