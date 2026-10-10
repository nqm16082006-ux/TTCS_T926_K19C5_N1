using System;

namespace EventTicketBooking.Api.Models
{
    public class OrderItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OrderId { get; set; }
        public Guid SeatId { get; set; }
        public int Price { get; set; }

        public bool IsCheckedIn { get; set; } = false;
        public string? CheckInGate { get; set; }
        public DateTimeOffset? CheckInTime { get; set; }

        public Order Order { get; set; } = null!;
        public Seat Seat { get; set; } = null!;
        public Ticket? Ticket { get; set; }
    }
}
