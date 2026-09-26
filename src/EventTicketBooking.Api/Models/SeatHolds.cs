using System;

namespace EventTicketBooking.Api.Models;

public class SeatHolds
{
    public int Id { get; set; }
    public int SeatId { get; set; }
    public int UserId { get; set; }
    public string Status { get; set; } = "ACTIVE"; // ACTIVE, EXPIRED, RELEASED
    public DateTime ExpiresAt { get; set; }
}
