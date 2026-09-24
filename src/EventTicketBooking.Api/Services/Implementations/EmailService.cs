using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Api.Services.Implementations;

/// <summary>
/// Triển khai dịch vụ gửi email sử dụng MailKit qua SMTP.
/// Cấu hình SMTP lấy từ appsettings.json (mục "EmailSettings").
/// </summary>
public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;
    private readonly IWebHostEnvironment _env;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger, IWebHostEnvironment env)
    {
        _configuration = configuration;
        _logger = logger;
        _env = env;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// AC1: Gửi email ngay sau khi đăng ký thành công.
    /// AC2: Email chứa liên kết xác nhận hợp lệ được tạo từ Identity token.
    /// </remarks>
    public async Task SendConfirmationEmailAsync(string toEmail, string toName, string confirmationLink)
    {
        var settings = _configuration.GetSection("EmailSettings");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            settings["SenderName"] ?? "Hệ thống",
            settings["SenderEmail"] ?? "noreply@example.com"
        ));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = "✅ Xác nhận địa chỉ email của bạn";

        // Nội dung email HTML đẹp, rõ ràng
        message.Body = new TextPart("html")
        {
            Text = BuildEmailBody(toName, confirmationLink)
        };

        // Yêu cầu: môi trường dev in ra log thay vì gửi thật, không log mã kích hoạt ở môi trường staging trở lên
        if (_env.IsDevelopment())
        {
            _logger.LogInformation("🛠️ [DEV MODE] Đã giả lập gửi email xác nhận đến {Email}. Link kích hoạt: {Link}", toEmail, confirmationLink);
            return;
        }

        using var client = new SmtpClient();
        try
        {
            var host = settings["SmtpHost"] ?? "smtp.ethereal.email";
            var port = int.Parse(settings["SmtpPort"] ?? "587");
            var user = settings["SmtpUser"] ?? "";
            var pass = settings["SmtpPass"] ?? "";

            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(user, pass);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("📧 Đã gửi email xác nhận đến {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Lỗi khi gửi email đến {Email}", toEmail);
            throw new InvalidOperationException($"Không thể gửi email xác nhận: {ex.Message}", ex);
        }
    }

    // ─── Template email HTML ────────────────────────────────────────────────
    private static string BuildEmailBody(string name, string link) => $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8"/>
          <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
        </head>
        <body style="font-family: Arial, sans-serif; background:#f4f4f4; margin:0; padding:20px;">
          <table width="100%" cellpadding="0" cellspacing="0">
            <tr>
              <td align="center">
                <table width="600" cellpadding="0" cellspacing="0"
                       style="background:#ffffff; border-radius:8px; overflow:hidden;
                              box-shadow:0 2px 8px rgba(0,0,0,0.1);">
                  <!-- Header -->
                  <tr>
                    <td style="background:#4F46E5; padding:32px; text-align:center;">
                      <h1 style="color:#ffffff; margin:0; font-size:24px;">
                        🔐 Xác nhận Email
                      </h1>
                    </td>
                  </tr>
                  <!-- Body -->
                  <tr>
                    <td style="padding:32px;">
                      <p style="font-size:16px; color:#333;">Xin chào <strong>{name}</strong>,</p>
                      <p style="font-size:15px; color:#555; line-height:1.6;">
                        Cảm ơn bạn đã đăng ký tài khoản. Vui lòng nhấn vào nút bên dưới
                        để xác nhận địa chỉ email và kích hoạt tài khoản của bạn.
                      </p>
                      <div style="text-align:center; margin:32px 0;">
                        <a href="{link}"
                           style="background:#4F46E5; color:#ffffff; padding:14px 32px;
                                  text-decoration:none; border-radius:6px; font-size:16px;
                                  font-weight:bold; display:inline-block;">
                          ✅ Xác nhận Email
                        </a>
                      </div>
                      <p style="font-size:13px; color:#888;">
                        Liên kết này có hiệu lực trong <strong>24 giờ</strong>.
                        Nếu bạn không thực hiện yêu cầu này, hãy bỏ qua email này.
                      </p>
                      <hr style="border:none; border-top:1px solid #eee; margin:24px 0;"/>
                      <p style="font-size:12px; color:#aaa; word-break:break-all;">
                        Hoặc sao chép liên kết này vào trình duyệt:<br/>
                        <span style="color:#4F46E5;">{link}</span>
                      </p>
                    </td>
                  </tr>
                  <!-- Footer -->
                  <tr>
                    <td style="background:#f9f9f9; padding:16px; text-align:center;">
                      <p style="font-size:12px; color:#bbb; margin:0;">
                        © 2026 Hệ thống. Email này được gửi tự động, vui lòng không trả lời.
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

