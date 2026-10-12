using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service xử lý hoàn tiền, retry tự động và đối soát chống duplicate (Jira Story S-48).
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
        Task<RefundExecutionResult> ProcessRefundAsync(Guid refundId, CancellationToken cancellationToken = default);

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
    }
}
