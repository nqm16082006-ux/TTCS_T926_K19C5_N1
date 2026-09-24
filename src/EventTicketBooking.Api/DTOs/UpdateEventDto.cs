using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    public class UpdateEventDto : IValidatableObject
    {
        [Required(ErrorMessage = "Tên sự kiện là bắt buộc.")]
        [MaxLength(250, ErrorMessage = "Tên sự kiện không được vượt quá 250 ký tự.")]
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Required(ErrorMessage = "Địa điểm là bắt buộc.")]
        [MaxLength(500, ErrorMessage = "Địa điểm không được vượt quá 500 ký tự.")]
        public string Location { get; set; } = string.Empty;

        [Required(ErrorMessage = "Thời gian bắt đầu là bắt buộc.")]
        public DateTime StartTime { get; set; }

        [Required(ErrorMessage = "Thời gian kết thúc là bắt buộc.")]
        public DateTime EndTime { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Tổng số ghế phải lớn hơn 0.")]
        public int TotalSeats { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (EndTime <= StartTime)
            {
                yield return new ValidationResult(
                    "Thời gian kết thúc phải lớn hơn thời gian bắt đầu.",
                    new[] { nameof(EndTime) }
                );
            }
        }
    }
}
