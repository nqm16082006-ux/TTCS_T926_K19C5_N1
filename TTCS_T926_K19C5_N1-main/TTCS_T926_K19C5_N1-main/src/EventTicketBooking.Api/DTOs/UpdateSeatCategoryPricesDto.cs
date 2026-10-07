using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class UpdateSeatCategoryPricesDto
    {
        public List<UpdateSeatCategoryPriceItemDto> Categories { get; set; } = new();
    }

    public class UpdateSeatCategoryPriceItemDto
    {
        public Guid SeatCategoryId { get; set; }
        public int? Price { get; set; }
    }
}
