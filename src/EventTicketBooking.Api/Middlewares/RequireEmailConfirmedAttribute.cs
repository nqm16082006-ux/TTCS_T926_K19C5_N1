using System;

namespace EventTicketBooking.Api.Middlewares
{
    /// <summary>
    /// Attribute khai báo yêu cầu tài khoản phải kích hoạt/xác thực email mới được truy cập API (AC4).
    /// - [RequireEmailConfirmed] : Chặn người dùng chưa xác nhận email.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class RequireEmailConfirmedAttribute : Attribute
    {
    }
}