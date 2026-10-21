using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Hợp đồng dịch vụ nhắc người mua trước giờ diễn qua email và thông báo khi đổi giờ diễn (Story S-61).
    /// </summary>
    public interface IShowtimeReminderService
    {
        /// <summary>
        /// Quét tất cả các suất diễn sắp diễn ra trong vòng 24 giờ tới (StartTime &lt;= now + 24h và StartTime &gt; now)
        /// và gửi email nhắc nhở kèm thông tin vé và chỉ dẫn địa điểm cho các đơn hàng đã thanh toán chưa được nhắc (Story S-61).
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Số lượng email nhắc nhở đã được gửi thành công.</returns>
        Task<int> SendUpcomingShowtimeRemindersAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gửi email nhắc nhở cho một suất diễn cụ thể (hỗ trợ gọi thủ công / test).
        /// </summary>
        /// <param name="showtimeId">ID của suất diễn.</param>
        /// <param name="force">Nếu true, gửi lại email ngay cả khi đơn đã có ReminderSentAt.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Số lượng email nhắc nhở đã được gửi thành công.</returns>
        Task<int> SendReminderForShowtimeAsync(Guid showtimeId, bool force = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gửi email cập nhật khi đổi giờ diễn tới tất cả người mua vé đã thanh toán của suất diễn đó (Story S-61).
        /// </summary>
        /// <param name="showtimeId">ID của suất diễn.</param>
        /// <param name="oldStartTime">Thời gian bắt đầu cũ.</param>
        /// <param name="oldEndTime">Thời gian kết thúc cũ.</param>
        /// <param name="newStartTime">Thời gian bắt đầu mới.</param>
        /// <param name="newEndTime">Thời gian kết thúc mới.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Số lượng email thông báo đổi giờ đã được gửi thành công.</returns>
        Task<int> SendShowtimeRescheduledNotificationsAsync(
            Guid showtimeId,
            DateTime oldStartTime,
            DateTime oldEndTime,
            DateTime newStartTime,
            DateTime newEndTime,
            CancellationToken cancellationToken = default);
    }
}
