namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// Đối tượng kết quả xử lý đăng nhập nội bộ giữa Service và Controller.
    /// </summary>
    public class AuthResult
    {
        public bool Success { get; set; }
        public bool IsLocked { get; set; }
        public int StatusCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public LoginResponseDto? Data { get; set; }

        public static AuthResult Ok(LoginResponseDto data) => new()
        {
            Success = true,
            IsLocked = false,
            StatusCode = 200,
            Message = "Đăng nhập thành công.",
            Data = data
        };

        public static AuthResult Unauthorized(string message = "Email hoặc mật khẩu không chính xác.") => new()
        {
            Success = false,
            IsLocked = false,
            StatusCode = 401,
            Message = message
        };

        public static AuthResult Locked(string message = "Tài khoản của bạn đã bị khóa tạm thời trong 15 phút do nhập sai quá nhiều lần. Vui lòng thử lại sau.") => new()
        {
            Success = false,
            IsLocked = true,
            StatusCode = 423, // 423 Locked
            Message = message
        };
    }
}
