namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO kết quả trả về khi đăng nhập thành công.
    /// </summary>
    public class LoginResponseDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = "Bearer";
        public int ExpiresIn { get; set; }
        public UserDto User { get; set; } = new();
    }
}
