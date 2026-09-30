using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class OrderDto
    {
        public Guid Id { get; set; }
        public Guid ShowtimeId { get; set; }
        public string Status { get; set; } = null!;
        public int TotalAmount { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public List<OrderItemDto> Items { get; set; } = new();
    }

    public class OrderItemDto
    {
        public Guid Id { get; set; }
        public Guid SeatId { get; set; }
        public int Price { get; set; }
        public string? SeatName { get; set; }
        public string? CategoryName { get; set; }
    }

    /// <summary>
    /// DTO tối giản cho Task T-50 (trả về trạng thái đơn hàng để polling nhẹ dưới 100ms)
    /// </summary>
    public class OrderStatusResponseDto
    {
        public string Status { get; set; } = null!;
    }
}
