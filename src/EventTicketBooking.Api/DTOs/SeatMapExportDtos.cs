using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class SeatMapExportItemDto
    {
        public Guid Id { get; set; }
        public string Row { get; set; } = string.Empty;
        public int SeatNumber { get; set; }
        public string SeatCode => $"{Row}{SeatNumber}";
        public string CategoryName { get; set; } = string.Empty;
        public int? Price { get; set; }
        /// <summary>
        /// Status: "SOLD", "HELD", "AVAILABLE"
        /// </summary>
        public string Status { get; set; } = "AVAILABLE";
    }

    public class SeatMapExportDataDto
    {
        public Guid ShowtimeId { get; set; }
        public string ShowtimeName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public Guid EventId { get; set; }
        public string EventTitle { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

        public List<SeatMapExportItemDto> Seats { get; set; } = new();

        public int TotalSeats => Seats.Count;
        public int SoldCount => Seats.FindAll(s => s.Status == "SOLD").Count;
        public int HeldCount => Seats.FindAll(s => s.Status == "HELD").Count;
        public int AvailableCount => Seats.FindAll(s => s.Status == "AVAILABLE").Count;

        public double SoldPercentage => TotalSeats > 0 ? Math.Round((double)SoldCount * 100 / TotalSeats, 1) : 0;
        public double HeldPercentage => TotalSeats > 0 ? Math.Round((double)HeldCount * 100 / TotalSeats, 1) : 0;
        public double AvailablePercentage => TotalSeats > 0 ? Math.Round((double)AvailableCount * 100 / TotalSeats, 1) : 0;
    }
}
