using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service xử lý chuyển nhượng vé cho người khác (Story S-57).
    /// </summary>
    public interface ITicketTransferService
    {
        /// <summary>
        /// Thực hiện chuyển nhượng vé từ người gửi cho người nhận theo email.
        /// </summary>
        Task<ApiResponse<TicketTransferResultDto>> TransferTicketAsync(
            Guid ticketId,
            Guid senderUserId,
            TransferTicketRequestDto request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy lịch sử các lần chuyển nhượng của một vé.
        /// </summary>
        Task<ApiResponse<List<TicketTransferHistoryDto>>> GetTicketTransferHistoryAsync(
            Guid ticketId,
            Guid requesterUserId,
            bool isAdmin,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy danh sách tất cả các vé thuộc sở hữu của người dùng (bao gồm vé mua và vé được nhận chuyển nhượng).
        /// </summary>
        Task<ApiResponse<List<TicketDto>>> GetUserTicketsAsync(
            Guid userId,
            CancellationToken cancellationToken = default);
    }
}
