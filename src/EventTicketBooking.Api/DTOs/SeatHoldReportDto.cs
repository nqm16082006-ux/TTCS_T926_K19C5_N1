using System;

namespace EventTicketBooking.Api.DTOs
{
    public class SeatHoldReportDto
    {
        public Guid ShowtimeId { get; set; }
        public string EventTitle { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }

        public int TotalHoldCount { get; set; }
        public int ConvertedHoldCount { get; set; }
        public int ExpiredHoldCount { get; set; }
        public int ReleasedHoldCount { get; set; }
        public int UnsuccessfulHoldCount { get; set; }

        public double ConversionRate { get; set; }
        public double? AveragePaymentDurationSeconds { get; set; }
        public double? P90PaymentDurationSeconds { get; set; }
    }
}
