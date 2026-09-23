namespace EmailConfirmation.Services;

/// <summary>
/// Contract dịch vụ gửi email xác nhận.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Gửi email xác nhận tài khoản chứa liên kết/token hợp lệ (AC2).
    /// </summary>
    /// <param name="toEmail">Địa chỉ email người nhận.</param>
    /// <param name="toName">Tên hiển thị người nhận.</param>
    /// <param name="confirmationLink">Liên kết xác nhận đầy đủ.</param>
    Task SendConfirmationEmailAsync(string toEmail, string toName, string confirmationLink);
}
