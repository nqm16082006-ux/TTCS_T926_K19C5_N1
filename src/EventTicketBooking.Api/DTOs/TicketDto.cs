using System;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO thông tin vé điện tử trả về cho khách hàng (Story S-25).
    /// Đáp ứng AC1: mã QR (TicketCode), tên sự kiện, suất, hạng, và số ghế.
    /// </summary>
    public class TicketDto
    {
        public Guid Id { get; set; }
        public Guid OrderItemId { get; set; }
        public string TicketCode { get; set; } = string.Empty;

        /// <summary>
        /// Payload dùng để vẽ mã QR trên giao diện. Ở Story S-25 đúng bằng TicketCode.
        /// </summary>
        public string QrPayload => TicketCode;

        public string? EventTitle { get; set; }
        public string? EventLocation { get; set; }
        public DateTime? ShowtimeStartTime { get; set; }
        public DateTime? ShowtimeEndTime { get; set; }
        public string? CategoryName { get; set; }
        public string? SeatRow { get; set; }
        public int SeatNumber { get; set; }
        public string SeatName => !string.IsNullOrEmpty(SeatRow) ? $"{SeatRow}{SeatNumber}" : $"Ghế #{SeatNumber}";
        public int Price { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
