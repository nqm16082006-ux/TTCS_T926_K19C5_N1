namespace EventTicketBooking.Api.DTOs
{
    public class SeatImportResultDto
    {
        public Guid ShowtimeId { get; set; }

        public int ImportedSeatCount { get; set; }

        public int CreatedCategoryCount { get; set; }
    }
}
