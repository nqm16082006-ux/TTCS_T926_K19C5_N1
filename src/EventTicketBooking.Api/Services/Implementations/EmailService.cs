using System;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    public async Task SendConfirmationEmailAsync(string toEmail, string toName, string confirmationLink)
    {
        var settings = _configuration.GetSection("EmailSettings");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            settings["SenderName"] ?? "EventPulse Ticketing",
            settings["SenderEmail"] ?? "noreply@eventticket.com"
        ));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = "✅ Xác nhận địa chỉ email của bạn - EventPulse";

        message.Body = new TextPart("html")
        {
            Text = BuildEmailBody(System.Net.WebUtility.HtmlEncode(toName), System.Net.WebUtility.HtmlEncode(confirmationLink))
        };

        await SendEmailInternalAsync(message, toEmail, $"Link: {confirmationLink}");
    }

    /// <inheritdoc/>
    public async Task SendOtpEmailAsync(string toEmail, string toName, string otpCode)
    {
        var settings = _configuration.GetSection("EmailSettings");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            settings["SenderName"] ?? "EventPulse Ticketing",
            settings["SenderEmail"] ?? "noreply@eventticket.com"
        ));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = $"Mã xác nhận EventPulse của bạn: {otpCode}";

        message.Body = new TextPart("html")
        {
            Text = BuildOtpEmailBody(System.Net.WebUtility.HtmlEncode(toName), otpCode)
        };

        await SendEmailInternalAsync(message, toEmail, $"Mã OTP: {otpCode}");
    }

    /// <inheritdoc/>
    public async Task SendWelcomeEmailAsync(string toEmail, string toName, string registrationMethod)
    {
        var settings = _configuration.GetSection("EmailSettings");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            settings["SenderName"] ?? "EventPulse Ticketing",
            settings["SenderEmail"] ?? "noreply@eventticket.com"
        ));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = "🎉 Đăng ký thành công - Chào mừng bạn đến với EventPulse!";

        message.Body = new TextPart("html")
        {
            Text = BuildWelcomeEmailBody(System.Net.WebUtility.HtmlEncode(toName), System.Net.WebUtility.HtmlEncode(toEmail), System.Net.WebUtility.HtmlEncode(registrationMethod))
        };

        await SendEmailInternalAsync(message, toEmail, $"Chào mừng thành viên mới ({registrationMethod})");
    }

    /// <inheritdoc/>
    public async Task SendTicketEmailAsync(string toEmail, string toName, string eventTitle, string location, string showtime, System.Collections.Generic.IEnumerable<string> seatNames, byte[] qrCodeBytes)
    {
        var settings = _configuration.GetSection("EmailSettings");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            settings["SenderName"] ?? "EventPulse Ticketing",
            settings["SenderEmail"] ?? "noreply@eventticket.com"
        ));
        message.To.Add(new MailboxAddress(toName, toEmail));
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

        await SendEmailInternalAsync(message, toEmail, $"Vé sự kiện: {eventTitle}");
    }

    private async Task SendEmailInternalAsync(MimeMessage message, string toEmail, string logDetail)
    {
        var settings = _configuration.GetSection("EmailSettings");
        var user = settings["SmtpUser"] ?? "";
        var pass = settings["SmtpPass"] ?? "";

        // Nếu chưa cấu hình mật khẩu SMTP thì ghi log mô phỏng (cho môi trường test)
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            if (_env.IsDevelopment())
            {
                _logger.LogInformation("[SIMULATED] Email tới {Email}", toEmail);
                return;
            }
            throw new InvalidOperationException("SMTP credentials must be configured.");
        }

        try
        {
            var host = settings["SmtpHost"] ?? "smtp.gmail.com";
            var port = int.Parse(settings["SmtpPort"] ?? "587");

            using var client = new SmtpClient();
            client.CheckCertificateRevocation = false;
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;

            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(user, pass);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("📧 Đã gửi email thành công đến {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Lỗi khi gửi email qua SMTP đến {Email}: {Message}", toEmail, ex.Message);
            // Vẫn log chi tiết để nhà phát triển có thể kiểm tra nếu SMTP gặp sự cố mạng
            throw;
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
}
