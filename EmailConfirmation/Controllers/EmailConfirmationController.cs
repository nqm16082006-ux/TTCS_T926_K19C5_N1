using EmailConfirmation.Services;
using Microsoft.AspNetCore.Mvc;

namespace EmailConfirmation.Controllers;

/// <summary>
/// Controller xử lý các yêu cầu xác nhận email.
/// Đây là điểm tích hợp chính cho chức năng xác nhận email.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class EmailConfirmationController : ControllerBase
{
    private readonly IEmailConfirmationService _confirmationService;
    private readonly ILogger<EmailConfirmationController> _logger;

    public EmailConfirmationController(
        IEmailConfirmationService confirmationService,
        ILogger<EmailConfirmationController> logger)
    {
        _confirmationService = confirmationService;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/email-confirmation/confirm?userId=xxx&token=yyy
    // AC3, AC5: Xử lý nhấn link từ email
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Xác nhận email thông qua liên kết được gửi trong email (AC3, AC5).
    /// Endpoint này được nhúng trong URL email xác nhận.
    /// </summary>
    /// <param name="userId">ID người dùng.</param>
    /// <param name="token">Token xác nhận mã hóa Base64Url.</param>
    /// <returns>
    /// 200 OK nếu xác nhận thành công (AC3).
    /// 400 Bad Request nếu token không hợp lệ/hết hạn (AC5).
    /// </returns>
    [HttpGet("confirm")]
    [ProducesResponseType(typeof(ConfirmEmailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConfirmEmailResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail(
        [FromQuery] string userId,
        [FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new ConfirmEmailResponse(false, "Thiếu thông tin xác nhận."));
        }

        var (success, message) = await _confirmationService.ConfirmEmailAsync(userId, token);

        if (success)
        {
            // AC3: Xác nhận thành công → tài khoản kích hoạt
            return Ok(new ConfirmEmailResponse(true, message));
        }

        // AC5: Token không hợp lệ → từ chối kích hoạt
        return BadRequest(new ConfirmEmailResponse(false, message));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /api/email-confirmation/resend
    // Cho phép gửi lại email xác nhận khi link cũ hết hạn
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gửi lại email xác nhận khi link cũ hết hạn hoặc bị mất (AC1, AC2).
    /// </summary>
    /// <param name="request">Thông tin yêu cầu gửi lại.</param>
    [HttpPost("resend")]
    [ProducesResponseType(typeof(ConfirmEmailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConfirmEmailResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResendConfirmationEmail([FromBody] ResendConfirmationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return BadRequest(new ConfirmEmailResponse(false, "Thiếu thông tin người dùng."));
        }

        try
        {
            await _confirmationService.GenerateAndSendConfirmationAsync(
                request.UserId,
                request.Email,
                request.FullName);

            return Ok(new ConfirmEmailResponse(true,
                "Email xác nhận đã được gửi lại. Vui lòng kiểm tra hộp thư của bạn."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi gửi lại email cho userId={UserId}", request.UserId);
            return BadRequest(new ConfirmEmailResponse(false, "Không thể gửi email xác nhận. Vui lòng thử lại sau."));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/email-confirmation/status/{userId}
    // AC4: Kiểm tra trạng thái xác nhận (dùng trong middleware/guard)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kiểm tra trạng thái xác nhận email của tài khoản (AC4).
    /// Dùng để guard các chức năng yêu cầu tài khoản đã kích hoạt.
    /// </summary>
    [HttpGet("status/{userId}")]
    [ProducesResponseType(typeof(EmailStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEmailConfirmationStatus(string userId)
    {
        var isConfirmed = await _confirmationService.IsEmailConfirmedAsync(userId);

        return Ok(new EmailStatusResponse(
            userId,
            isConfirmed,
            isConfirmed
                ? "Tài khoản đã được kích hoạt."
                // AC4: Tài khoản chưa xác nhận không được coi là đã kích hoạt
                : "Tài khoản chưa được kích hoạt. Vui lòng xác nhận email."
        ));
    }
}

// ─── DTOs / Response Models ───────────────────────────────────────────────────

/// <summary>Kết quả xác nhận email.</summary>
public record ConfirmEmailResponse(bool Success, string Message);

/// <summary>Trạng thái xác nhận email của tài khoản.</summary>
public record EmailStatusResponse(string UserId, bool IsEmailConfirmed, string Message);

/// <summary>Yêu cầu gửi lại email xác nhận.</summary>
public record ResendConfirmationRequest(string UserId, string Email, string FullName);
