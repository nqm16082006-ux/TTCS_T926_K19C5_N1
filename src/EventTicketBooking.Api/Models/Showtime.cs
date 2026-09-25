using System;

namespace EventTicketBooking.Api.Models
{
    public class Showtime
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid EventId { get; set; }

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        public int AvailableSeats { get; set; }

        public Event Event { get; set; } = null!;
    }
}
