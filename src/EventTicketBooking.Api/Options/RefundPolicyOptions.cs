namespace EventTicketBooking.Api.Options
{
    /// <summary>
    /// Chính sách huỷ vé & hoàn tiền (Story S-45).
    /// CHÚ Ý: 24 giờ và 90% là GIÁ TRỊ DEMO chưa được Product phê duyệt chính thức.
    /// Cấu hình tại appsettings.json → section "RefundPolicy".
    /// </summary>
    public class RefundPolicyOptions
    {
        public const string SectionName = "RefundPolicy";

        /// <summary>Số giờ tối thiểu trước giờ chiếu để được huỷ (demo: 24).</summary>
        public int CutoffHours { get; set; } = 24;

        /// <summary>Tỉ lệ phần trăm số tiền được hoàn (demo: 90).</summary>
        public int RefundPercent { get; set; } = 90;
    }
}
