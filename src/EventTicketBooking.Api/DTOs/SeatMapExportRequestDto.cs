namespace EventTicketBooking.Api.DTOs;

/// <summary>
/// Yêu cầu xuất sơ đồ ghế ra file PDF (Task TTKN-127)
/// </summary>
public class SeatMapExportRequestDto
{
    /// <summary>
    /// Mã định danh sự kiện (nếu có, hệ thống có thể truy vấn thêm thông tin từ DB)
    /// </summary>
    public Guid? EventId { get; set; }

    /// <summary>
    /// Tên sự kiện
    /// </summary>
    public string? EventTitle { get; set; }

    /// <summary>
    /// Địa điểm tổ chức
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Thời gian diễn ra sự kiện
    /// </summary>
    public DateTime? EventDate { get; set; }

    /// <summary>
    /// Tên hội trường / phòng chiếu (Ví dụ: Khán phòng A, Grand Hall, Cinema 01)
    /// </summary>
    public string? HallName { get; set; }

    /// <summary>
    /// Danh sách chi tiết các ghế và trạng thái tô màu.
    /// Nếu để trống, hệ thống sẽ tự động tổng hợp dựa theo sự kiện hoặc nạp dữ liệu mẫu thực tế.
    /// </summary>
    public List<SeatDto>? Seats { get; set; }
}
