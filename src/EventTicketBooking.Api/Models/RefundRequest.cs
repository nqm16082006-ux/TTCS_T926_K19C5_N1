using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi yêu cầu hoàn tiền và theo dõi lịch sử retry, trạng thái đối soát kế toán (Jira Story S-48).
    /// </summary>
    public class RefundRequest
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// ID của đơn hàng liên quan.
        /// </summary>
        public Guid OrderId { get; set; }

        /// <summary>
        /// Mã số đơn hàng dạng số (dùng để đối soát với cổng thanh toán).
        /// </summary>
        public long OrderCode { get; set; }

        /// <summary>
        /// Mã giao dịch thanh toán ban đầu từ cổng thanh toán.
        /// </summary>
        public string? TransactionId { get; set; }

        /// <summary>
        /// Mã giao dịch hoàn tiền được cổng thanh toán cấp sau khi hoàn tiền thành công.
        /// </summary>
        public string? RefundTransactionId { get; set; }

        /// <summary>
        /// Khóa Idempotency chống gửi trùng lệnh hoàn tiền cho cổng thanh toán.
        /// </summary>
        public string IdempotencyKey { get; set; } = $"ref_{Guid.NewGuid():N}";

        /// <summary>
        /// Số tiền hoàn (VNĐ).
        /// </summary>
        public int Amount { get; set; }

        /// <summary>
        /// Loại tiền tệ (mặc định: "VND").
        /// </summary>
        public string Currency { get; set; } = "VND";

        /// <summary>
        /// Lý do hoàn tiền (ví dụ: "ShowtimeCancelled", "ExpiredOrderPaid", "ManualReview").
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Trạng thái hiện tại của lệnh hoàn tiền.
        /// </summary>
        public RefundStatus Status { get; set; } = RefundStatus.Pending;

        /// <summary>
        /// Tổng số lần đã thực thi lệnh (lần ban đầu = 1, các lần sau tăng dần).
        /// </summary>
        public int AttemptCount { get; set; } = 0;

        /// <summary>
        /// Số lần đã retry bổ sung sau lần thử ban đầu (tối đa 3 lần: 0, 1, 2, 3).
        /// </summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>
        /// Số lần retry bổ sung tối đa cho phép (mặc định: 3).
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Thời điểm thực hiện lần thử gần nhất.
        /// </summary>
        public DateTimeOffset? LastAttemptedAt { get; set; }

        /// <summary>
        /// Thời điểm được lên lịch thử lại tiếp theo theo exponential backoff.
        /// </summary>
        public DateTimeOffset? NextRetryAt { get; set; }

        /// <summary>
        /// Thời điểm xác định thất bại cuối cùng.
        /// </summary>
        public DateTimeOffset? FailedAt { get; set; }

        /// <summary>
        /// Mã lỗi từ cổng thanh toán hoặc hệ thống.
        /// </summary>
        public string? ErrorCode { get; set; }

        /// <summary>
        /// Thông tin lỗi chi tiết (đã làm sạch dữ liệu nhạy cảm).
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Đánh dấu lỗi lần cuối có phải lỗi tạm thời có thể retry được hay không.
        /// </summary>
        public bool? IsRetryable { get; set; }

        /// <summary>
        /// Đã gửi thông báo cho kế toán hay chưa.
        /// </summary>
        public bool AccountingNotified { get; set; } = false;

        /// <summary>
        /// Thời điểm gửi thông báo cho kế toán thành công.
        /// </summary>
        public DateTimeOffset? AccountingNotifiedAt { get; set; }

        /// <summary>
        /// Lỗi gặp phải khi gửi thông báo cho kế toán (nếu có, để hỗ trợ retry gửi lại an toàn).
        /// </summary>
        public string? AccountingNotificationError { get; set; }

        /// <summary>
        /// Khóa thời gian lease để ngăn chặn hai worker cùng nhận xử lý một bản ghi.
        /// </summary>
        public DateTimeOffset? LockedUntil { get; set; }

        /// <summary>
        /// Thời điểm tạo yêu cầu.
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Thời điểm cập nhật trạng thái gần nhất.
        /// </summary>
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Đơn hàng liên quan.
        /// </summary>
        public Order? Order { get; set; }
    }
}
