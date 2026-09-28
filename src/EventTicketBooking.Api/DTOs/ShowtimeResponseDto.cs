using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class ShowtimeResponseDto
    {
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int AvailableSeats { get; set; }
        public EventTicketBooking.Api.Models.ShowtimeStatus Status { get; set; }
        public List<SeatCategoryPriceDto> SeatCategories { get; set; } = new();
        public bool CanOpenSale => Status == EventTicketBooking.Api.Models.ShowtimeStatus.Draft && AvailableSeats > 0;
        public bool CanCloseSale => Status == EventTicketBooking.Api.Models.ShowtimeStatus.OnSale;
        public string? StatusActionMessage { get; set; }
    }
}
