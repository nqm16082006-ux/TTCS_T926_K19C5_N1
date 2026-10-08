using System.Text.Json.Serialization;

namespace EventTicketBooking.Api.DTOs
{
    public class SeatImportItemDto
    {
        [JsonPropertyName("row")]
        public string? Row { get; set; }

        [JsonPropertyName("seatNumber")]
        public int SeatNumber { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("categoryName")]
        public string? CategoryName
        {
            get => Category;
            set
            {
                if (string.IsNullOrEmpty(Category))
                {
                    Category = value;
                }
            }
        }
    }
}
