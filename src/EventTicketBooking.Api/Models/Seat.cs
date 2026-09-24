namespace EventTicketBooking.Api.Models;

public class Seat
{
    public int Id { get; set; }
    public int ShowtimeId { get; set; }
    public string Status { get; set; } = "AVAILABLE"; // AVAILABLE, HELD, SOLD
}