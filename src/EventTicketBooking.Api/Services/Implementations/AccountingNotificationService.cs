using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ gửi thông báo cho kế toán khi lệnh hoàn tiền thất bại (Jira Story S-48).
    /// </summary>
    public class AccountingNotificationService : IAccountingNotificationService
    {
        private readonly IEmailService _emailService;
        private readonly AccountingOptions _options;
        private readonly ILogger<AccountingNotificationService> _logger;
        private readonly AppDbContext _context;

        public AccountingNotificationService(
            IEmailService emailService,
            IOptions<AccountingOptions> options,
            ILogger<AccountingNotificationService> logger,
            AppDbContext context)
        {
            _emailService = emailService;
            _options = options.Value;
            _logger = logger;
            _context = context;
        }

        public async Task<bool> NotifyFailedRefundAsync(RefundRequest refund, CancellationToken cancellationToken = default)
        {
            if (refund == null)
            {
                throw new ArgumentNullException(nameof(refund));
            }

            // 1. Chống gửi trùng lặp (Idempotent): Nếu đã gửi thành công trước đó thì dừng ngay
            if (refund.AccountingNotified)
            {
                _logger.LogInformation("Thông báo kế toán cho RefundId {RefundId} đã được gửi thành công trước đó vào {NotifiedAt}. Bỏ qua gửi trùng.",
                    refund.Id, refund.AccountingNotifiedAt);
                return true;
            }

            // 2. Kiểm tra cấu hình địa chỉ email kế toán
            string targetEmail = _options.Email?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetEmail))
            {
                _logger.LogWarning("Chưa cấu hình địa chỉ email kế toán (Accounting:Email). Không thể gửi thông báo cho RefundId {RefundId}.", refund.Id);
                refund.AccountingNotified = false;
                refund.AccountingNotificationError = "Cấu hình email kế toán (Accounting:Email) đang để trống.";
                await _context.SaveChangesAsync(cancellationToken);
                return false;
            }

            // 3. Chuẩn bị nội dung email (làm sạch dữ liệu nhạy cảm)
            string sanitizedError = SanitizeText(refund.ErrorMessage ?? "Không có mô tả chi tiết.");
            string sanitizedCode = SanitizeText(refund.ErrorCode ?? "UNKNOWN_ERROR");
            string formattedAmount = refund.Amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " " + refund.Currency;
            string failedTime = (refund.FailedAt ?? DateTimeOffset.UtcNow).ToString("HH:mm:ss dd/MM/yyyy UTC");

            string subject = $"[KHẨN CẤP] Cần xử lý hoàn tiền thủ công - Đơn hàng #{refund.OrderCode} (Refund #{refund.Id.ToString()[..8]})";
            string htmlBody = BuildHtmlBody(refund, sanitizedCode, sanitizedError, formattedAmount, failedTime);

            try
            {
                _logger.LogInformation("Đang gửi email thông báo thất bại hoàn tiền cho Kế toán tại {Email} (RefundId: {RefundId})", targetEmail, refund.Id);

                bool isSent = await _emailService.SendAccountingAlertEmailAsync(targetEmail, subject, htmlBody);
                if (isSent)
                {
                    refund.AccountingNotified = true;
                    refund.AccountingNotifiedAt = DateTimeOffset.UtcNow;
                    refund.AccountingNotificationError = null;
                    refund.UpdatedAt = DateTimeOffset.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation("Đã gửi thông báo thành công cho Kế toán về RefundId {RefundId}.", refund.Id);
                    return true;
                }
                else
                {
                    refund.AccountingNotified = false;
                    refund.AccountingNotificationError = "Dịch vụ email trả về false.";
                    refund.UpdatedAt = DateTimeOffset.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi email thông báo kế toán cho RefundId {RefundId}", refund.Id);
                refund.AccountingNotified = false;
                refund.AccountingNotificationError = SanitizeText(ex.Message);
                refund.UpdatedAt = DateTimeOffset.UtcNow;

                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Lỗi khi lưu trạng thái lỗi thông báo kế toán cho RefundId {RefundId}", refund.Id);
                }

                return false;
            }
        }

        private static string SanitizeText(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // Xóa dấu vết số thẻ tín dụng (13-19 số)
            string sanitized = System.Text.RegularExpressions.Regex.Replace(input, @"\b(?:\d[ -]*?){13,19}\b", "****-****-****-****");
            // Xóa secret keys, tokens, mật khẩu
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"(?i)(api[-_]?key|secret|password|token)\s*[:=]\s*[^\s,;]+", "$1=********");
            return sanitized;
        }

        private string BuildHtmlBody(RefundRequest refund, string errorCode, string errorMessage, string formattedAmount, string failedTime)
        {
            string department = WebUtility.HtmlEncode(_options.DepartmentName ?? "Ban Kế toán & Tài chính");
            string refundId = refund.Id.ToString();
            string orderId = refund.OrderId.ToString();
            string orderCode = refund.OrderCode.ToString();
            string txId = WebUtility.HtmlEncode(refund.TransactionId ?? "N/A");
            string reason = WebUtility.HtmlEncode(refund.Reason ?? "Không rõ");

            return $"""
                <!DOCTYPE html>
                <html lang="vi">
                <head>
                  <meta charset="UTF-8">
                  <title>Yêu cầu hoàn tiền cần xử lý thủ công</title>
                </head>
                <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 24px; color: #1e293b;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="max-width: 650px; margin: 0 auto; background-color: #ffffff; border-radius: 12px; border: 1px solid #e2e8f0; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.05);">
                    <tr>
                      <td style="background-color: #dc2626; padding: 24px; color: #ffffff; text-align: center;">
                        <h2 style="margin: 0; font-size: 20px; font-weight: 700;">🚨 THÔNG BÁO HOÀN TIỀN THẤT BẠI - CẦN XỬ LÝ THỦ CÔNG</h2>
                        <p style="margin: 6px 0 0 0; font-size: 14px; opacity: 0.95;">Kính gửi: {department}</p>
                      </td>
                    </tr>
                    <tr>
                      <td style="padding: 24px;">
                        <p style="margin-top: 0; font-size: 15px; line-height: 1.5;">
                          Hệ thống bán vé tự động <strong>EventPulse</strong> đã hoàn tất <strong>{refund.RetryCount} lần thử lại (tối đa {refund.MaxRetries} lần retry)</strong> cho lệnh hoàn tiền dưới đây nhưng không thành công qua cổng thanh toán tự động.
                        </p>

                        <table width="100%" cellpadding="8" cellspacing="0" style="border-collapse: collapse; margin: 20px 0; background-color: #f8fafc; border-radius: 8px; font-size: 14px;">
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; width: 35%; color: #475569;">Mã yêu cầu hoàn tiền (Refund ID):</td>
                            <td style="font-family: monospace; font-weight: 600; color: #0f172a;">{refundId}</td>
                          </tr>
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; color: #475569;">Mã đơn hàng (Order ID / Code):</td>
                            <td>#{orderCode} <span style="font-size: 12px; color: #64748b;">({orderId})</span></td>
                          </tr>
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; color: #475569;">Mã giao dịch gốc (Transaction ID):</td>
                            <td style="font-family: monospace;">{txId}</td>
                          </tr>
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; color: #475569;">Số tiền cần hoàn:</td>
                            <td style="font-size: 16px; font-weight: 700; color: #dc2626;">{formattedAmount}</td>
                          </tr>
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; color: #475569;">Lý do hoàn tiền:</td>
                            <td>{reason}</td>
                          </tr>
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; color: #475569;">Tổng số lần thử / Retry:</td>
                            <td>{refund.AttemptCount} lần thử ({refund.RetryCount} lần retry bổ sung)</td>
                          </tr>
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; color: #475569;">Thời điểm lỗi cuối cùng:</td>
                            <td>{failedTime}</td>
                          </tr>
                          <tr style="border-bottom: 1px solid #e2e8f0;">
                            <td style="font-weight: 600; color: #475569;">Mã lỗi (Error Code):</td>
                            <td style="color: #b91c1c; font-weight: 600;">{errorCode}</td>
                          </tr>
                          <tr>
                            <td style="font-weight: 600; color: #475569;">Chi tiết lỗi (Sanitized):</td>
                            <td style="color: #64748b; font-size: 13px;">{errorMessage}</td>
                          </tr>
                        </table>

                        <div style="background-color: #fef2f2; border-left: 4px solid #ef4444; padding: 16px; border-radius: 4px; margin: 24px 0;">
                          <h4 style="margin: 0 0 8px 0; color: #991b1b; font-size: 15px;">📋 HƯỚNG DẪN XỬ LÝ THỦ CÔNG DÀNH CHO KẾ TOÁN:</h4>
                          <ol style="margin: 0; padding-left: 20px; font-size: 14px; line-height: 1.6; color: #7f1d1d;">
                            <li>Kiểm tra sao kê cổng thanh toán / ngân hàng đối tác theo <strong>Mã giao dịch gốc ({txId})</strong> để xác nhận số tiền thực nhận.</li>
                            <li>Xác minh xem khách hàng đã nhận được khoản hoàn trả từ hệ thống ngân hàng hay chưa.</li>
                            <li>Nếu cổng thanh toán chưa hoàn tiền, tiến hành chuyển khoản hoàn tiền thủ công cho khách hàng theo thông tin chủ đơn hàng.</li>
                            <li>Cập nhật trạng thái xử lý vào cổng quản trị hoặc ghi nhận chứng từ kế toán theo quy định nội bộ.</li>
                          </ol>
                        </div>

                        <p style="font-size: 12px; color: #94a3b8; margin-top: 24px; border-top: 1px solid #e2e8f0; padding-top: 12px;">
                          Email này được tạo tự động bởi EventPulse System (Jira Story S-48). Không trả lời trực tiếp email này.
                        </p>
                      </td>
                    </tr>
                  </table>
                </body>
                </html>
                """;
        }
    }
}
