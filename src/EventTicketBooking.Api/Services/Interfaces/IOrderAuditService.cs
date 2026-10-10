using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Ghi nhận và truy vấn nhật ký thao tác trên đơn hàng, vé và giữ chỗ (Story S-49).
    /// </summary>
    public interface IOrderAuditService
    {
        /// <summary>
        /// Ghi nhận bản ghi nhật ký vào ChangeTracker hiện tại của DbContext (lưu đồng thời khi SaveChanges).
        /// </summary>
        void Record(
            Guid orderId,
            string entityType,
            string? entityId,
            string action,
            string? oldStatus,
            string newStatus,
            string actorType,
            string actor,
            Guid? actorUserId = null,
            string? note = null,
            DateTimeOffset? timestamp = null);

        /// <summary>
        /// Ghi nhận và lưu ngay lập tức vào cơ sở dữ liệu.
        /// </summary>
        Task RecordAndSaveAsync(
            Guid orderId,
            string entityType,
            string? entityId,
            string action,
            string? oldStatus,
            string newStatus,
            string actorType,
            string actor,
            Guid? actorUserId = null,
            string? note = null,
            DateTimeOffset? timestamp = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy toàn bộ lịch sử thao tác của một đơn hàng, sắp xếp theo thời gian tăng dần.
        /// </summary>
        Task<List<OrderAuditLogDto>> GetLogsByOrderIdAsync(
            Guid orderId,
            string? entityType = null,
            CancellationToken cancellationToken = default);
    }
}
