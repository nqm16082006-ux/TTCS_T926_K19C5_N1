namespace EventTicketBooking.Api.DTOs
{
    public class SeatImportError
    {
        public int? SeatIndex { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
