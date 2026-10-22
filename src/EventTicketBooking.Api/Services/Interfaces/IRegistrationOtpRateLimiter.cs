using System.Threading.Tasks;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public record OtpRateLimitResult(
        bool Allowed,
        int RemainingAttempts,
        int RetryAfterSeconds,
        int RetryAfterMinutes);

    public interface IRegistrationOtpRateLimiter
    {
        /// <summary>
        /// Kiểm tra và ghi nhận lượt gửi lại mã xác nhận đăng ký cho email chỉ định.
        /// Giới hạn: tối đa 5 lần gửi lại mỗi giờ.
        /// </summary>
        Task<OtpRateLimitResult> CheckAndRecordResendAsync(string email);

        /// <summary>
        /// Lấy số lần gửi lại còn lại trong khung giờ hiện tại của email.
        /// </summary>
        Task<int> GetRemainingAttemptsAsync(string email);

        /// <summary>
        /// Đặt lại bộ đếm cho email (ví dụ khi kích hoạt tài khoản thành công).
        /// </summary>
        Task ResetAsync(string email);
    }
}
