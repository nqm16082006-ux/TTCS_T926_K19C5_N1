namespace EventTicketBooking.Api.DTOs;

public class ShowtimeSalesReportDto
{
    public DateTime GeneratedAt { get; set; }
    public List<ShowtimeSalesDto> Showtimes { get; set; } = new();
}

public class ShowtimeSalesDto
{
    public Guid ShowtimeId { get; set; }
    public string EventTitle { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int TicketSoldCount { get; set; }
    public int HeldSeatCount { get; set; }
    public int AvailableSeatCount { get; set; }
    public List<SeatCategorySalesDto> RevenueByCategory { get; set; } = new();
}

public class SeatCategorySalesDto
{
    public Guid SeatCategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TicketSoldCount { get; set; }
    public long Revenue { get; set; }
}
