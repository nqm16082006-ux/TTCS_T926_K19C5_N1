using System;

namespace EventTicketBooking.Api.DTOs
{
    public class SeatStatusDto
    {
        public Guid Id { get; set; }
        public string Row { get; set; } = string.Empty;
        public int SeatNumber { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string Status { get; set; } = "AVAILABLE"; // AVAILABLE, HELD, SOLD
    }
}
