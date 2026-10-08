using System;

namespace EventTicketBooking.Api.DTOs
{
    public class SeatCategoryPriceDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int? Price { get; set; }
        public int TotalQuantity { get; set; }
        public int HeldQuantity { get; set; }
        public int AvailableQuantity { get; set; }
        public int RemainingQuantity { get; set; }
    }
}
