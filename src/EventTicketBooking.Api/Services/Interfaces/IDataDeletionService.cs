using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Giao diện xử lý yêu cầu xoá và ẩn danh dữ liệu cá nhân theo Nghị định 13/2023/NĐ-CP (Story S-53).
    /// </summary>
    public interface IDataDeletionService
    {
        /// <summary>
        /// Khách hàng gửi yêu cầu xoá dữ liệu cá nhân.
        /// </summary>
        Task<DataDeletionRequestDto> CreateRequestAsync(Guid userId, CreateDataDeletionRequestDto dto);

        /// <summary>
        /// Lấy thông tin trạng thái yêu cầu xoá dữ liệu của người dùng hiện tại.
        /// </summary>
        Task<DataDeletionStatusDto> GetUserStatusAsync(Guid userId);

        /// <summary>
        /// Quản trị viên lấy danh sách các yêu cầu xoá dữ liệu.
        /// </summary>
        Task<List<DataDeletionRequestDto>> GetAllRequestsAsync(string? status = null);

        /// <summary>
        /// Người có thẩm quyền (Admin) xem xét duyệt hoặc từ chối yêu cầu (Story S-53 AC & NFR).
        /// Khi duyệt: tiến hành ẩn danh hoá họ tên, email, vô hiệu hoá tài khoản nhưng giữ nguyên đơn hàng, giao dịch kế toán.
        /// </summary>
        Task<DataDeletionRequestDto> ReviewRequestAsync(Guid requestId, Guid adminUserId, ReviewDataDeletionRequestDto dto);
    }
}
