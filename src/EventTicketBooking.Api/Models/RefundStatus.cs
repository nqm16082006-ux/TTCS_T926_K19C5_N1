namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Trạng thái của yêu cầu hoàn tiền (Jira Story S-48).
    /// </summary>
    public enum RefundStatus
    {
        /// <summary>
        /// Mới tạo, chờ xử lý lần đầu.
        /// </summary>
        Pending = 0,

        /// <summary>
        /// Đang được worker hoặc tiến trình thực thi gọi cổng thanh toán.
        /// </summary>
        Processing = 1,

        /// <summary>
        /// Hoàn tiền thành công trên cổng thanh toán (Trạng thái cuối cùng).
        /// </summary>
        Succeeded = 2,

        /// <summary>
        /// Gặp lỗi tạm thời (retryable), đã lên lịch thử lại theo exponential backoff.
        /// </summary>
        RetryScheduled = 3,

        /// <summary>
        /// Thất bại do lỗi không thể thử lại (non-retryable error) (Trạng thái cuối cùng).
        /// </summary>
        Failed = 4,

        /// <summary>
        /// Vượt quá số lần thử tối đa (3 lần retry bổ sung) hoặc cần kế toán xử lý thủ công (Trạng thái cuối cùng).
        /// </summary>
        ManualReviewRequired = 5
    }
}
