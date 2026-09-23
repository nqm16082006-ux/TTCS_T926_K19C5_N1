namespace EmailConfirmation.Services;

/// <summary>
/// Contract dịch vụ xử lý logic xác nhận email.
/// </summary>
public interface IEmailConfirmationService
{
    /// <summary>
    /// Tạo token xác nhận email cho user và gửi email (AC1, AC2).
    /// Gọi ngay sau khi user đăng ký thành công.
    /// </summary>
    /// <param name="userId">ID người dùng vừa đăng ký.</param>
    /// <param name="email">Địa chỉ email đăng ký.</param>
    /// <param name="fullName">Tên hiển thị trong email.</param>
    Task GenerateAndSendConfirmationAsync(string userId, string email, string fullName);

    /// <summary>
    /// Xác nhận token và kích hoạt tài khoản (AC3, AC5).
    /// </summary>
    /// <param name="userId">ID người dùng cần xác nhận.</param>
    /// <param name="token">Token nhận từ link email.</param>
    /// <returns>
    /// <see langword="true"/> nếu token hợp lệ và tài khoản được kích hoạt;
    /// <see langword="false"/> nếu token không hợp lệ hoặc đã hết hạn (AC5).
    /// </returns>
    Task<(bool Success, string Message)> ConfirmEmailAsync(string userId, string token);

    /// <summary>
    /// Kiểm tra tài khoản đã xác nhận email chưa (AC4).
    /// </summary>
    Task<bool> IsEmailConfirmedAsync(string userId);
}
