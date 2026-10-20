using System;
using System.Threading.Tasks;
using EventTicketBooking.Api.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations;

/// <summary>
/// Triển khai dịch vụ gửi email sử dụng MailKit qua SMTP.
/// SMTP settings are read from SmtpSettings, with EmailSettings retained for compatibility.
/// </summary>
public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SendConfirmationEmailAsync(string toEmail, string toName, string confirmationLink)
    {
        var message = CreateMessage(toEmail, toName);
        message.Subject = "✅ Xác nhận địa chỉ email của bạn - EventPulse";

        message.Body = new TextPart("html")
        {
            Text = BuildEmailBody(System.Net.WebUtility.HtmlEncode(toName), System.Net.WebUtility.HtmlEncode(confirmationLink))
        };

        await SendEmailInternalAsync(message, toEmail);
    }

    /// <inheritdoc/>
    public async Task SendOtpEmailAsync(string toEmail, string toName, string otpCode)
    {
        try
        {
            var message = CreateMessage(toEmail, toName);
            message.Subject = $"Mã xác nhận EventPulse của bạn: {otpCode}";

            message.Body = new TextPart("html")
            {
                Text = BuildOtpEmailBody(System.Net.WebUtility.HtmlEncode(toName), otpCode)
            };

            await SendEmailInternalAsync(message, toEmail);
        }
        catch (EmailDeliveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare or send registration OTP to {Email}", toEmail);
            throw new EmailDeliveryException("Failed to send the registration OTP email.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task SendWelcomeEmailAsync(string toEmail, string toName, string registrationMethod)
    {
        var message = CreateMessage(toEmail, toName);
        message.Subject = "🎉 Đăng ký thành công - Chào mừng bạn đến với EventPulse!";

        message.Body = new TextPart("html")
        {
            Text = BuildWelcomeEmailBody(System.Net.WebUtility.HtmlEncode(toName), System.Net.WebUtility.HtmlEncode(toEmail), System.Net.WebUtility.HtmlEncode(registrationMethod))
        };

        await SendEmailInternalAsync(message, toEmail);
    }

    /// <inheritdoc/>
    public async Task SendTicketEmailAsync(string toEmail, string toName, string eventTitle, string location, string showtime, System.Collections.Generic.IEnumerable<string> seatNames, byte[] qrCodeBytes)
    {
        var message = CreateMessage(toEmail, toName);
        message.Subject = $"🎫 Vé điện tử của bạn: {eventTitle}";

        var builder = new BodyBuilder();

        // Nhúng mã QR như là inline image (đính kèm nhưng hiển thị trực tiếp trong HTML)
        var qrImage = builder.LinkedResources.Add("ticket-qr.png", qrCodeBytes);
        qrImage.ContentId = "ticket-qr-code";

        string seatsText = string.Join(", ", seatNames);

        builder.HtmlBody = BuildTicketEmailBody(
            System.Net.WebUtility.HtmlEncode(toName),
            System.Net.WebUtility.HtmlEncode(eventTitle),
            System.Net.WebUtility.HtmlEncode(location),
            System.Net.WebUtility.HtmlEncode(showtime),
            System.Net.WebUtility.HtmlEncode(seatsText)
        );

        message.Body = builder.ToMessageBody();

        await SendEmailInternalAsync(message, toEmail);
    }

    /// <inheritdoc/>
    public async Task<bool> SendMarketingEmailAsync(string toEmail, string toName, string subject, string content)
    {
        try
        {
            var senderEmail = GetSetting("SenderEmail", "FromEmail", "Username", "User", "SmtpUser");
            if (string.IsNullOrWhiteSpace(senderEmail))
            {
                _logger.LogInformation("SMTP not configured. Marketing email to {Email} simulated successfully.", toEmail);
                return true;
            }

            var message = CreateMessage(toEmail, toName);
            message.Subject = string.IsNullOrWhiteSpace(subject) ? "🎁 Ưu đãi đặc biệt từ EventPulse dành riêng cho bạn" : subject;

            var appUrl = (_configuration["AppUrl"] ?? _configuration["PublicUrl"] ?? "http://localhost:5012").TrimEnd('/');
            message.Body = new TextPart("html")
            {
                Text = BuildMarketingEmailBody(
                    System.Net.WebUtility.HtmlEncode(toName),
                    System.Net.WebUtility.HtmlEncode(subject ?? "Ưu đãi"),
                    System.Net.WebUtility.HtmlEncode(content ?? "Khám phá các sự kiện hot nhất tuần này."),
                    appUrl)
            };

            await SendEmailInternalAsync(message, toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send marketing email to {Email}. Handled gracefully for dev/test environment.", toEmail);
            return true;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SendAccountingAlertEmailAsync(string toEmail, string subject, string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogWarning("Email người nhận của bộ phận kế toán chưa được cấu hình. Bỏ qua gửi email.");
            return false;
        }

        var message = CreateMessage(toEmail, "Ban Kế toán & Tài chính");
        message.Subject = subject;
        message.Body = new TextPart("html")
        {
            Text = htmlContent
        };

        await SendEmailInternalAsync(message, toEmail);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> SendShowtimeReminderEmailAsync(
        string toEmail,
        string toName,
        string eventTitle,
        string location,
        string showtime,
        System.Collections.Generic.IEnumerable<string> seatNames,
        System.Collections.Generic.IEnumerable<string>? ticketCodes = null,
        string? orderId = null,
        string? directions = null)
    {
        try
        {
            var message = CreateMessage(toEmail, toName);
            message.Subject = $"⏰ [Nhắc nhở] Sự kiện sắp diễn ra trong 24 giờ tới: {eventTitle}";

            var appUrl = (_configuration["App:PublicBaseUrl"] ?? _configuration["AppUrl"] ?? _configuration["PublicUrl"] ?? "http://localhost:5012").TrimEnd('/');
            var seatsText = string.Join(", ", seatNames);
            var ticketsText = ticketCodes != null ? string.Join(", ", ticketCodes) : string.Empty;

            message.Body = new TextPart("html")
            {
                Text = BuildShowtimeReminderEmailBody(
                    System.Net.WebUtility.HtmlEncode(toName),
                    System.Net.WebUtility.HtmlEncode(eventTitle),
                    System.Net.WebUtility.HtmlEncode(location),
                    System.Net.WebUtility.HtmlEncode(showtime),
                    System.Net.WebUtility.HtmlEncode(seatsText),
                    System.Net.WebUtility.HtmlEncode(ticketsText),
                    orderId,
                    directions,
                    appUrl)
            };

            await SendEmailInternalAsync(message, toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send showtime reminder email to {Email}", toEmail);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SendShowtimeRescheduledEmailAsync(
        string toEmail,
        string toName,
        string eventTitle,
        string location,
        string oldShowtime,
        string newShowtime,
        System.Collections.Generic.IEnumerable<string> seatNames,
        string? orderId = null,
        string? directions = null)
    {
        try
        {
            var message = CreateMessage(toEmail, toName);
            message.Subject = $"📢 [Cập nhật quan trọng] Thay đổi giờ diễn sự kiện: {eventTitle}";

            var appUrl = (_configuration["App:PublicBaseUrl"] ?? _configuration["AppUrl"] ?? _configuration["PublicUrl"] ?? "http://localhost:5012").TrimEnd('/');
            var seatsText = string.Join(", ", seatNames);

            message.Body = new TextPart("html")
            {
                Text = BuildShowtimeRescheduledEmailBody(
                    System.Net.WebUtility.HtmlEncode(toName),
                    System.Net.WebUtility.HtmlEncode(eventTitle),
                    System.Net.WebUtility.HtmlEncode(location),
                    System.Net.WebUtility.HtmlEncode(oldShowtime),
                    System.Net.WebUtility.HtmlEncode(newShowtime),
                    System.Net.WebUtility.HtmlEncode(seatsText),
                    orderId,
                    directions,
                    appUrl)
            };

            await SendEmailInternalAsync(message, toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send showtime rescheduled email to {Email}", toEmail);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SendWaitlistSeatAvailableEmailAsync(
        string toEmail,
        string toName,
        string eventTitle,
        string location,
        string showtime,
        int availableSeatsCount,
        string bookingUrl)
    {
        try
        {
            var message = CreateMessage(toEmail, toName);
            message.Subject = $"🎟️ [Có vé trống] Cơ hội đặt vé cho suất diễn bạn đang chờ: {eventTitle}";

            message.Body = new TextPart("html")
            {
                Text = BuildWaitlistSeatAvailableEmailBody(
                    System.Net.WebUtility.HtmlEncode(toName),
                    System.Net.WebUtility.HtmlEncode(eventTitle),
                    System.Net.WebUtility.HtmlEncode(location),
                    System.Net.WebUtility.HtmlEncode(showtime),
                    availableSeatsCount,
                    bookingUrl)
            };

            await SendEmailInternalAsync(message, toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send waitlist seat available email to {Email}", toEmail);
            return false;
        }
    }

    private MimeMessage CreateMessage(string toEmail, string toName)
    {
        var senderEmail = GetSetting("SenderEmail", "FromEmail", "Username", "User", "SmtpUser");
        if (string.IsNullOrWhiteSpace(senderEmail))
        {
            senderEmail = "noreply@eventpulse.vn";
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            GetSetting("SenderName", "FromName") ?? "EventPulse Ticketing",
            senderEmail));
        message.To.Add(new MailboxAddress(toName, toEmail));
        return message;
    }

    private string? GetSetting(params string[] keys)
    {
        foreach (var section in new[] { "SmtpSettings", "EmailSettings" })
        {
            foreach (var key in keys)
            {
                var value = _configuration[$"{section}:{key}"];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private async Task SendEmailInternalAsync(MimeMessage message, string toEmail)
    {
        var user = GetSetting("Username", "User", "SmtpUser")?.Trim();
        var pass = GetSetting("Password", "SmtpPass")?.Trim();
        var host = (GetSetting("Server", "Host", "SmtpHost") ?? "smtp.gmail.com").Trim();
        var senderEmail = (GetSetting("SenderEmail", "FromEmail") ?? user)?.Trim();

        if (string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(pass) ||
            string.IsNullOrWhiteSpace(senderEmail))
        {
            _logger.LogWarning("SMTP not fully configured. Simulating email delivery to {Email} with subject '{Subject}' for dev/test environment.", toEmail, message.Subject);
            return;
        }

        // Tự động chuẩn hóa mật khẩu ứng dụng Gmail (loại bỏ khoảng trắng, dấu gạch ngang nếu có)
        if (host.Contains("gmail.com", StringComparison.OrdinalIgnoreCase))
        {
            pass = pass.Replace(" ", "").Replace("-", "").Trim();
        }

        try
        {
            var configuredPort = GetSetting("Port", "SmtpPort") ?? "587";
            if (!int.TryParse(configuredPort, out var port) || port is < 1 or > 65535)
            {
                throw new InvalidOperationException("SMTP port must be a valid TCP port.");
            }

            using var client = new SmtpClient();
            client.CheckCertificateRevocation = false;
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            var socketOptions = port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            await client.ConnectAsync(host, port, socketOptions);
            await client.AuthenticateAsync(user, pass);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Email sent successfully to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email through SMTP to {Email}", toEmail);
            throw new EmailDeliveryException("Failed to send email through SMTP.", ex);
        }
    }

    // ─── Template email OTP ──────────────────────────────────────────────────
    private static string BuildOtpEmailBody(string name, string otpCode) => $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="100%" style="max-width: 540px; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;">
                  <!-- Header -->
                  <tr>
                    <td style="background: linear-gradient(135deg, #3525cd 0%, #712ae2 100%); padding: 32px 24px; text-align: center;">
                      <div style="font-size: 32px; line-height: 1;">🎫</div>
                      <h1 style="color: #ffffff; margin: 8px 0 0 0; font-size: 22px; font-weight: 800; letter-spacing: -0.5px;">
                        EventPulse Ticketing
                      </h1>
                      <p style="color: #e0e7ff; margin: 4px 0 0 0; font-size: 13px;">Hệ thống phân phối & đặt vé sự kiện trực tuyến</p>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding: 32px 28px;">
                      <p style="font-size: 16px; color: #1e293b; margin: 0 0 12px 0;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size: 14px; color: #475569; line-height: 1.6; margin: 0 0 24px 0;">
                        Bạn vừa đăng ký tài khoản tại <strong>EventPulse</strong>. Dưới đây là mã xác thực OTP 6 số để hoàn tất kích hoạt tài khoản của bạn:
                      </p>

                      <!-- OTP Box -->
                      <div style="background-color: #f1f5f9; border: 2px dashed #cbd5e1; border-radius: 12px; padding: 20px; text-align: center; margin: 0 0 24px 0;">
                        <span style="font-size: 11px; font-weight: 700; color: #64748b; text-transform: uppercase; letter-spacing: 1px; display: block; margin-bottom: 8px;">Mã xác thực của bạn</span>
                        <div style="font-family: 'SF Mono', Monaco, Consolas, 'Courier New', monospace; font-size: 36px; font-weight: 900; letter-spacing: 8px; color: #3525cd;">
                          {otpCode}
                        </div>
                        <span style="font-size: 12px; color: #94a3b8; display: block; margin-top: 8px;">Hiệu lực trong vòng <strong>15 phút</strong></span>
                      </div>

                      <p style="font-size: 13px; color: #64748b; line-height: 1.5; margin: 0 0 16px 0;">
                        🔒 <strong>Lưu ý bảo mật:</strong> Không chia sẻ mã xác thực này cho bất kỳ ai. Nhân viên EventPulse sẽ không bao giờ hỏi mã OTP của bạn.
                      </p>

                      <p style="font-size: 13px; color: #94a3b8; line-height: 1.5; margin: 0;">
                        Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email hoặc thông báo cho chúng tôi để bảo vệ tài khoản.
                      </p>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="background-color: #f8fafc; padding: 20px 24px; text-align: center; border-top: 1px solid #f1f5f9;">
                      <p style="font-size: 12px; color: #94a3b8; margin: 0;">
                        © 2026 EventPulse System. Email tự động từ hệ thống, vui lòng không phản hồi.
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    // ─── Template email Link Nút bấm xác nhận ──────────────────────────────
    private static string BuildEmailBody(string name, string link) => $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="100%" style="max-width: 560px; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 24px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;">
                  <!-- Header -->
                  <tr>
                    <td style="background: linear-gradient(135deg, #3525cd 0%, #712ae2 100%); padding: 32px 24px; text-align: center;">
                      <div style="font-size: 36px; line-height: 1;">🔐</div>
                      <h1 style="color: #ffffff; margin: 8px 0 0 0; font-size: 22px; font-weight: 800; letter-spacing: -0.5px;">
                        Xác Nhận Kích Hoạt Tài Khoản
                      </h1>
                      <p style="color: #e0e7ff; margin: 4px 0 0 0; font-size: 13px;">Hệ thống bán vé sự kiện EventPulse</p>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding: 32px 28px;">
                      <p style="font-size: 16px; color: #1e293b; margin: 0 0 12px 0;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size: 14px; color: #475569; line-height: 1.6; margin: 0 0 20px 0;">
                        Bạn vừa thực hiện đăng ký hoặc đăng nhập bằng tài khoản <strong>Google</strong> tại EventPulse. Để đảm bảo an toàn và hoàn tất quá trình này, bạn vui lòng nhấn vào nút xác nhận bên dưới:
                      </p>

                      <!-- Action Button -->
                      <div style="text-align: center; margin: 30px 0;">
                        <a href="{link}"
                           style="background: linear-gradient(135deg, #3525cd 0%, #4f46e5 100%); color: #ffffff; padding: 16px 36px; text-decoration: none; border-radius: 12px; font-size: 15px; font-weight: 800; display: inline-block; box-shadow: 0 4px 14px rgba(53,37,205,0.35); letter-spacing: 0.3px;">
                          👉 XÁC NHẬN ĐĂNG KÝ TÀI KHOẢN
                        </a>
                      </div>

                      <div style="background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 10px; padding: 14px 16px; margin: 0 0 20px 0; font-size: 12px; color: #64748b; line-height: 1.5;">
                        ⏱️ Liên kết xác nhận này có hiệu lực trong vòng <strong>24 giờ</strong>. Nếu bạn không bấm nút xác nhận, tài khoản sẽ chưa thể kích hoạt vào hệ thống.
                      </div>

                      <p style="font-size: 12px; color: #94a3b8; line-height: 1.5; margin: 0;">
                        Nếu nút bấm bên trên không hoạt động, bạn có thể sao chép và dán liên kết sau vào trình duyệt:<br/>
                        <a href="{link}" style="color: #3525cd; word-break: break-all;">{link}</a>
                      </p>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="background-color: #f8fafc; padding: 20px 24px; text-align: center; border-top: 1px solid #f1f5f9;">
                      <p style="font-size: 12px; color: #94a3b8; margin: 0;">
                        © 2026 EventPulse System. Email tự động từ hệ thống bảo mật EventPulse.
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    // ─── Template email Chào mừng đăng ký thành công ─────────────────────────
    private string BuildWelcomeEmailBody(string name, string email, string method) => $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="100%" style="max-width: 560px; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 24px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;">
                  <!-- Header -->
                  <tr>
                    <td style="background: linear-gradient(135deg, #10b981 0%, #059669 100%); padding: 32px 24px; text-align: center;">
                      <div style="font-size: 36px; line-height: 1;">🎉</div>
                      <h1 style="color: #ffffff; margin: 8px 0 0 0; font-size: 24px; font-weight: 800; letter-spacing: -0.5px;">
                        Đăng Ký Thành Công!
                      </h1>
                      <p style="color: #d1fae5; margin: 4px 0 0 0; font-size: 14px;">Chào mừng bạn đến với cộng đồng người mua vé EventPulse</p>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding: 32px 28px;">
                      <p style="font-size: 16px; color: #1e293b; margin: 0 0 12px 0;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size: 14px; color: #475569; line-height: 1.6; margin: 0 0 20px 0;">
                        Tài khoản EventPulse của bạn đã được khởi tạo và <strong>kích hoạt thành công</strong>. Từ bây giờ bạn có thể trải nghiệm toàn bộ tiện ích đặt vé, giữ chỗ sự kiện trực tuyến.
                      </p>

                      <!-- Summary Card -->
                      <div style="background-color: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 12px; padding: 18px 20px; margin: 0 0 24px 0;">
                        <table width="100%" cellpadding="4" cellspacing="0" style="font-size: 13px; color: #166534;">
                          <tr>
                            <td width="35%" style="font-weight: 700;">Họ và tên:</td>
                            <td>{name}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700;">Email đăng ký:</td>
                            <td>{email}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700;">Phương thức:</td>
                            <td>{method}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700;">Trạng thái:</td>
                            <td><span style="display:inline-block; padding:2px 8px; background:#dcfce7; color:#15803d; border-radius:6px; font-weight:bold; font-size:11px;">Hoạt động (Active)</span></td>
                          </tr>
                        </table>
                      </div>

                      <!-- Feature highlights -->
                      <div style="margin: 0 0 28px 0; border-top: 1px solid #f1f5f9; padding-top: 20px;">
                        <h3 style="font-size: 14px; font-weight: 800; color: #0f172a; margin: 0 0 12px 0;">Quyền lợi thành viên của bạn:</h3>
                        <div style="font-size: 13px; color: #475569; line-height: 1.8;">
                          <div>🎫 <strong>Đặt vé tức thì:</strong> Nhận vé điện tử mã QR check-in qua email.</div>
                          <div>⏱️ <strong>Giữ chỗ 10 phút:</strong> Không lo bị giành ghế trong lúc thanh toán.</div>
                          <div>🔔 <strong>Lịch diễn âm nhạc:</strong> Cập nhật sớm các đại nhạc hội và liveshow đỉnh cao.</div>
                        </div>
                      </div>

                      <!-- Action Button -->
                      <div style="text-align: center; margin: 0 0 16px 0;">
                        <a href="{System.Net.WebUtility.HtmlEncode((_configuration["App:PublicBaseUrl"] ?? "http://localhost:5012").TrimEnd('/') + "/public-events.html")}"
                           style="background: #3525cd; color: #ffffff; padding: 14px 32px; text-decoration: none; border-radius: 10px; font-size: 14px; font-weight: 800; display: inline-block; box-shadow: 0 4px 12px rgba(53,37,205,0.25);">
                          Khám Phá Sự Kiện Ngay →
                        </a>
                      </div>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="background-color: #f8fafc; padding: 20px 24px; text-align: center; border-top: 1px solid #f1f5f9;">
                      <p style="font-size: 12px; color: #94a3b8; margin: 0;">
                        © 2026 EventPulse System. Hỗ trợ khách hàng: 1900 8899 | support@eventticket.com
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    // ─── Template email Vé điện tử ──────────────────────────────────────────
    private string BuildTicketEmailBody(string name, string eventTitle, string location, string showtime, string seats) => $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="100%" style="max-width: 560px; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 24px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;">
                  <!-- Header -->
                  <tr>
                    <td style="background: linear-gradient(135deg, #3525cd 0%, #712ae2 100%); padding: 32px 24px; text-align: center;">
                      <div style="font-size: 36px; line-height: 1;">🎫</div>
                      <h1 style="color: #ffffff; margin: 8px 0 0 0; font-size: 24px; font-weight: 800; letter-spacing: -0.5px;">
                        Vé Điện Tử Sự Kiện
                      </h1>
                      <p style="color: #e0e7ff; margin: 4px 0 0 0; font-size: 14px;">EventPulse Ticketing</p>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding: 32px 28px;">
                      <p style="font-size: 16px; color: #1e293b; margin: 0 0 20px 0;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size: 14px; color: #475569; line-height: 1.6; margin: 0 0 20px 0;">
                        Cảm ơn bạn đã đặt vé. Vui lòng lưu lại mã QR này và xuất trình tại cổng check-in sự kiện.
                      </p>

                      <!-- Ticket Info Card -->
                      <div style="background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 12px; padding: 20px; margin: 0 0 24px 0;">
                        <h2 style="margin: 0 0 16px 0; font-size: 18px; color: #0f172a;">{eventTitle}</h2>
                        <table width="100%" cellpadding="6" cellspacing="0" style="font-size: 13px; color: #475569;">
                          <tr>
                            <td width="30%" style="font-weight: 700; border-bottom: 1px solid #e2e8f0;">📍 Địa điểm:</td>
                            <td style="border-bottom: 1px solid #e2e8f0;">{location}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; border-bottom: 1px solid #e2e8f0;">⏰ Thời gian:</td>
                            <td style="border-bottom: 1px solid #e2e8f0;">{showtime}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700;">💺 Số ghế:</td>
                            <td><strong style="color: #3525cd;">{seats}</strong></td>
                          </tr>
                        </table>
                      </div>

                      <!-- QR Code Section -->
                      <div style="text-align: center; margin: 0 0 20px 0; padding: 20px; border: 2px dashed #cbd5e1; border-radius: 12px;">
                        <span style="display: block; font-size: 12px; font-weight: 700; color: #64748b; text-transform: uppercase; margin-bottom: 12px;">Mã QR Check-in</span>
                        <img src="cid:ticket-qr-code" alt="QR Code" style="width: 200px; height: 200px; max-width: 100%;" />
                      </div>

                      <p style="font-size: 12px; color: #94a3b8; line-height: 1.5; margin: 0;">
                        * Vui lòng không chia sẻ mã QR này lên mạng xã hội để tránh bị người khác sử dụng.
                      </p>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="background-color: #f8fafc; padding: 20px 24px; text-align: center; border-top: 1px solid #f1f5f9;">
                      <p style="font-size: 12px; color: #94a3b8; margin: 0;">
                        © 2026 EventPulse System. Hỗ trợ khách hàng: 1900 8899 | support@eventticket.com
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    private static string BuildMarketingEmailBody(string toName, string subject, string content, string baseUrl = "http://localhost:5012") =>
        $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8">
          <title>{subject}</title>
        </head>
        <body style="margin: 0; padding: 24px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f1f5f9;">
          <table width="100%" cellpadding="0" cellspacing="0" style="max-width: 600px; margin: 0 auto; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1);">
            <tr>
              <td style="background: linear-gradient(135deg, #3525cd 0%, #4f46e5 100%); padding: 32px 24px; text-align: center; color: #ffffff;">
                <h1 style="margin: 0; font-size: 24px; font-weight: 800;">🎉 EventPulse Promotions</h1>
                <p style="margin: 8px 0 0 0; font-size: 14px; opacity: 0.9;">Ưu đãi độc quyền dành cho thành viên</p>
              </td>
            </tr>
            <tr>
              <td style="padding: 32px 24px; color: #1e293b;">
                <p style="font-size: 16px; margin: 0 0 16px 0;">Xin chào <strong>{toName}</strong>,</p>
                <div style="font-size: 15px; line-height: 1.6; color: #334155; margin-bottom: 24px;">
                  {content}
                </div>
                <div style="text-align: center; margin: 32px 0;">
                  <a href="{baseUrl}/public-events.html" style="background-color: #3525cd; color: #ffffff; text-decoration: none; padding: 14px 28px; border-radius: 12px; font-weight: 700; font-size: 14px; display: inline-block;">
                    Khám phá sự kiện ngay
                  </a>
                </div>
                <hr style="border: none; border-top: 1px solid #e2e8f0; margin: 24px 0;" />
                <p style="font-size: 12px; color: #64748b; line-height: 1.5; margin: 0;">
                  Bạn nhận được email này vì đã đồng ý nhận thông tin tiếp thị từ EventPulse. 
                  Nếu không muốn nhận email này nữa, bạn có thể <a href="{baseUrl}/privacy-settings.html" style="color: #3525cd; text-decoration: underline;">thu hồi quyền nhận email tiếp thị tại đây</a> bất cứ lúc nào.
                </p>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    // ─── Template email Nhắc nhở trước 24 giờ (Story S-61) ───────────────────
    private static string BuildShowtimeReminderEmailBody(
        string name,
        string eventTitle,
        string location,
        string showtime,
        string seats,
        string ticketCodes,
        string? orderId,
        string? directions,
        string baseUrl)
    {
        var orderLink = !string.IsNullOrWhiteSpace(orderId)
            ? $"{baseUrl}/order-details.html?id={orderId}"
            : $"{baseUrl}/my-orders.html";

        var directionsHtml = !string.IsNullOrWhiteSpace(directions)
            ? System.Net.WebUtility.HtmlEncode(directions)
            : "Vui lòng có mặt tại địa điểm trước giờ diễn ít nhất 30 - 45 phút để làm thủ tục check-in soát vé và ổn định vị trí ngồi. Bãi gửi xe được bố trí tại cổng chính của địa điểm.";

        var ticketCodesSection = !string.IsNullOrWhiteSpace(ticketCodes)
            ? $"""
              <tr>
                <td style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #e2e8f0; vertical-align: top;">🎫 Mã vé:</td>
                <td style="padding: 8px 0; border-bottom: 1px solid #e2e8f0; font-family: monospace; font-weight: bold; color: #3525cd;">{ticketCodes}</td>
              </tr>
              """
            : "";

        return $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
          <title>Nhắc nhở: Sự kiện sắp diễn ra trong 24 giờ tới</title>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="100%" style="max-width: 600px; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 24px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;">
                  <!-- Header -->
                  <tr>
                    <td style="background: linear-gradient(135deg, #3525cd 0%, #712ae2 100%); padding: 32px 24px; text-align: center;">
                      <div style="font-size: 38px; line-height: 1;">⏰</div>
                      <h1 style="color: #ffffff; margin: 10px 0 0 0; font-size: 22px; font-weight: 800; letter-spacing: -0.5px;">
                        Sự Kiện Sắp Diễn Ra Trong 24 Giờ Tới!
                      </h1>
                      <p style="color: #e0e7ff; margin: 6px 0 0 0; font-size: 13px;">EventPulse Ticketing • Thông báo nhắc lịch tự động</p>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding: 32px 28px;">
                      <p style="font-size: 16px; color: #1e293b; margin: 0 0 12px 0;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size: 14px; color: #475569; line-height: 1.6; margin: 0 0 20px 0;">
                        Chỉ còn <strong>chưa đầy 24 giờ nữa</strong> là sự kiện bạn mong đợi sẽ chính thức diễn ra! EventPulse xin gửi email nhắc nhở để bạn có sự chuẩn bị chu đáo nhất.
                      </p>

                      <!-- Event Info Card -->
                      <div style="background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 12px; padding: 20px; margin: 0 0 24px 0;">
                        <h2 style="margin: 0 0 16px 0; font-size: 18px; color: #0f172a; font-weight: 800;">{eventTitle}</h2>
                        <table width="100%" cellpadding="0" cellspacing="0" style="font-size: 13px; color: #475569;">
                          <tr>
                            <td width="32%" style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #e2e8f0;">⏰ Giờ diễn:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #e2e8f0; font-weight: bold; color: #0f172a;">{showtime}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #e2e8f0;">📍 Địa điểm:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #e2e8f0;">{location}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #e2e8f0;">💺 Ghế đã đặt:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #e2e8f0; font-weight: bold; color: #3525cd;">{seats}</td>
                          </tr>
                          {ticketCodesSection}
                        </table>
                      </div>

                      <!-- Directions & Venue Instructions Box -->
                      <div style="background-color: #eff6ff; border-left: 4px solid #3b82f6; border-radius: 0 10px 10px 0; padding: 16px 18px; margin: 0 0 24px 0;">
                        <div style="font-size: 14px; font-weight: 700; color: #1e40af; margin-bottom: 6px;">
                          🗺️ Chỉ Dẫn Địa Điểm &amp; Lưu Ý Tham Dự:
                        </div>
                        <div style="font-size: 13px; color: #1e3a8a; line-height: 1.6;">
                          {directionsHtml}
                        </div>
                        <ul style="margin: 10px 0 0 0; padding-left: 18px; font-size: 12px; color: #3b82f6; line-height: 1.6;">
                          <li>Vui lòng chuẩn bị sẵn mã QR hoặc mã vé trên điện thoại để quét vào cổng nhanh chóng.</li>
                          <li>Không mang theo vũ khí, chất cháy nổ hoặc vật dụng bị cấm vào khán phòng.</li>
                        </ul>
                      </div>

                      <!-- CTA Button -->
                      <div style="text-align: center; margin: 28px 0 20px 0;">
                        <a href="{orderLink}"
                           style="background: linear-gradient(135deg, #3525cd 0%, #4f46e5 100%); color: #ffffff; padding: 14px 32px; text-decoration: none; border-radius: 12px; font-size: 14px; font-weight: 800; display: inline-block; box-shadow: 0 4px 14px rgba(53,37,205,0.3); letter-spacing: 0.3px;">
                          👉 Xem Vé &amp; Mã QR Của Bạn
                        </a>
                      </div>

                      <p style="font-size: 12px; color: #94a3b8; line-height: 1.5; text-align: center; margin: 0;">
                        Chúc bạn có một buổi trải nghiệm sự kiện thật bùng nổ và đáng nhớ!
                      </p>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="background-color: #f8fafc; padding: 20px 24px; text-align: center; border-top: 1px solid #f1f5f9;">
                      <p style="font-size: 12px; color: #94a3b8; margin: 0;">
                        © 2026 EventPulse System. Hỗ trợ khách hàng: 1900 8899 | support@eventticket.com
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    // ─── Template email Thông báo cập nhật đổi giờ diễn (Story S-61) ──────────
    private static string BuildShowtimeRescheduledEmailBody(
        string name,
        string eventTitle,
        string location,
        string oldShowtime,
        string newShowtime,
        string seats,
        string? orderId,
        string? directions,
        string baseUrl)
    {
        var orderLink = !string.IsNullOrWhiteSpace(orderId)
            ? $"{baseUrl}/order-details.html?id={orderId}"
            : $"{baseUrl}/my-orders.html";

        var directionsHtml = !string.IsNullOrWhiteSpace(directions)
            ? System.Net.WebUtility.HtmlEncode(directions)
            : "Quý khách vui lòng đến trước giờ diễn mới 30 - 45 phút để hoàn tất thủ tục check-in.";

        return $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
          <title>Thông báo quan trọng: Cập nhật giờ diễn sự kiện</title>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="100%" style="max-width: 600px; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 24px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;">
                  <!-- Header -->
                  <tr>
                    <td style="background: linear-gradient(135deg, #e11d48 0%, #9333ea 100%); padding: 32px 24px; text-align: center;">
                      <div style="font-size: 38px; line-height: 1;">📢</div>
                      <h1 style="color: #ffffff; margin: 10px 0 0 0; font-size: 22px; font-weight: 800; letter-spacing: -0.5px;">
                        Thông Báo Cập Nhật Giờ Diễn
                      </h1>
                      <p style="color: #fce7f3; margin: 6px 0 0 0; font-size: 13px;">EventPulse Ticketing • Thay đổi lịch biểu diễn chính thức</p>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding: 32px 28px;">
                      <p style="font-size: 16px; color: #1e293b; margin: 0 0 12px 0;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size: 14px; color: #475569; line-height: 1.6; margin: 0 0 20px 0;">
                        Ban tổ chức sự kiện <strong>{eventTitle}</strong> xin trân trọng thông báo rằng lịch biểu diễn đã được điều chỉnh. Dưới đây là thông tin chi tiết về sự thay đổi:
                      </p>

                      <!-- Time Change Comparison Card -->
                      <div style="background-color: #fff1f2; border: 1px solid #fecdd3; border-radius: 12px; padding: 20px; margin: 0 0 24px 0;">
                        <h2 style="margin: 0 0 14px 0; font-size: 17px; color: #9f1239; font-weight: 800;">{eventTitle}</h2>
                        <table width="100%" cellpadding="0" cellspacing="0" style="font-size: 13px; color: #475569;">
                          <tr>
                            <td width="35%" style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #fecdd3; color: #e11d48;">⏳ Thời gian cũ:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #fecdd3; color: #9f1239; text-decoration: line-through;">{oldShowtime}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #fecdd3; color: #15803d;">⏰ THỜI GIAN MỚI:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #fecdd3; font-weight: 800; color: #15803d; font-size: 14px;">{newShowtime}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #fecdd3;">📍 Địa điểm:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #fecdd3;">{location}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0;">💺 Ghế của bạn:</td>
                            <td style="padding: 8px 0; font-weight: bold; color: #3525cd;">{seats}</td>
                          </tr>
                        </table>
                      </div>

                      <!-- Reassurance & Instructions -->
                      <div style="background-color: #f0fdf4; border-left: 4px solid #22c55e; border-radius: 0 10px 10px 0; padding: 16px 18px; margin: 0 0 24px 0;">
                        <div style="font-size: 14px; font-weight: 700; color: #166534; margin-bottom: 6px;">
                          ✅ Vé Của Bạn Vẫn Hoàn Toàn Hợp Lệ!
                        </div>
                        <p style="font-size: 13px; color: #15803d; line-height: 1.6; margin: 0 0 8px 0;">
                          Toàn bộ vé và vị trí ghế bạn đã đặt mua <strong>tự động có giá trị</strong> cho khung giờ biểu diễn mới. Bạn không cần thao tác đổi vé hay cập nhật thông tin.
                        </p>
                        <div style="font-size: 12px; color: #166534; line-height: 1.5;">
                          {directionsHtml}
                        </div>
                      </div>

                      <p style="font-size: 13px; color: #64748b; line-height: 1.6; margin: 0 0 24px 0;">
                        Nếu khung giờ mới không phù hợp với kế hoạch của bạn hoặc bạn có bất kỳ câu hỏi nào, xin vui lòng liên hệ tổng đài hỗ trợ <strong>1900 8899</strong> hoặc gửi email về <strong>support@eventticket.com</strong> để được hỗ trợ tốt nhất.
                      </p>

                      <!-- CTA Button -->
                      <div style="text-align: center; margin: 28px 0 16px 0;">
                        <a href="{orderLink}"
                           style="background: linear-gradient(135deg, #e11d48 0%, #be123c 100%); color: #ffffff; padding: 14px 32px; text-decoration: none; border-radius: 12px; font-size: 14px; font-weight: 800; display: inline-block; box-shadow: 0 4px 14px rgba(225,29,72,0.3); letter-spacing: 0.3px;">
                          Kiểm Tra Chi Tiết Đơn Hàng
                        </a>
                      </div>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="background-color: #f8fafc; padding: 20px 24px; text-align: center; border-top: 1px solid #f1f5f9;">
                      <p style="font-size: 12px; color: #94a3b8; margin: 0;">
                        © 2026 EventPulse System. Thành thật xin lỗi vì sự bất tiện này và cảm ơn sự thông cảm của quý khách!
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    // ─── Template email Thông báo danh sách chờ khi có vé về kho (Story S-59) ──────────
    private static string BuildWaitlistSeatAvailableEmailBody(
        string name,
        string eventTitle,
        string location,
        string showtime,
        int availableSeatsCount,
        string bookingUrl)
    {
        var seatsBadge = availableSeatsCount > 0 ? $"{availableSeatsCount} ghế trống vừa mở lại" : "Có ghế vừa nhả về kho";

        return $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
          <title>Thông báo: Có vé mở lại cho suất diễn bạn đang chờ</title>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="100%" style="max-width: 600px; background-color: #ffffff; border-radius: 16px; overflow: hidden; box-shadow: 0 4px 24px rgba(0,0,0,0.06); border: 1px solid #e2e8f0;">
                  <!-- Header -->
                  <tr>
                    <td style="background: linear-gradient(135deg, #3525cd 0%, #712ae2 100%); padding: 32px 24px; text-align: center;">
                      <div style="font-size: 38px; line-height: 1;">🎟️</div>
                      <h1 style="color: #ffffff; margin: 10px 0 0 0; font-size: 22px; font-weight: 800; letter-spacing: -0.5px;">
                        Có Vé Về Kho – Đến Lượt Bạn!
                      </h1>
                      <p style="color: #dad7ff; margin: 6px 0 0 0; font-size: 13px;">EventPulse Waitlist • Ưu tiên thông báo cho người đăng ký chờ</p>
                    </td>
                  </tr>

                  <!-- Body -->
                  <tr>
                    <td style="padding: 32px 28px;">
                      <p style="font-size: 16px; color: #1e293b; margin: 0 0 12px 0;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size: 14px; color: #475569; line-height: 1.6; margin: 0 0 20px 0;">
                        Bạn nhận được thông báo này vì đã đăng ký vào <strong>Danh sách chờ (Waitlist)</strong> cho sự kiện dưới đây khi suất diễn tạm thời hết vé. Do có khán giả huỷ vé hoặc lượt giữ chỗ hết hạn, ghế đã được giải phóng và quay trở lại kho bán:
                      </p>

                      <!-- Showtime Information Box -->
                      <div style="background-color: #f8fafc; border-radius: 12px; padding: 18px 20px; border: 1px solid #e2e8f0; margin-bottom: 24px;">
                        <table width="100%" cellpadding="0" cellspacing="0" style="font-size: 14px; color: #334155;">
                          <tr>
                            <td style="font-weight: 700; width: 130px; padding: 8px 0; border-bottom: 1px solid #e2e8f0;">🎭 Sự kiện:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #e2e8f0; font-weight: 700; color: #0f172a;">{eventTitle}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #e2e8f0;">⏰ Thời gian diễn:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #e2e8f0; font-weight: bold; color: #3525cd;">{showtime}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0; border-bottom: 1px solid #e2e8f0;">📍 Địa điểm:</td>
                            <td style="padding: 8px 0; border-bottom: 1px solid #e2e8f0;">{location}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 700; padding: 8px 0;">⚡ Tình trạng kho:</td>
                            <td style="padding: 8px 0; color: #16a34a; font-weight: 700;">{seatsBadge}</td>
                          </tr>
                        </table>
                      </div>

                      <!-- Priority Notice Box -->
                      <div style="background-color: #fffbeb; border-left: 4px solid #f59e0b; border-radius: 0 10px 10px 0; padding: 16px 18px; margin: 0 0 24px 0;">
                        <div style="font-size: 14px; font-weight: 700; color: #b45309; margin-bottom: 6px;">
                          ⚡ Nhanh Tay Đặt Vé Ngay:
                        </div>
                        <p style="font-size: 13px; color: #92400e; line-height: 1.6; margin: 0;">
                          Vé được mở bán công khai theo thứ tự ưu tiên ai chọn trước sẽ giữ chỗ trước. Vì số lượng ghế quay về kho có hạn, hãy click vào nút bên dưới để chọn ghế và hoàn tất đơn hàng ngay lập tức!
                        </p>
                      </div>

                      <!-- CTA Button -->
                      <div style="text-align: center; margin: 28px 0 20px 0;">
                        <a href="{bookingUrl}"
                           style="background: linear-gradient(135deg, #3525cd 0%, #4f46e5 100%); color: #ffffff; padding: 14px 34px; text-decoration: none; border-radius: 12px; font-size: 15px; font-weight: 800; display: inline-block; box-shadow: 0 4px 14px rgba(53,37,205,0.35); letter-spacing: 0.3px;">
                          👉 Chọn Ghế &amp; Đặt Vé Ngay
                        </a>
                      </div>

                      <p style="font-size: 12px; color: #94a3b8; line-height: 1.5; text-align: center; margin: 0;">
                        Nếu bạn không còn nhu cầu mua vé suất này, bạn có thể bỏ qua email này.
                      </p>
                    </td>
                  </tr>

                  <!-- Footer -->
                  <tr>
                    <td style="background-color: #f8fafc; padding: 20px 24px; text-align: center; border-top: 1px solid #f1f5f9;">
                      <p style="font-size: 12px; color: #94a3b8; margin: 0;">
                        © 2026 EventPulse System. Hỗ trợ khách hàng: 1900 8899 | support@eventticket.com
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }
}


