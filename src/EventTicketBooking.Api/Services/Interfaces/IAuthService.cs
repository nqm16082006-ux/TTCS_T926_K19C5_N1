using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service xử lý nghiệp vụ xác thực đăng nhập người dùng (Task TTKN-25).
    /// </summary>
    public interface IAuthService
    {
        /// <summary>
        /// Xử lý đăng nhập bằng email và mật khẩu, kiểm soát đếm lỗi và khóa tạm thời qua Redis.
        /// </summary>
        Task<AuthResult> LoginAsync(LoginRequestDto request);
    }
}
