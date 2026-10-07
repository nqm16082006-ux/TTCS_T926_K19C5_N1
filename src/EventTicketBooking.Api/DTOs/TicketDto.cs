using System;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO thông tin vé điện tử trả về cho khách hàng (Story S-25 & S-26).
    /// Đáp ứng AC1: mã QR có chữ ký số (QrPayload), tên sự kiện, suất, hạng, và số ghế.
    /// </summary>
    public class TicketDto
    {
        public Guid Id { get; set; }
        public Guid OrderItemId { get; set; }
        public string TicketCode { get; set; } = string.Empty;

        private string? _qrPayload;

        /// <summary>
        /// Payload dùng để vẽ mã QR trên giao diện (Story S-25 & S-26).
        /// Ở Story S-26: Payload gồm mã vé, mã suất, phiên bản khoá và chữ ký số bất đối xứng (ECDSA NIST P-256).
        /// </summary>
        public string QrPayload
        {
            get => !string.IsNullOrEmpty(_qrPayload) ? _qrPayload : TicketCode;
            set => _qrPayload = value;
        }

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
