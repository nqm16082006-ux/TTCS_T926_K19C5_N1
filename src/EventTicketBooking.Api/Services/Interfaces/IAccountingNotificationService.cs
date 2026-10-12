using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service thông báo cho bộ phận Kế toán khi có lệnh hoàn tiền thất bại cần xử lý thủ công (Story S-48).
    /// </summary>
    public interface IAccountingNotificationService
    {
        /// <summary>
        /// Gửi thông báo khẩn cấp tới bộ phận Kế toán về lệnh hoàn tiền thất bại.
        /// Đảm bảo tính idempotent: Không gửi trùng lặp nếu đã gửi thành công trước đó.
        /// </summary>
        Task<bool> NotifyFailedRefundAsync(RefundRequest refund, CancellationToken cancellationToken = default);
    }
}
