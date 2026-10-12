using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service xử lý nghiệp vụ hoàn tiền độc lập và tái sử dụng (Story S-45 & S-47).
    /// </summary>
    public interface IRefundService
    {
        /// <summary>
        /// Xử lý hoàn tiền cho một đơn hàng cụ thể đã thanh toán.
        /// Đảm bảo idempotent: nếu đơn hàng đã có bản ghi hoàn tiền thì không hoàn lần hai.
        /// </summary>
        Task<RefundExecutionResult> ProcessOrderRefundAsync(
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
        Task<List<RefundExecutionResult>> ProcessShowtimeRefundsAsync(
            Guid showtimeId,
            IEnumerable<Guid> paidOrderIds,
            string actorType,
            string actorName,
            Guid? actorId = null,
            CancellationToken cancellationToken = default);
    }
}
