using System;

namespace EventTicketBooking.Api.Models
{
    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Trạng thái kích hoạt (T-07)
        public bool IsActive { get; set; } = false;
        // Foreign Key liên kết tới Role
        public Guid RoleId { get; set; }

        // Navigation property tới Role
        public Role Role { get; set; } = null!;
    }
}
