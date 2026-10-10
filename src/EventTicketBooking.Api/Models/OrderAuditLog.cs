using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi nhật ký thao tác và thay đổi trạng thái trên đơn hàng, vé và giữ chỗ (Story S-49).
    /// Mọi thay đổi do người dùng, nhân viên, hoặc job nền thực hiện đều có bản ghi kèm:
    /// - Tác nhân (Actor, ActorType)
    /// - Thời điểm (Timestamp)
    /// - Trạng thái trước (OldStatus) và trạng thái sau (NewStatus)
    /// - Xem và truy vấn được theo từng đơn hàng (OrderId).
    /// </summary>
    public class OrderAuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Mã đơn hàng liên quan</summary>
        public Guid OrderId { get; set; }

        /// <summary>Loại thực thể: ORDER, TICKET, SEAT_HOLD</summary>
        public string EntityType { get; set; } = "ORDER";

        /// <summary>Định danh đối tượng bị tác động (OrderId, TicketCode, hoặc Tên ghế / SeatId)</summary>
        public string? EntityId { get; set; }

        /// <summary>Mã hành động thao tác (ví dụ: ORDER_CREATED, ORDER_PAID, ORDER_EXPIRED, ORDER_CANCELLED, TICKET_ISSUED, TICKET_CHECKED_IN, TICKET_READMISSION, SEAT_HOLD_ATTACHED, SEAT_HOLD_CONVERTED, SEAT_HOLD_EXPIRED...)</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>Trạng thái trước khi thay đổi (null nếu tạo mới)</summary>
        public string? OldStatus { get; set; }

        /// <summary>Trạng thái mới sau khi thay đổi</summary>
        public string NewStatus { get; set; } = string.Empty;

        /// <summary>Phân loại tác nhân: USER, STAFF, ADMIN, SYSTEM, BACKGROUND_JOB</summary>
        public string ActorType { get; set; } = "USER";

        /// <summary>Tên hoặc định danh mô tả tác nhân (ví dụ: username, tên nhân viên, "job:ExpiredOrderCleanupWorker", "system:PaymentWebhook")</summary>
        public string Actor { get; set; } = string.Empty;

        /// <summary>ID người dùng thực hiện (nếu tác nhân là con người)</summary>
        public Guid? ActorUserId { get; set; }

        /// <summary>Thời điểm ghi nhận thao tác</summary>
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Chi tiết bổ sung (cổng soát vé, lý do, ghi chú...)</summary>
        public string? Note { get; set; }

        // Navigation property
        public Order? Order { get; set; }
    }
}
