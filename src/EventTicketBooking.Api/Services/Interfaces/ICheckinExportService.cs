using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Giao diện dịch vụ xuất danh sách khách vào cửa theo suất diễn (Story S-62).
    /// </summary>
    public interface ICheckinExportService
    {
        /// <summary>
        /// Truy vấn dữ liệu soát vé từ CSDL theo ShowId và xử lý các cờ xung đột.
        /// </summary>
        /// <param name="query">Tham số tìm kiếm và bộ lọc (ShowId, Gate, OnlyConflicts...)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Danh sách các bản ghi vào cửa có gắn cờ xung đột nếu có</returns>
        Task<List<CheckinExportItemDto>> GetCheckinExportDataAsync(ExportCheckinListQuery query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Đóng gói danh sách bản ghi thành mảng byte CSV chuẩn hóa UTF-8 kèm BOM (mở không lỗi font tiếng Việt trên Excel).
        /// </summary>
        /// <param name="items">Danh sách bản ghi soát vé</param>
        /// <returns>Mảng byte CSV với UTF-8 BOM</returns>
        byte[] GenerateCsvBytes(IEnumerable<CheckinExportItemDto> items);

        /// <summary>
        /// Thực hiện trọn gói truy vấn và xuất tệp CSV dạng mảng byte.
        /// </summary>
        /// <param name="query">Tham số tìm kiếm và bộ lọc</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Mảng byte nội dung file CSV</returns>
        Task<byte[]> ExportCheckinCsvAsync(ExportCheckinListQuery query, CancellationToken cancellationToken = default);
    }
}
