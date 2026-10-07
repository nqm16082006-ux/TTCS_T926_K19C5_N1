using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class AdminUserResponseDto
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
        public int TotalEvents { get; set; }
    }

    public class UpdateUserRolesDto
    {
        public List<string> Roles { get; set; } = new List<string>();
    }

    public class UpdateUserStatusDto
    {
        public bool IsActive { get; set; }
    }
}
