using System;

namespace EventTicketBooking.Api.DTOs
{
    public class ShowtimeResponseDto
    {
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int AvailableSeats { get; set; }
    }
}
