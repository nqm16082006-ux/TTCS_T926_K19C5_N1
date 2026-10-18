using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service quản lý danh sách chờ khi suất diễn hết vé (Story S-59).
    /// </summary>
    public interface IWaitlistService
    {
        /// <summary>
        /// Đăng ký người dùng vào danh sách chờ của một suất diễn hết vé.
        /// </summary>
        Task<WaitlistRegistrationResultDto> JoinWaitlistAsync(JoinWaitlistRequestDto request, Guid? currentUserId = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy trạng thái danh sách chờ của suất diễn (số lượng người đang chờ, vị trí hàng đợi nếu có email).
        /// </summary>
        Task<WaitlistStatusResponseDto> GetStatusAsync(Guid showtimeId, string? email = null, Guid? currentUserId = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy danh sách những người đăng ký chờ theo thứ tự FIFO (dành cho Ban tổ chức / Quản trị viên).
        /// </summary>
        Task<List<WaitlistEntryDto>> GetWaitlistForShowtimeAsync(Guid showtimeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gửi email thông báo cho người chờ theo thứ tự đăng ký khi có ghế về kho do huỷ hay hết hạn.
        /// </summary>
        Task<int> ProcessAvailableSeatsNotificationAsync(Guid showtimeId, int seatsReleasedCount = 1, CancellationToken cancellationToken = default);

        /// <summary>
        /// Quét tất cả các suất diễn có ghế trống và có khách chờ để gửi email theo thứ tự đăng ký.
        /// </summary>
        Task<int> ProcessAllPendingWaitlistsAsync(CancellationToken cancellationToken = default);
    }
}
