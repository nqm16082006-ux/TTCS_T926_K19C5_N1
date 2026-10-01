using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class OrderDto
    {
        public Guid Id { get; set; }
        public Guid ShowtimeId { get; set; }
        public string? EventTitle { get; set; }
        public string? EventLocation { get; set; }
        public DateTime? ShowtimeStartTime { get; set; }
        public DateTime? ShowtimeEndTime { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerEmail { get; set; }
        public string Status { get; set; } = null!;
        public int TotalAmount { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
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

    public class OrderStatusResponseDto
    {
        public string Status { get; set; } = string.Empty;
    }
}
