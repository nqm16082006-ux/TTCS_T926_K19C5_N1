using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO tiếp nhận dữ liệu đăng nhập bằng email và mật khẩu (Task TTKN-25).
    /// </summary>
    public class LoginRequestDto
    {
        [Required(ErrorMessage = "Email không được để trống.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mật khẩu không được để trống.")]
        public string Password { get; set; } = string.Empty;
    }
}
