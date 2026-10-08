using System;

namespace EventTicketBooking.Api.Models;

public class SeatHolds
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SeatId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = "ACTIVE"; // ACTIVE, EXPIRED, RELEASED, CONVERTED
    public DateTime HeldAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    // Navigation properties
    public Seat Seat { get; set; } = null!;
    public User User { get; set; } = null!;
}
