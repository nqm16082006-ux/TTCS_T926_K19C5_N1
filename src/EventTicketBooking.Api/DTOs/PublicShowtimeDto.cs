using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class PublicShowtimeDto
    {
        public Guid Id { get; set; }

        public Guid EventId { get; set; }

        public string EventTitle { get; set; } = string.Empty;

        public string? EventDescription { get; set; }

        public string? ImageUrl { get; set; }

        public string EventLocation { get; set; } = string.Empty;

        public string OrganizerName { get; set; } = string.Empty;

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        public int AvailableSeats { get; set; }

        public int TotalSeats { get; set; }

        public int RemainingTickets { get; set; }

        public int AvailableTickets
        {
            get => RemainingTickets;
            set => RemainingTickets = value;
        }

        /// <summary>
        /// Số ghế thực tế còn có thể mua: loại trừ ghế SOLD và ghế đang có active hold chưa hết hạn.
        /// </summary>
        public int RemainingSeats { get; set; }

        public string Status { get; set; } = string.Empty;

        public decimal MinPrice { get; set; }

        public decimal MaxPrice { get; set; }

        public int MaxTicketsPerUser { get; set; } = 10;
    }

    public class PublicEventDetailDto
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? ImageUrl { get; set; }

        public string Location { get; set; } = string.Empty;

        public string OrganizerName { get; set; } = string.Empty;

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        public int TotalSeats { get; set; }

        public int AvailableSeats { get; set; }

        public int MaxTicketsPerUser { get; set; } = 10;

        public decimal MinPrice { get; set; }

        public decimal MaxPrice { get; set; }

        public List<PublicShowtimeDto> Showtimes { get; set; } = new List<PublicShowtimeDto>();
    }
}

