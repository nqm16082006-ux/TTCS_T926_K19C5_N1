using Microsoft.AspNetCore.Identity;

namespace EmailConfirmation.Data;

/// <summary>
/// Entity người dùng — kế thừa IdentityUser.
/// Field <see cref="IdentityUser.EmailConfirmed"/> (bool) đã có sẵn, 
/// mặc định = false, chỉ set true khi xác nhận email thành công (AC3, AC4).
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>Họ và tên đầy đủ của người dùng.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>Ngày tạo tài khoản.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
