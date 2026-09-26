using System;
using System.Text.Json.Serialization;

namespace EventTicketBooking.Api.Models
{
    public class SeatCategory
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid ShowtimeId { get; set; }

        public string Name { get; set; } = string.Empty;

        public decimal Price { get; set; }

        [JsonIgnore]
        public Showtime Showtime { get; set; } = null!;
    }
}
