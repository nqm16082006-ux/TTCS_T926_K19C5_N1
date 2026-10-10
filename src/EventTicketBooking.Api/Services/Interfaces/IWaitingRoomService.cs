using System;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface IWaitingRoomService
    {
        /// <summary>
        /// Yêu cầu gia nhập hàng chờ hoặc nhận lượt truy cập trực tiếp nếu chưa vượt ngưỡng.
        /// </summary>
        Task<WaitingRoomStatusDto> JoinQueueAsync(Guid showtimeId, Guid? userId, string clientIdentifier);

        /// <summary>
        /// Gửi heartbeat định kỳ để duy trì vị trí trong hàng và nhận cập nhật tiến độ.
        /// </summary>
        Task<WaitingRoomStatusDto?> HeartbeatAsync(Guid showtimeId, string queueToken);

        /// <summary>
        /// Chủ động rời khỏi hàng chờ.
        /// </summary>
        Task<bool> LeaveQueueAsync(Guid showtimeId, string queueToken);

        /// <summary>
        /// Xác thực xem passToken có hợp lệ và còn hiệu lực cho suất diễn này hay không.
        /// </summary>
        Task<bool> ValidatePassTokenAsync(Guid showtimeId, string passToken, string? clientIdentifier = null, Guid? userId = null);

        /// <summary>
        /// Gửi tín hiệu sống (active-ping) định kỳ từ trang sơ đồ ghế để duy trì phiên hoạt động.
        /// </summary>
        Task<bool> ActivePingAsync(Guid showtimeId, string passToken, string clientIdentifier);

        /// <summary>
        /// Giải phóng một phiên đang hoạt động khi người dùng hoàn tất đặt vé hoặc thoát khỏi sơ đồ ghế.
        /// </summary>
        Task<bool> ReleaseActivePassAsync(Guid showtimeId, string passToken);

        /// <summary>
        /// Kiểm tra xem suất diễn hiện tại có đang cần phòng chờ (vượt ngưỡng hoặc có hàng đợi) hay không.
        /// </summary>
        Task<bool> IsWaitingRoomRequiredAsync(Guid showtimeId);

        /// <summary>
        /// Lấy thống kê số lượng người đang hoạt động, độ dài hàng đợi và ngưỡng hiện tại.
        /// </summary>
        Task<WaitingRoomStatsDto> GetStatsAsync(Guid showtimeId);

        /// <summary>
        /// Thiết lập ngưỡng số người hoạt động cho một suất diễn cụ thể.
        /// </summary>
        Task SetShowtimeThresholdAsync(Guid showtimeId, int threshold);

        /// <summary>
        /// Dọn dẹp các ticket bỏ dở (quá hạn heartbeat) và pass hết hạn, sau đó promote người tiếp theo.
        /// </summary>
        void CleanupStaleSessions();
    }
}
