using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs.DiscountCodes
{
    public class ApplyDiscountRequest
    {
        [Required(ErrorMessage = "Mã giảm giá không được để trống")]
        [StringLength(50, ErrorMessage = "Mã giảm giá tối đa 50 ký tự")]
        public string Code { get; set; } = string.Empty;
    }
}
