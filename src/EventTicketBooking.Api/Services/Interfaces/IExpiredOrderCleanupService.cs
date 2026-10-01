using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface IExpiredOrderCleanupService
    {
        /// <summary>
        /// Tìm đơn hàng quá hạn thanh toán, khoá dòng, chuyển trạng thái sang Expired và nhả ghế về AVAILABLE trong cùng một giao dịch (Task T-52).
        /// </summary>
        /// <param name="fakeNow">Thời điểm giả định dùng trong unit/integration test, nếu null sẽ dùng DateTimeOffset.UtcNow</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Số lượng đơn hàng quá hạn đã được huỷ</returns>
        Task<int> CleanupExpiredOrdersAsync(DateTimeOffset? fakeNow = null, CancellationToken cancellationToken = default);
    }
}
