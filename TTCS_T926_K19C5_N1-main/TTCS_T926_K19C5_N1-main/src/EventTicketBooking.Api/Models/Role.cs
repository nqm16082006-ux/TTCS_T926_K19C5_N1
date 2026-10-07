using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Entity biểu diễn Vai trò (Role) trong hệ thống bán vé.
    /// </summary>
    public class Role
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation property quan hệ N:N thông qua bảng trung gian UserRoles
        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    }
}
