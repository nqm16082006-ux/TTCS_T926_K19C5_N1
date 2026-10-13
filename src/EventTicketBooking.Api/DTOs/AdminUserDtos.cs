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

    /// <summary>
    /// Yêu cầu Admin tạo tài khoản nhân viên mới kèm vai trò (S-28).
    /// Password chỉ nhận từ request và được băm ngay ở Backend, không bao giờ trả về.
    /// </summary>
    public class CreateAdminUserDto
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
    }

    /// <summary>
    /// Bản ghi nhật ký kiểm toán thay đổi vai trò trả về cho Admin (S-28).
    /// </summary>
    public class RoleAuditLogResponseDto
    {
        public Guid Id { get; set; }
        public Guid ActorUserId { get; set; }
        public string ActorUsername { get; set; } = string.Empty;
        public Guid TargetUserId { get; set; }
        public string TargetUsername { get; set; } = string.Empty;
        public string TargetEmail { get; set; } = string.Empty;
        public string OldRoles { get; set; } = string.Empty;
        public string NewRoles { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}

