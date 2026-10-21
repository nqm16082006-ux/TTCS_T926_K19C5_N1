using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service xử lý hoàn tiền, retry tự động và đối soát chống duplicate (Jira Story S-48, S-45 & S-47).
    /// </summary>
    public interface IRefundService
    {
        /// <summary>
        /// Tạo yêu cầu hoàn tiền cho đơn hàng (Idempotent theo orderId nếu đã có refund đang xử lý).
        /// </summary>
        Task<RefundRequest> CreateRefundRequestAsync(Guid orderId, int? amount = null, string? reason = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Thực thi xử lý một yêu cầu hoàn tiền cụ thể (gọi gateway, retry backoff, hoặc chuyển sang ManualReviewRequired).
        /// </summary>
        Task<EventTicketBooking.Api.DTOs.Payment.RefundExecutionResult> ProcessRefundAsync(Guid refundId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Quét và xử lý tất cả các refund đang ở trạng thái Pending hoặc RetryScheduled đã tới thời điểm NextRetryAt.
        /// Đồng thời đảm bảo concurrency control giữa nhiều worker.
        /// </summary>
        Task<int> ProcessPendingAndRetryableRefundsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Quét và thử gửi lại thông báo cho kế toán cho các refund ở trạng thái ManualReviewRequired nhưng chưa gửi thành công.
        /// </summary>
        Task<int> RetryFailedAccountingNotificationsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy thông tin chi tiết của một refund theo ID.
        /// </summary>
        Task<RefundRequest?> GetRefundByIdAsync(Guid refundId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy danh sách các refund đang cần kế toán can thiệp xử lý thủ công (ManualReviewRequired).
        /// </summary>
        Task<List<RefundRequest>> GetRefundsForManualReviewAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy danh sách tất cả các refund gần nhất (sắp xếp giảm dần theo thời gian tạo).
        /// </summary>
        Task<List<RefundRequest>> GetAllRefundsAsync(int take = 50, CancellationToken cancellationToken = default);

        /// <summary>
        /// Xử lý hoàn tiền cho một đơn hàng cụ thể đã thanh toán.
        /// Đảm bảo idempotent: nếu đơn hàng đã có bản ghi hoàn tiền thì không hoàn lần hai.
        /// </summary>
        Task<EventTicketBooking.Api.DTOs.RefundExecutionResult> ProcessOrderRefundAsync(
            Guid orderId,
            int amount,
            string cancelReason,
            string actorType,
            string actorName,
            Guid? actorId = null,
            string? description = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Xử lý hoàn tiền cho danh sách các đơn hàng đã thanh toán khi suất diễn bị huỷ (Story S-47).
        /// Mỗi đơn hàng được xử lý độc lập, lỗi của một đơn không làm gián đoạn các đơn còn lại.
        /// </summary>
        Task<List<EventTicketBooking.Api.DTOs.RefundExecutionResult>> ProcessShowtimeRefundsAsync(
            Guid showtimeId,
            IEnumerable<Guid> paidOrderIds,
            string actorType,
            string actorName,
            Guid? actorId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Giả lập hoàn tiền thất bại cho đơn hàng phục vụ kiểm thử Story S-48.
        /// Ghi nhận RetryCount (tăng dần 1 -> 2 -> 3).
        /// Khi RetryCount >= 3, tự động chuyển sang ManualReviewRequired và kích hoạt AccountingNotificationService.
        /// </summary>
        Task<EventTicketBooking.Api.DTOs.RefundExecutionResult> SimulateRefundFailureForOrderAsync(
            Guid orderId,
            string actorType = "USER",
            string actorName = "User",
            Guid? actorId = null,
            CancellationToken cancellationToken = default);
    }
}
