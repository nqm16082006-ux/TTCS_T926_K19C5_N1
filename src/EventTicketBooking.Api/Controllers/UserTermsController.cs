using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/user/terms")]
    public class UserTermsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ILogger<UserTermsController> _logger;

        public UserTermsController(
            AppDbContext context,
            IEmailService emailService,
            ILogger<UserTermsController> logger)
        {
            _context = context;
            _emailService = emailService;
            _logger = logger;
        }

        /// <summary>
        /// Lấy thông tin Điều khoản dịch vụ và Chính sách riêng tư hiện hành (Công khai).
        /// </summary>
        [HttpGet("policy")]
        [ProducesResponseType(typeof(TermsPolicyInfoDto), StatusCodes.Status200OK)]
        public IActionResult GetCurrentPolicy()
        {
            var policyInfo = new TermsPolicyInfoDto
            {
                Version = TermsPolicy.CurrentVersion,
                EffectiveDate = "2026-10-09",
                Title = "Điều khoản dịch vụ và Chính sách bảo vệ dữ liệu cá nhân EventPulse",
                Summary = "Quy định về điều khoản dịch vụ, bảo vệ quyền riêng tư và quyền thu hồi sự đồng ý nhận email tiếp thị theo Nghị định 13/2023/NĐ-CP.",
                TermsContent = "1. Chấp thuận điều khoản: Bằng việc đăng ký tài khoản hoặc sử dụng hệ thống EventPulse, bạn đồng ý tuân thủ toàn bộ các điều khoản được quy định tại đây.\n2. Tài khoản và Bảo mật: Người dùng có trách nhiệm bảo mật thông tin đăng nhập và mã xác thực hai lớp (OTP).\n3. Mua vé và Thanh toán: Mọi giao dịch đặt vé tuân thủ chính sách giữ chỗ và thanh toán theo từng sự kiện.\n4. Cập nhật điều khoản: Khi hệ thống nâng cấp phiên bản điều khoản mới, người dùng sẽ được yêu cầu xác nhận lại tại lần đăng nhập kế tiếp.",
                PrivacyContent = "1. Thu thập dữ liệu: Chúng tôi thu thập thông tin cá nhân cần thiết (họ tên, email, lịch sử đơn hàng) nhằm mục đích cung cấp dịch vụ đặt vé và soát vé an toàn.\n2. Bảo mật thông tin: Dữ liệu vé và mã QR được ký số mật mã ECDSA để chống giả mạo.\n3. Email tiếp thị và Quyền thu hồi: Bạn có quyền thu hồi sự đồng ý nhận email tiếp thị/khuyến mãi bất cứ lúc nào trong mục 'Cài đặt riêng tư'. Sau khi thu hồi, hệ thống cam kết không gửi email tiếp thị đến bạn nữa."
            };

            return Ok(policyInfo);
        }

        /// <summary>
        /// Lấy trạng thái chấp thuận điều khoản và sự đồng ý tiếp thị của người dùng hiện tại (AC1, AC2, AC4).
        /// </summary>
        [HttpGet("status")]
        [RequireRole]
        [ProducesResponseType(typeof(UserTermsStatusDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetTermsStatus()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            var statusDto = new UserTermsStatusDto
            {
                UserId = user.Id,
                Email = user.Email,
                FullName = user.FullName ?? user.Username,
                TermsVersion = user.TermsVersion,
                TermsAcceptedAt = user.TermsAcceptedAt,
                CurrentPolicyVersion = TermsPolicy.CurrentVersion,
                IsTermsCurrent = TermsPolicy.IsCurrent(user),
                MarketingEmailOptIn = user.MarketingEmailOptIn,
                CanReceiveMarketingEmails = user.MarketingEmailOptIn
            };

            return Ok(statusDto);
        }

        /// <summary>
        /// Người dùng thu hồi quyền nhận email tiếp thị (AC4).
        /// Sau khi thu hồi, hệ thống không gửi email tiếp thị nữa.
        /// </summary>
        [HttpPost("revoke-marketing")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> RevokeMarketingConsent()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            user.MarketingEmailOptIn = false;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đã thu hồi quyền nhận email tiếp thị.", user.Email, user.Id);

            return Ok(new
            {
                success = true,
                marketingEmailOptIn = false,
                message = "Đã thu hồi quyền nhận email tiếp thị. Hệ thống sẽ không gửi email tiếp thị nữa."
            });
        }

        /// <summary>
        /// Người dùng đăng ký nhận lại email tiếp thị (Tùy chọn tái kích hoạt).
        /// </summary>
        [HttpPost("opt-in-marketing")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> OptInMarketingConsent()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            user.MarketingEmailOptIn = true;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đã đồng ý nhận lại email tiếp thị.", user.Email, user.Id);

            return Ok(new
            {
                success = true,
                marketingEmailOptIn = true,
                message = "Đã đăng ký nhận email tiếp thị và thông tin ưu đãi thành công."
            });
        }

        /// <summary>
        /// Người dùng đồng ý với phiên bản Điều khoản và Chính sách riêng tư hiện hành (AC3).
        /// </summary>
        [HttpPost("accept")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> AcceptCurrentTerms()
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            TermsPolicy.AcceptCurrent(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đã đồng ý điều khoản phiên bản {Version}.",
                user.Email, user.Id, user.TermsVersion);

            return Ok(new
            {
                success = true,
                termsVersion = user.TermsVersion,
                termsAcceptedAt = user.TermsAcceptedAt,
                message = $"Đã chấp thuận Điều khoản và Chính sách riêng tư phiên bản {user.TermsVersion} thành công."
            });
        }

        /// <summary>
        /// Gửi thử nghiệm email tiếp thị đến người dùng để kiểm chứng tiêu chí:
        /// 'Người dùng thu hồi được và khi đó không nhận email tiếp thị nữa' (AC4).
        /// </summary>
        [HttpPost("send-marketing-test")]
        [RequireRole]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> SendMarketingTest([FromBody] SendMarketingTestDto? request)
        {
            var user = await GetCurrentUserAsync();
            if (user == null)
            {
                return Unauthorized(new { message = "Vui lòng đăng nhập để tiếp tục." });
            }

            // KIỂM TRA QUYỀN: Nếu người dùng đã thu hồi quyền nhận email tiếp thị -> Chặn hoàn toàn!
            if (!user.MarketingEmailOptIn)
            {
                _logger.LogWarning("Chặn gửi email tiếp thị tới {Email} vì người dùng đã thu hồi quyền nhận tiếp thị.", user.Email);
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    success = false,
                    sent = false,
                    canReceiveMarketingEmails = false,
                    message = "Người dùng đã thu hồi quyền nhận email tiếp thị. Hệ thống từ chối gửi email tiếp thị."
                });
            }

            // Người dùng đang đồng ý nhận tiếp thị -> Gửi email
            var subject = request?.Subject ?? "🎁 Ưu đãi độc quyền vé Concert cuối tuần từ EventPulse";
            var content = request?.Content ?? "Giảm giá 20% cho đơn hàng tiếp theo khi đặt vé qua ứng dụng EventPulse trong tuần này.";

            var sent = await _emailService.SendMarketingEmailAsync(
                user.Email,
                user.FullName ?? user.Username,
                subject,
                content);

            return Ok(new
            {
                success = true,
                sent = sent,
                canReceiveMarketingEmails = true,
                message = $"Email tiếp thị đã được gửi thành công đến {user.Email}."
            });
        }

        /// <summary>
        /// Quản trị viên cập nhật phiên bản Điều khoản / Chính sách riêng tư của toàn hệ thống (AC3).
        /// </summary>
        [HttpPost("admin/change-policy-version")]
        [RequireRole("Admin")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public IActionResult ChangePolicyVersion([FromBody] UpdatePolicyVersionDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.NewVersion))
            {
                return BadRequest(new { message = "Phiên bản mới không được để trống." });
            }

            TermsPolicy.SetGlobalVersion(request.NewVersion.Trim());
            _logger.LogInformation("Quản trị viên đã đổi phiên bản chính sách hệ thống sang {Version}", TermsPolicy.CurrentVersion);

            return Ok(new
            {
                success = true,
                currentVersion = TermsPolicy.CurrentVersion,
                message = $"Đã cập nhật phiên bản chính sách hệ thống thành công sang {TermsPolicy.CurrentVersion}."
            });
        }

        private async Task<User?> GetCurrentUserAsync()
        {
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? User.FindFirst("id")?.Value
                              ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (!Guid.TryParse(userIdValue, out var userId))
            {
                return null;
            }

            return await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        }
    }
}
