namespace EventTicketBooking.Api.Options
{
    /// <summary>
    /// Cấu hình cổng thanh toán giả lập Development (Mock) cho Story S-45.
    /// Cấu hình tại appsettings.json → "PaymentSettings:Mock".
    /// </summary>
    public class MockPaymentOptions
    {
        public const string SectionName = "PaymentSettings:Mock";

        /// <summary>
        /// Kết quả mô phỏng khi hoàn tiền qua Mock gateway: "Succeeded" (mặc định) hoặc "Failed".
        /// Dùng cho demo/test; không liên quan tới cổng thật.
        /// </summary>
        public string RefundOutcome { get; set; } = "Succeeded";
    }
}
