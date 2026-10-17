namespace EventTicketBooking.Api.Options
{
    public class AuthSecurityOptions
    {
        public const string SectionName = "AuthSecurity";

        /// <summary>
        /// Số lần đăng nhập sai tối đa trước khi khóa tài khoản (Quy định cứng S-02: đúng 5 lần).
        /// </summary>
        public int AccountMaxFailures { get; set; } = 5;

        /// <summary>
        /// Số lần đăng nhập sai tối đa của một IP trước khi khóa IP (Mặc định 10 lần).
        /// </summary>
        public int IpMaxFailures { get; set; } = 10;

        /// <summary>
        /// Thời gian khóa tạm thời tính bằng phút (Quy định cứng S-02: 15 phút).
        /// </summary>
        public int LockoutMinutes { get; set; } = 15;

        /// <summary>
        /// Bắt buộc ngắt dịch vụ xác thực (Fail-closed) khi Redis không khả dụng.
        /// Mặc định: true (Production / Tests). Trong môi trường Development không có Redis: false (cho phép fallback xác thực CSDL Cloud).
        /// </summary>
        public bool FailClosedOnRedisDown { get; set; } = true;
    }
}
