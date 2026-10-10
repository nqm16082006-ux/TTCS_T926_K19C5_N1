using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller phụ trách xác thực người dùng (Task TTKN-25 - Story S-02).
    /// Hỗ trợ Đăng nhập/Đăng ký chuẩn bằng Email & Mật khẩu + OTP 6 số và Tiếp tục bằng Google.
    /// </summary>
    [ApiController]
    [Route("api/auth")]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly AppDbContext _context;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IEmailService _emailService;
        private readonly ITokenService _tokenService;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            IAuthService authService,
            AppDbContext context,
            IPasswordHasher passwordHasher,
            IEmailService emailService,
            ITokenService tokenService,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<AuthController> logger)
        {
            _authService = authService;
            _context = context;
            _passwordHasher = passwordHasher;
            _emailService = emailService;
            _tokenService = tokenService;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// API Đăng nhập bằng Email và Mật khẩu.
        /// </summary>
        [HttpPost("login")]
        [ProducesResponseType(typeof(LoginResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status423Locked)]
        public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var result = await _authService.LoginAsync(request);

            if (result.Success && result.Data != null)
            {
                return Ok(result.Data);
            }

            if (result.IsLocked)
            {
                return StatusCode(StatusCodes.Status423Locked, new
                {
                    statusCode = StatusCodes.Status423Locked,
                    message = result.Message,
                    isLocked = true
                });
            }

            if (result.RequiresTermsAcceptance)
            {
                return StatusCode(StatusCodes.Status428PreconditionRequired, new
                {
                    statusCode = StatusCodes.Status428PreconditionRequired,
                    requiresTermsAcceptance = true,
                    termsVersion = result.RequiredTermsVersion,
                    message = result.Message
                });
            }

            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                statusCode = StatusCodes.Status401Unauthorized,
                message = result.Message
            });
        }



        /// <summary>
        /// Bước 1: API Đăng ký tài khoản mới qua Email & Mật khẩu -> Gửi mã xác nhận OTP 6 số về email.
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            // Kiểm tra user đã tồn tại
            var existingUser = await _context.Users
                .Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            if (existingUser != null && existingUser.IsActive)
            {
                return BadRequest(new { message = "Email này đã được đăng ký và kích hoạt trong hệ thống. Vui lòng đăng nhập." });
            }

            if (existingUser != null && string.IsNullOrEmpty(existingUser.VerificationCode))
                return StatusCode(StatusCodes.Status423Locked, new { message = "Tài khoản đã bị khóa bởi Quản trị viên." });

            if (!request.AcceptTerms)
            {
                return BadRequest(new { message = "Bạn cần đồng ý với Điều khoản dịch vụ và Chính sách riêng tư để đăng ký." });
            }

            // Lấy Role "Customer" từ Database
            var customerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "Customer");
            if (customerRole == null)
            {
                customerRole = new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "Customer",
                    Description = "Khách hàng và người tham dự",
                    CreatedAt = DateTime.UtcNow
                };
                _context.Roles.Add(customerRole);
                await _context.SaveChangesAsync();
            }

            // Sinh mã OTP 6 chữ số an toàn
            string otpCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            DateTime expiresAt = DateTime.UtcNow.AddMinutes(15);
            string hashedPassword = _passwordHasher.Hash(request.Password);

            User targetUser;

            if (existingUser != null)
            {
                // Người dùng đã đăng ký trước đó nhưng chưa xác thực OTP -> cập nhật lại thông tin & mã mới
                existingUser.FullName = request.FullName?.Trim();
                existingUser.PasswordHash = hashedPassword;
                existingUser.VerificationCode = otpCode;
                existingUser.VerificationCodeExpiresAt = expiresAt;
                EventTicketBooking.Api.Services.TermsPolicy.AcceptCurrent(existingUser);
                existingUser.MarketingEmailOptIn = true;
                existingUser.UpdatedAt = DateTime.UtcNow;
                targetUser = existingUser;
            }
            else
            {
                // Dùng phần trước @ của Email để làm Username
                string baseUsername = normalizedEmail.Split('@')[0];
                if (baseUsername.Length > 40) baseUsername = baseUsername[..40];
                string username = baseUsername;
                int counter = 1;
                while (await _context.Users.AnyAsync(u => u.Username == username))
                {
                    username = $"{baseUsername}{counter}";
                    counter++;
                }

                targetUser = new User
                {
                    Id = Guid.NewGuid(),
                    FullName = request.FullName?.Trim(),
                    Email = normalizedEmail,
                    Username = username,
                    PasswordHash = hashedPassword,
                    IsActive = false,
                    VerificationCode = otpCode,
                    VerificationCodeExpiresAt = expiresAt,
                    TermsVersion = EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion,
                    TermsAcceptedAt = DateTime.UtcNow,
                    MarketingEmailOptIn = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                targetUser.UserRoles.Add(new UserRole
                {
                    UserId = targetUser.Id,
                    RoleId = customerRole.Id
                });

                _context.Users.Add(targetUser);
            }

            await _context.SaveChangesAsync();

            try
            {
                await _emailService.SendOtpEmailAsync(
                    targetUser.Email,
                    targetUser.FullName ?? targetUser.Username,
                    otpCode);
            }
            catch (EventTicketBooking.Api.Services.EmailDeliveryException ex)
            {
                _logger.LogError(ex, "Could not deliver registration OTP to {Email}", targetUser.Email);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    success = false,
                    requiresOtp = false,
                    email = targetUser.Email,
                    message = "Không thể gửi mã xác thực qua email lúc này. Tài khoản đã được lưu; vui lòng thử gửi lại mã sau."
                });
            }

            return Ok(new
            {
                success = true,
                requiresOtp = true,
                email = targetUser.Email,
                message = "Mã xác thực gồm 6 chữ số đã được gửi tới email của bạn. Vui lòng kiểm tra hộp thư."
            });
        }

        /// <summary>
        /// Bước 2: API Xác thực mã OTP 6 số để hoàn tất đăng ký tài khoản.
        /// </summary>
        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyEmailDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var code = request.Code.Trim();

            var user = await _context.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            if (user == null)
            {
                return BadRequest(new { message = "Không tìm thấy thông tin tài khoản đăng ký với email này." });
            }

            if (user.IsActive)
            {
                return BadRequest(new { message = "Tài khoản này đã được kích hoạt trước đó. Vui lòng đăng nhập." });
            }

            if (string.IsNullOrEmpty(user.VerificationCode) ||
                user.VerificationCode != code ||
                !user.VerificationCodeExpiresAt.HasValue ||
                user.VerificationCodeExpiresAt.Value < DateTime.UtcNow)
            {
                return BadRequest(new { message = "Mã xác thực không chính xác hoặc đã hết hạn (15 phút). Vui lòng thử lại hoặc bấm Gửi lại mã." });
            }

            // Kích hoạt tài khoản
            user.IsActive = true;
            user.VerificationCode = null;
            user.VerificationCodeExpiresAt = null;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (!EventTicketBooking.Api.Services.TermsPolicy.IsCurrent(user))
            {
                return StatusCode(StatusCodes.Status428PreconditionRequired, new
                {
                    statusCode = StatusCodes.Status428PreconditionRequired,
                    requiresTermsAcceptance = true,
                    termsVersion = EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion,
                    message = "Tài khoản đã được kích hoạt. Vui lòng đăng nhập và chấp nhận Điều khoản dịch vụ hiện hành để tiếp tục."
                });
            }

            // Gửi email xác nhận đăng ký thành công cho người dùng qua Email & Mật khẩu
            _ = Task.Run(async () =>
            {
                try
                {
                    await _emailService.SendWelcomeEmailAsync(user.Email, user.FullName ?? user.Username, "Email & Mật khẩu");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi gửi email xác nhận đăng ký thành công cho {Email}", user.Email);
                }
            });

            // Lấy danh sách Roles
            var roles = user.UserRoles
                .Where(ur => ur.Role != null)
                .Select(ur => ur.Role!.Name)
                .ToList();

            if (roles.Count == 0)
            {
                roles.Add("Customer");
            }

            // Tự động tạo Access Token và đăng nhập ngay
            var accessToken = _tokenService.GenerateAccessToken(user, roles);

            _logger.LogInformation("Người dùng {Email} đã kích hoạt tài khoản thành công qua OTP 6 số!", user.Email);

            return Ok(new LoginResponseDto
            {
                AccessToken = accessToken,
                TokenType = "Bearer",
                ExpiresIn = 86400,
                User = new UserDto
                {
                    Id = user.Id,
                    Username = user.Username,
                    Email = user.Email,
                    FullName = user.FullName,
                    Roles = roles
                }
            });
        }

        /// <summary>
        /// API Gửi lại mã xác nhận OTP (Resend OTP).
        /// </summary>
        [HttpPost("resend-otp")]
        public async Task<IActionResult> ResendOtp([FromBody] ResendOtpDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            if (user == null)
            {
                return BadRequest(new { message = "Email chưa được đăng ký trong hệ thống." });
            }

            if (user.IsActive)
            {
                return BadRequest(new { message = "Tài khoản này đã được kích hoạt, bạn có thể đăng nhập ngay." });
            }

            if (string.IsNullOrEmpty(user.VerificationCode))
                return StatusCode(StatusCodes.Status423Locked, new { message = "Tài khoản đã bị khóa bởi Quản trị viên." });

            // Sinh mã OTP 6 số mới
            string newOtp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            user.VerificationCode = newOtp;
            user.VerificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(15);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            try
            {
                await _emailService.SendOtpEmailAsync(
                    user.Email,
                    user.FullName ?? user.Username,
                    newOtp);
            }
            catch (EventTicketBooking.Api.Services.EmailDeliveryException ex)
            {
                _logger.LogError(ex, "Could not resend OTP to {Email}", user.Email);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    success = false,
                    message = "Không thể gửi mã xác thực qua email lúc này. Vui lòng thử lại sau."
                });
            }

            return Ok(new { success = true, message = "Đã gửi lại mã xác thực mới vào email của bạn. Vui lòng kiểm tra hộp thư." });
        }

        /// <summary>
        /// API Tiếp tục bằng Google (Google Sign-In / OAuth).
        /// Xác thực ID Token từ Google Identity Services và đăng nhập / đăng ký tự động.
        /// </summary>
        [HttpPost("google")]
        public async Task<IActionResult> GoogleAuth([FromBody] GoogleAuthRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.Credential))
            {
                return BadRequest(new { message = "Mã xác thực Google không hợp lệ hoặc bị thiếu." });
            }
            if (string.Equals(request.Mode, "register", StringComparison.OrdinalIgnoreCase) && !request.AcceptTerms)
            {
                return BadRequest(new { message = "Bạn cần đồng ý với Điều khoản dịch vụ để đăng ký." });
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                // Gọi API xác thực Google tokeninfo chuẩn
                var response = await client.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(request.Credential)}");

                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { message = "Mã xác thực Google không hợp lệ hoặc đã hết hạn." });
                }

                var payload = await response.Content.ReadFromJsonAsync<GoogleTokenPayload>();
                if (payload == null || string.IsNullOrWhiteSpace(payload.Email))
                {
                    return BadRequest(new { message = "Không thể đọc thông tin người dùng từ Google." });
                }

                // Kiểm tra Audience (Client ID)
                var expectedClientId = _configuration["GoogleAuth:ClientId"];
                if (string.IsNullOrWhiteSpace(expectedClientId) || !string.Equals(payload.Aud, expectedClientId, StringComparison.Ordinal) ||
                    !string.Equals(payload.EmailVerified, "true", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Google Token Aud '{Aud}' không khớp cấu hình '{Expected}'", payload.Aud, expectedClientId);
                    return BadRequest(new { message = "Google Client ID không khớp với hệ thống." });
                }

                var normalizedEmail = payload.Email.Trim().ToLowerInvariant();

                // Tìm hoặc tạo User
                var user = await _context.Users
                    .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                    .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

                if (user == null)
                {
                    if (!request.AcceptTerms && !request.AcceptCurrentTerms)
                    {
                        return StatusCode(StatusCodes.Status428PreconditionRequired, new
                        {
                            statusCode = StatusCodes.Status428PreconditionRequired,
                            requiresTermsAcceptance = true,
                            termsVersion = EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion,
                            message = $"Bạn cần chấp nhận Điều khoản dịch vụ phiên bản {EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion} trước khi tiếp tục."
                        });
                    }

                    var customerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "Customer");
                    if (customerRole == null)
                    {
                        customerRole = new Role
                        {
                            Id = Guid.NewGuid(),
                            Name = "Customer",
                            Description = "Khách hàng và người tham dự",
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.Roles.Add(customerRole);
                        await _context.SaveChangesAsync();
                    }

                    string baseUsername = normalizedEmail.Split('@')[0];
                    if (baseUsername.Length > 40) baseUsername = baseUsername[..40];
                    string username = baseUsername;
                    int counter = 1;
                    while (await _context.Users.AnyAsync(u => u.Username == username))
                    {
                        username = $"{baseUsername}{counter}";
                        counter++;
                    }

                    string verificationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

                    user = new User
                    {
                        Id = Guid.NewGuid(),
                        FullName = payload.Name ?? baseUsername,
                        Email = normalizedEmail,
                        Username = username,
                        PasswordHash = _passwordHasher.Hash(Guid.NewGuid().ToString("N")),
                        IsActive = false, // Bắt buộc phải bấm nút xác nhận trong email gửi về
                        VerificationCode = verificationToken,
                        VerificationCodeExpiresAt = DateTime.UtcNow.AddHours(24),
                        TermsVersion = EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion,
                        TermsAcceptedAt = DateTime.UtcNow,
                        MarketingEmailOptIn = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    user.UserRoles.Add(new UserRole
                    {
                        UserId = user.Id,
                        RoleId = customerRole.Id,
                        Role = customerRole
                    });

                    _context.Users.Add(user);
                    await _context.SaveChangesAsync();

                    // Gửi email chứa nút bấm xác nhận kích hoạt tài khoản
                    string scheme = Request.Scheme;
                    string host = Request.Host.Value ?? "localhost";
                    string confirmationLink = $"{scheme}://{host}/activate.html?email={Uri.EscapeDataString(user.Email)}&token={verificationToken}";

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _emailService.SendConfirmationEmailAsync(user.Email, user.FullName ?? user.Username, confirmationLink);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi gửi email xác nhận tài khoản Google cho {Email}", user.Email);
                        }
                    });

                    return Ok(new
                    {
                        success = true,
                        requiresEmailConfirmation = true,
                        email = user.Email,
                        message = "Chúng tôi đã gửi email xác nhận đến hòm thư của bạn. Vui lòng mở email và bấm nút 'Xác nhận Đăng ký Tài khoản' để hoàn tất kích hoạt."
                    });
                }
                else
                {
                    // Nếu tài khoản đã bị khóa bởi Quản trị viên (VerificationCode bị xóa khi khóa)
                    if (!user.IsActive && string.IsNullOrEmpty(user.VerificationCode))
                    {
                        return StatusCode(StatusCodes.Status423Locked, new
                        {
                            statusCode = StatusCodes.Status423Locked,
                            message = "Tài khoản của bạn đã bị khóa bởi Quản trị viên. Vui lòng liên hệ Admin để được hỗ trợ.",
                            isLocked = true
                        });
                    }

                    // Nếu là Đăng ký (Mode == "register") HOẶC tài khoản chưa được kích hoạt:
                    // Bắt buộc gửi email chứa nút bấm xác nhận và yêu cầu người dùng bấm nút
                    if (!user.IsActive)
                    {
                        if (request.AcceptTerms)
                        {
                            EventTicketBooking.Api.Services.TermsPolicy.AcceptCurrent(user);
                            user.MarketingEmailOptIn = true;
                        }

                        user.IsActive = false;
                        string verificationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                        user.VerificationCode = verificationToken;
                        user.VerificationCodeExpiresAt = DateTime.UtcNow.AddHours(24);
                        user.UpdatedAt = DateTime.UtcNow;

                        if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(payload.Name))
                        {
                            user.FullName = payload.Name;
                        }

                        await _context.SaveChangesAsync();

                        string scheme = Request.Scheme;
                        string host = Request.Host.Value ?? "localhost";
                        string confirmationLink = $"{scheme}://{host}/activate.html?email={Uri.EscapeDataString(user.Email)}&token={verificationToken}";

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _emailService.SendConfirmationEmailAsync(user.Email, user.FullName ?? user.Username, confirmationLink);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Lỗi gửi email xác nhận cho {Email}", user.Email);
                            }
                        });

                        return Ok(new
                        {
                            success = true,
                            requiresEmailConfirmation = true,
                            email = user.Email,
                            message = "Chúng tôi đã gửi email xác nhận đến hòm thư của bạn. Vui lòng mở email và nhấn nút 'Xác nhận Đăng ký Tài khoản' để hoàn tất kích hoạt."
                        });
                    }

                    if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(payload.Name))
                    {
                        user.FullName = payload.Name;
                        await _context.SaveChangesAsync();
                    }
                }

                if (!EventTicketBooking.Api.Services.TermsPolicy.IsCurrent(user))
                {
                    if (!request.AcceptCurrentTerms && !request.AcceptTerms)
                    {
                        return StatusCode(StatusCodes.Status428PreconditionRequired, new
                        {
                            statusCode = StatusCodes.Status428PreconditionRequired,
                            requiresTermsAcceptance = true,
                            termsVersion = EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion,
                            message = $"Bạn cần chấp nhận Điều khoản dịch vụ phiên bản {EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion} trước khi đăng nhập."
                        });
                    }

                    EventTicketBooking.Api.Services.TermsPolicy.AcceptCurrent(user);
                    await _context.SaveChangesAsync();
                }

                // Nếu tài khoản ĐÃ được kích hoạt thành công trước đó (đã bấm nút xác nhận trong mail)
                var roles = user.UserRoles
                    .Where(ur => ur.Role != null)
                    .Select(ur => ur.Role!.Name)
                    .ToList();

                if (roles.Count == 0)
                {
                    roles.Add("Customer");
                }

                var accessToken = _tokenService.GenerateAccessToken(user, roles);

                _logger.LogInformation("Người dùng {Email} đăng nhập Google thành công (tài khoản đã kích hoạt)!", user.Email);

                return Ok(new LoginResponseDto
                {
                    AccessToken = accessToken,
                    TokenType = "Bearer",
                    ExpiresIn = 86400,
                    User = new UserDto
                    {
                        Id = user.Id,
                        Username = user.Username,
                        Email = user.Email,
                        FullName = user.FullName,
                        Roles = roles
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xử lý đăng nhập Google.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Lỗi xác thực Google từ máy chủ." });
            }
        }

        /// <summary>
        /// API Xác nhận email khi người dùng bấm vào nút trong email gửi về.
        /// Kích hoạt tài khoản, gửi email chào mừng thành công và tự động đăng nhập.
        /// </summary>
        [HttpGet("verify-email")]
        public async Task<IActionResult> VerifyEmail([FromQuery] string email, [FromQuery] string token)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(new { message = "Thông tin xác nhận email không đầy đủ hoặc bị thiếu." });
            }

            var normalizedEmail = email.Trim().ToLowerInvariant();
            var user = await _context.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            if (user == null)
            {
                return NotFound(new { message = "Không tìm thấy thông tin tài khoản ứng với email này." });
            }

            var roles = user.UserRoles
                .Where(ur => ur.Role != null)
                .Select(ur => ur.Role!.Name)
                .ToList();
            if (roles.Count == 0) roles.Add("Customer");

            if (user.IsActive)
            {
                return Ok(new
                {
                    success = true,
                    isAlreadyActive = true,
                    message = "Tài khoản của bạn đã được xác nhận và kích hoạt từ trước.",
                    user = new UserDto
                    {
                        Id = user.Id,
                        Username = user.Username,
                        Email = user.Email,
                        FullName = user.FullName,
                        Roles = roles
                    }
                });
            }

            // Kiểm tra token và thời hạn
            if (string.IsNullOrEmpty(user.VerificationCode) ||
                !string.Equals(user.VerificationCode.Trim(), token.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !user.VerificationCodeExpiresAt.HasValue ||
                user.VerificationCodeExpiresAt.Value < DateTime.UtcNow)
            {
                return BadRequest(new { message = "Liên kết xác nhận không hợp lệ hoặc đã hết hạn (24 giờ). Vui lòng yêu cầu gửi lại email mới." });
            }

            // Kích hoạt tài khoản thành công
            user.IsActive = true;
            user.VerificationCode = null;
            user.VerificationCodeExpiresAt = null;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (!EventTicketBooking.Api.Services.TermsPolicy.IsCurrent(user))
            {
                return StatusCode(StatusCodes.Status428PreconditionRequired, new
                {
                    statusCode = StatusCodes.Status428PreconditionRequired,
                    requiresTermsAcceptance = true,
                    termsVersion = EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion,
                    message = "Tài khoản đã được kích hoạt. Vui lòng đăng nhập và chấp nhận Điều khoản dịch vụ hiện hành để tiếp tục."
                });
            }

            var newAccessToken = _tokenService.GenerateAccessToken(user, roles);

            _logger.LogInformation("Người dùng {Email} đã bấm nút xác nhận trong email kích hoạt thành công!", user.Email);

            // Gửi email chúc mừng đăng ký thành công
            _ = Task.Run(async () =>
            {
                try
                {
                    await _emailService.SendWelcomeEmailAsync(user.Email, user.FullName ?? user.Username, "Tài khoản Google");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi gửi email chào mừng cho {Email}", user.Email);
                }
            });

            return Ok(new
            {
                success = true,
                message = "Kích hoạt tài khoản thành công!",
                accessToken = newAccessToken,
                user = new UserDto
                {
                    Id = user.Id,
                    Username = user.Username,
                    Email = user.Email,
                    FullName = user.FullName,
                    Roles = roles
                }
            });
        }

        /// <summary>
        /// API Gửi lại email chứa nút xác nhận kích hoạt tài khoản.
        /// </summary>
        [HttpPost("resend-confirmation-link")]
        public async Task<IActionResult> ResendConfirmationLink([FromBody] ResendOtpDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.Email))
            {
                return BadRequest(new { message = "Email không được để trống." });
            }

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            if (user == null)
            {
                return BadRequest(new { message = "Không tìm thấy tài khoản với email này." });
            }

            if (user.IsActive)
            {
                return Ok(new { success = true, isAlreadyActive = true, message = "Tài khoản này đã được kích hoạt trước đó. Bạn có thể đăng nhập ngay." });
            }

            if (string.IsNullOrEmpty(user.VerificationCode))
                return StatusCode(StatusCodes.Status423Locked, new { message = "Tài khoản đã bị khóa bởi Quản trị viên." });

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            user.VerificationCode = token;
            user.VerificationCodeExpiresAt = DateTime.UtcNow.AddHours(24);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            string scheme = Request.Scheme;
            string host = Request.Host.Value ?? "localhost";
            string confirmationLink = $"{scheme}://{host}/activate.html?email={Uri.EscapeDataString(user.Email)}&token={token}";

            _ = Task.Run(async () =>
            {
                try
                {
                    await _emailService.SendConfirmationEmailAsync(user.Email, user.FullName ?? user.Username, confirmationLink);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi gửi lại liên kết xác nhận cho {Email}", user.Email);
                }
            });

            return Ok(new { success = true, message = "Đã gửi lại email xác nhận mới. Vui lòng kiểm tra hộp thư của bạn." });
        }

        private class GoogleTokenPayload
        {
            [JsonPropertyName("email")]
            public string? Email { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("sub")]
            public string? Sub { get; set; }

            [JsonPropertyName("aud")]
            public string? Aud { get; set; }

            [JsonPropertyName("email_verified")]
            public string? EmailVerified { get; set; }
        }
    }
}
