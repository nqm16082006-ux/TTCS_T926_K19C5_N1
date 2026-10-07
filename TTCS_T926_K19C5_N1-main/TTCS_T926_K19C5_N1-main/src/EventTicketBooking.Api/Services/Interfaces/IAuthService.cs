using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service xử lý nghiệp vụ xác thực người dùng.
    /// </summary>
    public interface IAuthService
    {
        /// <summary>
        /// Xử lý đăng nhập bằng email và mật khẩu.
        /// </summary>
        Task<AuthResult> LoginAsync(LoginRequestDto request);

        /// <summary>
        /// Tạo và lưu mã xác thực email.
        /// </summary>
        Task<string> GenerateAndSaveVerificationCodeAsync(User user);

        /// <summary>
        /// Kiểm tra mã xác thực email.
        /// </summary>
        Task<bool> VerifyEmailCodeAsync(string email, string code);
    }
}
