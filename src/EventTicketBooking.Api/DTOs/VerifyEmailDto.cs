using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    public class VerifyEmailDto
    {
        [Required(ErrorMessage = "Email là bắt buộc.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mã xác nhận là bắt buộc.")]
        public string Code { get; set; } = string.Empty;
    }
}