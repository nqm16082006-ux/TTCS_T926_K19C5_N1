using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.Models
{
    public class Event
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OwnerId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Location { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int TotalSeats { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public User Owner { get; set; } = null!;
        public ICollection<Showtime> Showtimes { get; set; } = new List<Showtime>();
    }
}
