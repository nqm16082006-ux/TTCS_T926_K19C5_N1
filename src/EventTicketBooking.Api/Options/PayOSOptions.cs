namespace EventTicketBooking.Api.Options
{
    public class PayOSOptions
    {
        public const string SectionName = "Payment:PayOS";

        public string ClientId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ChecksumKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://api-merchant.payos.vn";
        public string ReturnUrl { get; set; } = "http://localhost:5012/payment-success.html";
        public string CancelUrl { get; set; } = "http://localhost:5012/payment-cancel.html";
    }
}
