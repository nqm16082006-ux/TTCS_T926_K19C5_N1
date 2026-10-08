using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    public class ShowtimeResponseDto
    {
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int AvailableSeats { get; set; }
        public int RemainingTickets { get; set; }
        public int AvailableTickets
        {
            get => RemainingTickets;
            set => RemainingTickets = value;
        }
        public int? ActualSeatCount { get; set; }
        public int SoldSeatCount { get; set; }
        public EventTicketBooking.Api.Models.ShowtimeStatus Status { get; set; }
        public List<SeatCategoryPriceDto> SeatCategories { get; set; } = new();
        public bool CanOpenSale => Status == EventTicketBooking.Api.Models.ShowtimeStatus.Draft && AvailableSeats > 0 &&
            (!ActualSeatCount.HasValue || (ActualSeatCount > 0 && SeatCategories.Count > 0 && SeatCategories.TrueForAll(c => c.Price is >= 0)));
        public bool CanCloseSale => Status == EventTicketBooking.Api.Models.ShowtimeStatus.OnSale;
        public string? StatusActionMessage { get; set; }
    }

    public class CreateShowtimeDto : IValidatableObject
    {
        [Required(ErrorMessage = "Thời gian bắt đầu là bắt buộc.")]
        public DateTime StartTime { get; set; }

        [Required(ErrorMessage = "Thời gian kết thúc là bắt buộc.")]
        public DateTime EndTime { get; set; }

        public int AvailableSeats { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Số lượng vé tối đa mỗi người mua phải lớn hơn 0.")]
        public int? MaxTicketsPerUser { get; set; }

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

    public class UpdateShowtimeDto : IValidatableObject
    {
        [Required(ErrorMessage = "Thời gian bắt đầu là bắt buộc.")]
        public DateTime StartTime { get; set; }

        [Required(ErrorMessage = "Thời gian kết thúc là bắt buộc.")]
        public DateTime EndTime { get; set; }

        public int? AvailableSeats { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Số lượng vé tối đa mỗi người mua phải lớn hơn 0.")]
        public int? MaxTicketsPerUser { get; set; }

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

