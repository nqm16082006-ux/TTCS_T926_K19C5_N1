using System;
using System.ComponentModel.DataAnnotations;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.DTOs.DiscountCodes
{
    public class CreateDiscountCodeRequest
    {
        [Required(ErrorMessage = "Mã giảm giá không được để trống")]
        [StringLength(50, ErrorMessage = "Mã giảm giá tối đa 50 ký tự")]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "Loại giảm giá không được để trống")]
        public DiscountType DiscountType { get; set; }

        [Required(ErrorMessage = "Giá trị giảm không được để trống")]
        public int DiscountValue { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Số lượt sử dụng phải lớn hơn 0")]
        public int UsageLimit { get; set; }

        [Required(ErrorMessage = "Hạn dùng không được để trống")]
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
