using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services;

/// <summary>
/// Service tạo và xuất sơ đồ ghế ra file PDF (Task TTKN-127 / S-60)
/// </summary>
public interface ISeatMapPdfService
{
    /// <summary>
    /// Xuất sơ đồ ghế ra file PDF hoàn chỉnh (1 trang duy nhất) từ request
    /// </summary>
    Task<byte[]> ExportSeatMapPdfAsync(SeatMapExportRequestDto? request);

    /// <summary>
    /// Xuất sơ đồ ghế ra file PDF theo EventId
    /// </summary>
    Task<byte[]> ExportSeatMapPdfByEventIdAsync(Guid eventId);

    /// <summary>
    /// Lấy dữ liệu báo cáo sơ đồ ghế đã phân loại và tính toán
    /// </summary>
    Task<SeatMapReportDto> GetSeatMapReportDataAsync(SeatMapExportRequestDto? request);
}
