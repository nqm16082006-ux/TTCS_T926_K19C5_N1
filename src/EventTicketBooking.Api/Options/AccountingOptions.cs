namespace EventTicketBooking.Api.Options
{
    /// <summary>
    /// Cấu hình thông báo cho bộ phận Kế toán khi hoàn tiền thất bại (Jira Story S-48).
    /// </summary>
    public class AccountingOptions
    {
        public const string SectionName = "Accounting";

        /// <summary>
        /// Địa chỉ email nhận thông báo của bộ phận Kế toán / Tài chính.
        /// Cần được cấu hình qua appsettings hoặc biến môi trường ACCOUNTING_EMAIL.
        /// Mặc định là chuỗi rỗng để không giả định địa chỉ chính thức trước khi PO/Team xác nhận.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Tên phòng ban hiển thị trong email.
        /// </summary>
        public string DepartmentName { get; set; } = "Ban Kế toán & Tài chính";

        /// <summary>
        /// Tự động gửi thông báo khi một lệnh hoàn tiền chuyển sang ManualReviewRequired.
        /// </summary>
        public bool AutoNotifyOnFailure { get; set; } = true;
    }
}
