using System;

namespace EventTicketBooking.Api.Models;

public class SeatHolds
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SeatId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = "ACTIVE"; // ACTIVE, EXPIRED, RELEASED
    public DateTime ExpiresAt { get; set; }
}
