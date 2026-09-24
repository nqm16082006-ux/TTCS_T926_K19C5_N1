using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class EventResponseDto
    {
        public Guid Id { get; set; }
        public Guid OwnerId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Location { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int TotalSeats { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<ShowtimeResponseDto> Showtimes { get; set; } = new List<ShowtimeResponseDto>();
    }
}
