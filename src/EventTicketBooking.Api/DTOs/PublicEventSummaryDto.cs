namespace EventTicketBooking.Api.DTOs;

public class PublicEventSummaryDto
{
    public Guid EventId { get; set; }
    public string EventTitle { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string EventLocation { get; set; } = "";
    public string OrganizerName { get; set; } = "";
    public DateTime SalesEndTime { get; set; }
    public DateTime StartTime { get; set; }
    public int ShowtimeCount { get; set; }
    public int MinPrice { get; set; }
}
