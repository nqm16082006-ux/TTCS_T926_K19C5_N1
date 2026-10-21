using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Entity biểu diễn Người dùng đăng ký vào danh sách chờ khi suất diễn hết vé (Story S-59).
    /// Hàng đợi hoạt động theo cơ chế FIFO (First-In, First-Out) dựa trên CreatedAt.
    /// </summary>
    public class ShowtimeWaitlist
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid ShowtimeId { get; set; }

        public Guid? UserId { get; set; }

        public string Email { get; set; } = string.Empty;

        public string? FullName { get; set; }

        public string? PhoneNumber { get; set; }

        /// <summary>
        /// Trạng thái đăng ký: "WAITING" (đang chờ), "NOTIFIED" (đã gửi email thông báo), "CANCELLED" (đã huỷ)
        /// </summary>
        public string Status { get; set; } = "WAITING";

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? NotifiedAt { get; set; }

        public int NotificationCount { get; set; } = 0;

        // Navigation properties
        public Showtime Showtime { get; set; } = null!;

        public User? User { get; set; }
    }
}
