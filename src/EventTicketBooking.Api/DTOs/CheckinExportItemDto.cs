using System;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO thông tin bản ghi soát vé phục vụ xuất danh sách khách vào cửa theo suất diễn (Story S-62).
    /// </summary>
    public class CheckinExportItemDto
    {
        /// <summary>
        /// Mã vé điện tử bảo mật.
        /// </summary>
        public string TicketCode { get; set; } = string.Empty;

        /// <summary>
        /// Số ghế / vị trí ghế (ví dụ: A12, B5...).
        /// </summary>
        public string SeatNumber { get; set; } = string.Empty;

        /// <summary>
        /// Bí danh cho SeatNumber (SeatCode).
        /// </summary>
        public string SeatCode
        {
            get => SeatNumber;
            set => SeatNumber = value;
        }

        /// <summary>
        /// Thời gian khách vào cửa (Check-in time).
        /// </summary>
        public DateTimeOffset? CheckinTime { get; set; }

        /// <summary>
        /// Tên cửa/cổng soát vé vào (GateName).
        /// </summary>
        public string? GateName { get; set; }

        /// <summary>
        /// Bí danh cho GateName (DoorName).
        /// </summary>
        public string? DoorName
        {
            get => GateName;
            set => GateName = value;
        }

        /// <summary>
        /// Cờ đánh dấu bản ghi soát vé có xung đột hay không.
        /// </summary>
        public bool IsConflict { get; set; }

        /// <summary>
        /// Bí danh cho IsConflict (HasConflict).
        /// </summary>
        public bool HasConflict
        {
            get => IsConflict;
            set => IsConflict = value;
        }

        /// <summary>
        /// Ghi chú chi tiết lý do xung đột nếu có.
        /// </summary>
        public string? ConflictNote { get; set; }

        /// <summary>
        /// Trạng thái đơn hàng của vé (phục vụ đối soát).
        /// </summary>
        public string? OrderStatus { get; set; }
        public string EntryType { get; set; } = "Lần đầu";
        public string Result { get; set; } = "Đã vào";
    }
}
