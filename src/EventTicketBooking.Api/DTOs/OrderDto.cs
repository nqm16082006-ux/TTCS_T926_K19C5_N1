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
    }
}
