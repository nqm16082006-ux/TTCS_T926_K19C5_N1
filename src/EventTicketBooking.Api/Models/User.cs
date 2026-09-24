using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Entity biểu diễn Người dùng (User) trong hệ thống.
    /// </summary>
    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = false;
        public string? VerificationCode { get; set; }
        public DateTime? VerificationCodeExpiresAt { get; set; }

        // Navigation property quan hệ N:N thông qua bảng trung gian UserRoles
        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

        // Navigation property 1:N với Events (Task T-10)
        public ICollection<Event> Events { get; set; } = new List<Event>();
    }
}
