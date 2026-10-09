using System.Text.Json.Serialization;

namespace EventTicketBooking.Api.Models;

/// <summary>
/// Trạng thái ghế ngồi trong sự kiện (Task TTKN-127 / S-60)
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SeatStatus
{
    /// <summary>
    /// Ghế còn trống (chưa có người đặt)
    /// </summary>
    Available = 0,

    /// <summary>
    /// Ghế đang được giữ chỗ tạm thời
    /// </summary>
    Reserved = 1,

    /// <summary>
    /// Ghế đã được thanh toán và bán thành công
    /// </summary>
    Sold = 2
}
