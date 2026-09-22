using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.Models
{
    public class Role
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation property: Một Role có thể có nhiều User
        public ICollection<User> Users { get; set; } = new List<User>();
    }
}
