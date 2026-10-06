using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    public class GoogleAuthRequestDto
    {
        [Required(ErrorMessage = "Credential / ID Token từ Google là bắt buộc.")]
        public string Credential { get; set; } = string.Empty;

        /// <summary>
        /// Chế độ gọi: "register" (Đăng ký) hoặc "login" (Đăng nhập)
        /// </summary>
        public string? Mode { get; set; }
    }

    public class ResendOtpDto
    {
        [Required(ErrorMessage = "Email là bắt buộc.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        public string Email { get; set; } = string.Empty;
    }
}
