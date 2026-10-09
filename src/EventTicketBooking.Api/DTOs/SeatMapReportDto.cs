namespace EventTicketBooking.Api.DTOs;

/// <summary>
/// Dữ liệu tổng hợp báo cáo sơ đồ ghế phục vụ render PDF hoặc trả về qua API
/// </summary>
public class SeatMapReportDto
{
    public Guid EventId { get; set; }
    public string EventTitle { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public string HallName { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    public int TotalSeats { get; set; }
    public int SoldCount { get; set; }
    public int ReservedCount { get; set; }
    public int AvailableCount { get; set; }

    public double SoldPercentage => TotalSeats > 0 ? Math.Round((double)SoldCount / TotalSeats * 100, 1) : 0;
    public double ReservedPercentage => TotalSeats > 0 ? Math.Round((double)ReservedCount / TotalSeats * 100, 1) : 0;
    public double AvailablePercentage => TotalSeats > 0 ? Math.Round((double)AvailableCount / TotalSeats * 100, 1) : 0;

    public decimal TotalRevenue { get; set; }

    /// <summary>
    /// Danh sách các hàng ghế đã được gom nhóm theo hàng
    /// </summary>
    public List<SeatRowGroupDto> Rows { get; set; } = new();
}

public class SeatRowGroupDto
{
    public string RowName { get; set; } = string.Empty;
    public List<SeatDto> Seats { get; set; } = new();
}
