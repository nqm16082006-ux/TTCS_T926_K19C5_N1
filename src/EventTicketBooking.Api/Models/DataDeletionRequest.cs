using System;

namespace EventTicketBooking.Api.Models
{
    public enum DataDeletionRequestStatus
    {
        Pending,
        Approved,
        Rejected
    }

    /// <summary>
    /// Bản ghi yêu cầu xoá dữ liệu cá nhân theo Nghị định 13/2023/NĐ-CP (Story S-53).
    /// </summary>
    public class DataDeletionRequest
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// ID người dùng yêu cầu xoá dữ liệu.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Email người yêu cầu (sẽ được thay bằng giá trị ẩn danh khi được duyệt).
        /// </summary>
        public string RequesterEmail { get; set; } = string.Empty;

        /// <summary>
        /// Tên người yêu cầu (sẽ được thay bằng giá trị ẩn danh khi được duyệt).
        /// </summary>
        public string? RequesterName { get; set; }

        /// <summary>
        /// Lý do người dùng yêu cầu xoá dữ liệu cá nhân.
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// Trạng thái yêu cầu: Pending (Chờ duyệt), Approved (Đã duyệt & ẩn danh), Rejected (Từ chối).
        /// </summary>
        public DataDeletionRequestStatus Status { get; set; } = DataDeletionRequestStatus.Pending;

        /// <summary>
        /// Thời điểm khách gửi yêu cầu.
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Thời điểm người có thẩm quyền phê duyệt / từ chối.
        /// </summary>
        public DateTimeOffset? ReviewedAt { get; set; }

        /// <summary>
        /// ID của Admin/Người có thẩm quyền xem xét (NFR S-53).
        /// </summary>
        public Guid? ReviewedByUserId { get; set; }

        /// <summary>
        /// Ghi chú phản hồi của Admin (giải trình khi từ chối hoặc hướng dẫn khi duyệt).
        /// </summary>
        public string? AdminNote { get; set; }

        // Navigation properties
        public User? User { get; set; }
        public User? ReviewedByUser { get; set; }
    }
}
