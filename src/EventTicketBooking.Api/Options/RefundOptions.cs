namespace EventTicketBooking.Api.Options
{
    /// <summary>
    /// Cấu hình cơ chế thử lại lệnh hoàn tiền (Exponential Backoff) (Jira Story S-48).
    /// </summary>
    public class RefundOptions
    {
        public const string SectionName = "RefundSettings";

        /// <summary>
        /// Số lần retry bổ sung tối đa sau lần thử ban đầu (mặc định: 3 lần).
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Khoảng cách thời gian tăng dần giữa các lần retry (tính theo phút).
        /// Mặc định: [1, 2, 4] tương ứng với 1 phút, 2 phút, 4 phút.
        /// </summary>
        public int[] BackoffMinutes { get; set; } = new[] { 1, 2, 4 };

        /// <summary>
        /// Tùy chọn khoảng cách thời gian tính theo giây (dùng khi kiểm thử hoặc cấu hình dev).
        /// Nếu được thiết lập và không rỗng, sẽ ưu tiên dùng danh sách này.
        /// </summary>
        public int[]? BackoffSeconds { get; set; }

        /// <summary>
        /// Chu kỳ chạy của worker quét refund nền (tính theo giây). Mặc định: 30s.
        /// </summary>
        public int WorkerIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Thời hạn lease lock (giây) cho mỗi worker khi thực thi xử lý một refund.
        /// </summary>
        public int LockLeaseSeconds { get; set; } = 60;
    }
}
