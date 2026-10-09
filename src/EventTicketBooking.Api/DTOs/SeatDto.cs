using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.DTOs;

/// <summary>
/// DTO đại diện cho một ghế trong sơ đồ sự kiện (Task TTKN-127)
/// </summary>
public class SeatDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Mã ghế hiển thị (Ví dụ: A1, B10, VIP-01)
    /// </summary>
    public string SeatCode { get; set; } = string.Empty;

    /// <summary>
    /// Ký hiệu hàng ghế (Ví dụ: A, B, C...)
    /// </summary>
    public string Row { get; set; } = string.Empty;

    /// <summary>
    /// Số thứ tự ghế trong hàng (1, 2, 3...)
    /// </summary>
    public int Number { get; set; }

    /// <summary>
    /// Trạng thái của ghế: Available (Trống), Reserved (Giữ chỗ), Sold (Đã bán)
    /// </summary>
    public SeatStatus Status { get; set; } = SeatStatus.Available;

    /// <summary>
    /// Loại ghế (VIP, Standard, Economy, v.v.)
    /// </summary>
    public string? SeatType { get; set; } = "Standard";

    /// <summary>
    /// Đơn giá ghế
    /// </summary>
    public decimal Price { get; set; }

    /// <summary>
    /// Mã hiển thị ưu tiên: SeatCode hoặc ghép Row + Number
    /// </summary>
    public string DisplayCode => !string.IsNullOrWhiteSpace(SeatCode) 
        ? SeatCode 
        : $"{Row}{Number}";
}
