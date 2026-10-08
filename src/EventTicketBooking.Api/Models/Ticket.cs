using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi vé điện tử gắn với từng ghế trong đơn hàng (Story S-25).
    /// Quan hệ 1:1 với OrderItem. Mã vé TicketCode ngẫu nhiên bảo mật, không tuần tự.
    /// </summary>
    public class Ticket
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OrderItemId { get; set; }
        public string TicketCode { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public OrderItem OrderItem { get; set; } = null!;
    }
}
