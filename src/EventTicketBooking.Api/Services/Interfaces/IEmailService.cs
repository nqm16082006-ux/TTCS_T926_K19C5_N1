namespace EventTicketBooking.Api.Services.Interfaces;

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

    /// <summary>
    /// Gửi mã xác nhận OTP (6 số) để hoàn tất đăng ký tài khoản.
    /// </summary>
    /// <param name="toEmail">Địa chỉ email người nhận.</param>
    /// <param name="toName">Tên hiển thị người nhận.</param>
    /// <param name="otpCode">Mã OTP 6 chữ số.</param>
    Task SendOtpEmailAsync(string toEmail, string toName, string otpCode);

    /// <summary>
    /// Gửi email thông báo đăng ký tài khoản thành công (cho cả Email+OTP và Google).
    /// </summary>
    /// <param name="toEmail">Địa chỉ email người nhận.</param>
    /// <param name="toName">Tên hiển thị người nhận.</param>
    /// <param name="registrationMethod">Phương thức đăng ký ("Email & Mật khẩu" hoặc "Tài khoản Google").</param>
    Task SendWelcomeEmailAsync(string toEmail, string toName, string registrationMethod);

    /// <summary>
    /// Gửi vé điện tử qua email kèm mã QR (S-27).
    /// </summary>
    Task SendTicketEmailAsync(
        string toEmail,
        string toName,
        string eventTitle,
        string location,
        string showtime,
        System.Collections.Generic.IEnumerable<string> seatNames,
        byte[] qrCodeBytes);
}

