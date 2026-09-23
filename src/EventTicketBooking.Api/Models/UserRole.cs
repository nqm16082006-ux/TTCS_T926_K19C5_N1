using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bảng trung gian thể hiện quan hệ Nhiều - Nhiều (N:N) giữa Users và Roles.
    /// Cho phép 1 User có thể đảm nhiệm nhiều Role khác nhau trong hệ thống.
    /// </summary>
    public class UserRole
    {
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public Guid RoleId { get; set; }
        public Role Role { get; set; } = null!;

        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    }
}
