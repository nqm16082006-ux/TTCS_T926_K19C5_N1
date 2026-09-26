using System;

namespace EventTicketBooking.Api.DTOs
{
    public class PublicShowtimeDto
    {
        public Guid Id { get; set; }

        public Guid EventId { get; set; }

        public string EventTitle { get; set; } = string.Empty;

        public string? EventDescription { get; set; }

        public string EventLocation { get; set; } = string.Empty;

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        public int AvailableSeats { get; set; }

        public string Status { get; set; } = string.Empty;

        public decimal MinPrice { get; set; }

        public decimal MaxPrice { get; set; }
    }
}
