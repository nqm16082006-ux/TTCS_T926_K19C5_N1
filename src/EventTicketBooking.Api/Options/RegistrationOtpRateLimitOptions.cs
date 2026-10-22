namespace EventTicketBooking.Api.Options
{
    public class RegistrationOtpRateLimitOptions
    {
        public const string SectionName = "RegistrationOtpRateLimit";

        /// <summary>
        /// Giới hạn số lần gửi lại mã đăng ký mỗi giờ cho một email (mặc định 5 lần).
        /// </summary>
        public int MaxResendAttemptsPerHour { get; set; } = 5;

        /// <summary>
        /// Khung thời gian giới hạn tính bằng giây (mặc định 1 giờ = 3600 giây).
        /// </summary>
        public int WindowSeconds { get; set; } = 3600;
    }
}
