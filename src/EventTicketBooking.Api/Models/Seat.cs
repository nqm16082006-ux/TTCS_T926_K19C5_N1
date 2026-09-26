using System;

namespace EventTicketBooking.Api.Models
{
    public class Seat
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid ShowtimeId { get; set; }

        public Guid SeatCategoryId { get; set; }

        public string Row { get; set; } = string.Empty;

        public int SeatNumber { get; set; }

        public Showtime Showtime { get; set; } = null!;

        public SeatCategory SeatCategory { get; set; } = null!;
    }
}
