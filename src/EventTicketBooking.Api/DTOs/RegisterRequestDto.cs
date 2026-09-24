using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    public class RegisterRequestDto
    {
        [Required(ErrorMessage = "Email là bắt buộc.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mật khẩu là bắt buộc.")]
        [MinLength(8, ErrorMessage = "Mật khẩu phải dài ít nhất 8 ký tự.")]
        public string Password { get; set; } = string.Empty;

        public string? FullName { get; set; }
    }
}
