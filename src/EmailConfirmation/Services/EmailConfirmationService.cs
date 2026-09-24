using EmailConfirmation.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;

namespace EmailConfirmation.Services;

/// <summary>
/// Triển khai dịch vụ xác nhận email sử dụng ASP.NET Core Identity.
/// Identity tự tạo token bảo mật (data-protection based), validate, và cập nhật
/// cờ <see cref="ApplicationUser.EmailConfirmed"/> trong database.
/// </summary>
public class EmailConfirmationService : IEmailConfirmationService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailConfirmationService> _logger;

    public EmailConfirmationService(
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<EmailConfirmationService> logger)
    {
        _userManager = userManager;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC1 + AC2: Tạo token và gửi email xác nhận
    // ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task GenerateAndSendConfirmationAsync(string userId, string email, string fullName)
    {
        var user = await _userManager.FindByIdAsync(userId)
            ?? throw new InvalidOperationException($"Không tìm thấy user với Id={userId}");

        // Identity tạo token mã hóa bảo mật cho việc xác nhận email (AC2)
        var rawToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        // Encode token để an toàn khi truyền qua URL
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken));

        // Tạo link xác nhận đầy đủ
        var baseUrl = _configuration["AppSettings:BaseUrl"] ?? "https://localhost:7001";
        var confirmationLink = $"{baseUrl}/api/email-confirmation/confirm?userId={userId}&token={encodedToken}";

        _logger.LogInformation("🔑 Đã tạo token xác nhận email cho user {UserId}", userId);

        // AC1: Gửi email ngay sau khi đăng ký thành công
        await _emailService.SendConfirmationEmailAsync(email, fullName, confirmationLink);
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC3 + AC5: Xác nhận token và kích hoạt tài khoản
    // ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<(bool Success, string Message)> ConfirmEmailAsync(string userId, string token)
    {
        // Tìm user theo ID
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            _logger.LogWarning("⚠️ Xác nhận email thất bại: không tìm thấy userId={UserId}", userId);
            // AC5: Token không hợp lệ → từ chối kích hoạt
            return (false, "Liên kết xác nhận không hợp lệ.");
        }

        // Kiểm tra nếu đã xác nhận rồi
        if (user.EmailConfirmed)
        {
            return (false, "Email này đã được xác nhận trước đó.");
        }

        // Decode token từ URL-safe Base64
        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (Exception)
        {
            _logger.LogWarning("⚠️ Token không đúng định dạng Base64Url cho userId={UserId}", userId);
            // AC5: Token không hợp lệ
            return (false, "Token xác nhận không hợp lệ hoặc đã bị sửa đổi.");
        }

        // Identity validate token: kiểm tra chữ ký + hạn dùng (mặc định 1 ngày)
        // Nếu token hết hạn hoặc bị sửa đổi → ConfirmEmailAsync trả về Failure (AC5)
        var result = await _userManager.ConfirmEmailAsync(user, decodedToken);

        if (result.Succeeded)
        {
            // AC3: Token hợp lệ → tài khoản được kích hoạt (EmailConfirmed = true)
            _logger.LogInformation("✅ Tài khoản {Email} đã được kích hoạt thành công.", user.Email);
            return (true, "Tài khoản của bạn đã được xác nhận và kích hoạt thành công!");
        }

        // AC5: Identity từ chối → token không hợp lệ hoặc hết hạn
        var errors = string.Join("; ", result.Errors.Select(e => e.Description));
        _logger.LogWarning("❌ Xác nhận email thất bại cho {Email}: {Errors}", user.Email, errors);
        return (false, "Liên kết xác nhận không hợp lệ hoặc đã hết hạn. Vui lòng yêu cầu gửi lại email xác nhận.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC4: Kiểm tra trạng thái xác nhận email
    // ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<bool> IsEmailConfirmedAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return false;

        // AC4: Trả về false nếu chưa xác nhận → tài khoản chưa được kích hoạt
        return user.EmailConfirmed;
    }
}
