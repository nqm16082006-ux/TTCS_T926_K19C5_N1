using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi kiểm toán tối giản cho Story S-28 (chỉ phục vụ thao tác phân quyền).
    /// Actor được lấy từ identity đã xác thực ở Backend, không nhận từ body request.
    /// </summary>
    public class AuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Người thực hiện thao tác (Actor) - lấy từ claim đã xác thực.</summary>
        public Guid ActorUserId { get; set; }

        /// <summary>Tên tài khoản actor tại thời điểm thao tác (snapshot).</summary>
        public string ActorUsername { get; set; } = string.Empty;

        /// <summary>Tài khoản bị thay đổi (target user).</summary>
        public Guid TargetUserId { get; set; }

        /// <summary>Vai trò cũ, phân cách bằng dấu phẩy.</summary>
        public string OldRoles { get; set; } = string.Empty;

        /// <summary>Vai trò mới, phân cách bằng dấu phẩy.</summary>
        public string NewRoles { get; set; } = string.Empty;

        /// <summary>Hành động kiểm toán, ví dụ: ROLE_CHANGED.</summary>
        public string Action { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
