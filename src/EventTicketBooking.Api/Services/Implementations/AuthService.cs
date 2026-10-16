using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ xác thực người dùng, tích hợp chống brute-force qua Redis (T-05, Story S-02).
    /// </summary>
    public class AuthService : IAuthService
    {
        // Timing attack defense: precomputed dummy Argon2id hash sinh duy nhất 1 lần cho toàn bộ vòng đời ứng dụng
        private static readonly Lazy<string> DummyPasswordHash = new(() =>
            new Argon2PasswordHasher().Hash("EventTicketBooking_Timing_Defense_Dummy_2026_Secure!"));

        private readonly AppDbContext _dbContext;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ITokenService _tokenService;
        private readonly ILogger<AuthService> _logger;
        private readonly IConnectionMultiplexer? _redis;
        private readonly AuthSecurityOptions _securityOptions;

        public AuthService(
            AppDbContext dbContext,
            IPasswordHasher passwordHasher,
            ITokenService tokenService,
            ILogger<AuthService> logger,
            IConnectionMultiplexer? redis = null,
            IOptions<AuthSecurityOptions>? securityOptions = null,
            IConfiguration? configuration = null)
        {
            _dbContext = dbContext;
            _passwordHasher = passwordHasher;
            _tokenService = tokenService;
            _logger = logger;
            _redis = redis;

            if (securityOptions?.Value != null)
            {
                _securityOptions = securityOptions.Value;
            }
            else if (configuration != null)
            {
                _securityOptions = configuration.GetSection(AuthSecurityOptions.SectionName).Get<AuthSecurityOptions>() ?? new AuthSecurityOptions();
            }
            else
            {
                _securityOptions = new AuthSecurityOptions();
            }
        }

        // Sinh mã xác thực email dạng chuỗi (Guid) cho link và hết hạn 24 giờ (T-08)
        public async Task<string> GenerateAndSaveVerificationCodeAsync(User user)
        {
            var code = Guid.NewGuid().ToString("N");

            user.VerificationCode = code;
            user.VerificationCodeExpiresAt = DateTime.UtcNow.AddHours(24);

            await _dbContext.SaveChangesAsync();

            return code;
        }

        // Kiểm tra mã xác thực email
        public async Task<bool> VerifyEmailCodeAsync(string email, string code)
        {
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
            {
                return false;
            }

            if (user.VerificationCode != code ||
                !user.VerificationCodeExpiresAt.HasValue ||
                user.VerificationCodeExpiresAt.Value < DateTime.UtcNow)
            {
                return false;
            }

            user.IsActive = true;
            user.VerificationCode = null;
            user.VerificationCodeExpiresAt = null;

            await _dbContext.SaveChangesAsync();

            return true;
        }

        /// <summary>
        /// Chuẩn hoá địa chỉ IP an toàn theo chuẩn RFC & canonical System.Net.IPAddress (Story S-02 Hardening).
        /// Hỗ trợ IPv4, IPv6, và IPv4-mapped IPv6 (::ffff:127.0.0.1 -> 127.0.0.1).
        /// </summary>
        public static string NormalizeClientIp(string? clientIp)
        {
            if (string.IsNullOrWhiteSpace(clientIp)) return "unknown";
            var trimmed = clientIp.Trim();
            if (IPAddress.TryParse(trimmed, out var ipAddress))
            {
                if (ipAddress.IsIPv4MappedToIPv6)
                {
                    ipAddress = ipAddress.MapToIPv4();
                }
                return ipAddress.ToString().ToLowerInvariant();
            }
            return trimmed.ToLowerInvariant();
        }

        // Đăng nhập kết hợp chống brute-force theo account và client IP (Story S-02)
        public async Task<AuthResult> LoginAsync(LoginRequestDto request, string? clientIp = null)
        {
            var normalizedEmail = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
            var normalizedIp = NormalizeClientIp(clientIp);

            // S-02 CỰC KỲ QUAN TRỌNG: Redis unavailable -> FAIL CLOSED. Trả 503 Service Unavailable.
            if (_redis == null || !_redis.IsConnected)
            {
                _logger.LogError("REDIS UNAVAILABLE: Kết nối Redis không khả dụng khi xử lý đăng nhập cho {Email}. Fail-closed.", normalizedEmail);
                return AuthResult.ServiceUnavailable("Dịch vụ xác thực tạm thời không khả dụng. Vui lòng thử lại sau.");
            }

            IDatabase db;
            try
            {
                db = _redis.GetDatabase();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "REDIS UNAVAILABLE: Lỗi khi lấy kết nối IDatabase từ Redis cho {Email}. Fail-closed.", normalizedEmail);
                return AuthResult.ServiceUnavailable("Dịch vụ xác thực tạm thời không khả dụng. Vui lòng thử lại sau.");
            }

            var accountLockoutKey = $"login:lockout:{normalizedEmail}";
            var accountAttemptsKey = $"login:attempts:{normalizedEmail}";
            var ipLockoutKey = $"login:ip:lockout:{normalizedIp}";
            var ipAttemptsKey = $"login:ip:attempts:{normalizedIp}";

            // 1. Kiểm tra lockout theo IP TRƯỚC KHI verify password
            if (normalizedIp != "unknown")
            {
                try
                {
                    bool isIpLocked = await db.KeyExistsAsync(ipLockoutKey);
                    if (isIpLocked)
                    {
                        var ipTtl = await db.KeyTimeToLiveAsync(ipLockoutKey);
                        var ipRemaining = ipTtl.HasValue && ipTtl.Value.TotalMinutes > 0
                            ? Math.Ceiling(ipTtl.Value.TotalMinutes)
                            : _securityOptions.LockoutMinutes;

                        _logger.LogWarning("IP {Ip} đang bị khóa. Còn {Minutes} phút.", normalizedIp, ipRemaining);
                        return AuthResult.Locked($"Địa chỉ IP của bạn tạm thời bị khóa do có quá nhiều yêu cầu đăng nhập thất bại. Vui lòng thử lại sau khoảng {ipRemaining} phút.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi kiểm tra Redis IP lockout cho IP {Ip}. Fail-closed.", normalizedIp);
                    return AuthResult.ServiceUnavailable("Dịch vụ xác thực tạm thời không khả dụng. Vui lòng thử lại sau.");
                }
            }

            // 2. Kiểm tra lockout theo Account TRƯỚC KHI verify password
            try
            {
                bool isAccountLocked = await db.KeyExistsAsync(accountLockoutKey);
                if (isAccountLocked)
                {
                    var ttl = await db.KeyTimeToLiveAsync(accountLockoutKey);
                    var minutesRemaining = ttl.HasValue && ttl.Value.TotalMinutes > 0
                        ? Math.Ceiling(ttl.Value.TotalMinutes)
                        : _securityOptions.LockoutMinutes;

                    _logger.LogWarning("Tài khoản {Email} đang bị khóa. Còn {Minutes} phút.", normalizedEmail, minutesRemaining);
                    return AuthResult.Locked($"Tài khoản của bạn đã bị khóa tạm thời trong 15 phút. Vui lòng thử lại sau khoảng {minutesRemaining} phút.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi kiểm tra Redis account lockout cho {Email}. Fail-closed.", normalizedEmail);
                return AuthResult.ServiceUnavailable("Dịch vụ xác thực tạm thời không khả dụng. Vui lòng thử lại sau.");
            }

            // 3. Tìm user trong CSDL
            var user = await _dbContext.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            // 4. Kiểm tra tài khoản và mật khẩu bằng Argon2id (Timing attack defense)
            bool isPasswordValid = false;
            if (user != null)
            {
                isPasswordValid = _passwordHasher.Verify(request.Password, user.PasswordHash);
            }
            else
            {
                // Unknown email: thực hiện Argon2 verify trên dummy hash để đồng bộ thời gian phản hồi, ngăn chặn enumeration
                _passwordHasher.Verify(request.Password, DummyPasswordHash.Value);
            }

            if (user == null || !isPasswordValid)
            {
                try
                {
                    // Ghi nhận lần thử sai theo IP
                    if (normalizedIp != "unknown")
                    {
                        long ipAttempts = await db.StringIncrementAsync(ipAttemptsKey);
                        if (ipAttempts == 1)
                        {
                            await db.KeyExpireAsync(ipAttemptsKey, TimeSpan.FromMinutes(_securityOptions.LockoutMinutes));
                        }

                        if (ipAttempts >= _securityOptions.IpMaxFailures)
                        {
                            await db.StringSetAsync(ipLockoutKey, "locked", TimeSpan.FromMinutes(_securityOptions.LockoutMinutes));
                            await db.KeyDeleteAsync(ipAttemptsKey);
                            _logger.LogWarning("IP {Ip} đã bị khóa {Minutes} phút do nhập sai {Attempts} lần.",
                                normalizedIp, _securityOptions.LockoutMinutes, ipAttempts);
                        }
                    }

                    // Ghi nhận lần thử sai theo Account
                    long attempts = await db.StringIncrementAsync(accountAttemptsKey);
                    if (attempts == 1)
                    {
                        await db.KeyExpireAsync(accountAttemptsKey, TimeSpan.FromMinutes(_securityOptions.LockoutMinutes));
                    }

                    _logger.LogInformation("Đăng nhập thất bại {Email}. Lần sai: {Attempts}", normalizedEmail, attempts);

                    // Sai lần thứ 5 -> Khóa tài khoản 15 phút và trả về 401
                    if (attempts >= _securityOptions.AccountMaxFailures)
                    {
                        await db.StringSetAsync(accountLockoutKey, "locked", TimeSpan.FromMinutes(_securityOptions.LockoutMinutes));
                        await db.KeyDeleteAsync(accountAttemptsKey);

                        return AuthResult.Unauthorized("Tài khoản của bạn đã bị khóa tạm thời trong 15 phút do nhập sai 5 lần liên tiếp.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi ghi nhận số lần đăng nhập thất bại vào Redis. Fail-closed.");
                    return AuthResult.ServiceUnavailable("Dịch vụ xác thực tạm thời không khả dụng. Vui lòng thử lại sau.");
                }

                // Sai từ lần 1 đến lần 4: trả 401 generic
                return AuthResult.Unauthorized("Email hoặc mật khẩu không chính xác.");
            }

            // 5. Đăng nhập thành công -> Xóa bộ đếm thất bại của CHÍNH account đó (TUYỆT ĐỐI KHÔNG xóa counter của IP)
            try
            {
                await db.KeyDeleteAsync(accountAttemptsKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi xóa bộ đếm đăng nhập thất bại của tài khoản từ Redis.");
            }

            // 6. Kiểm tra trạng thái hoạt động (kích hoạt / đóng tài khoản / ẩn danh)
            if (!user.IsActive)
            {
                if (user.IsAnonymized)
                {
                    return AuthResult.Locked("Tài khoản này đã bị xoá và ẩn danh dữ liệu cá nhân theo yêu cầu bảo vệ quyền riêng tư (Nghị định 13/2023/NĐ-CP). Bạn không thể đăng nhập lại.");
                }
                if (string.IsNullOrEmpty(user.VerificationCode))
                {
                    return AuthResult.Locked("Tài khoản của bạn đã bị khóa bởi Quản trị viên. Vui lòng liên hệ Admin để được hỗ trợ.");
                }
                return AuthResult.Locked("Tài khoản của bạn chưa được kích hoạt. Vui lòng hoàn tất xác thực email để đăng nhập.");
            }

            if (!TermsPolicy.IsCurrent(user))
            {
                if (!request.AcceptCurrentTerms)
                {
                    return AuthResult.TermsAcceptanceRequired(TermsPolicy.CurrentVersion);
                }

                TermsPolicy.AcceptCurrent(user);
                await _dbContext.SaveChangesAsync();
            }

            // 7. SINGLE ROLE - BACKEND: Kiểm tra User phải có đúng 1 vai trò duy nhất. Nếu khác 1 -> FAIL CLOSED.
            var roles = user.UserRoles
                .Where(ur => ur.Role != null)
                .Select(ur => ur.Role.Name)
                .ToList();

            if (roles.Count != 1 || !SystemRoles.IsValid(roles[0]))
            {
                _logger.LogError("Tài khoản {Email} (Id: {UserId}) có {Count} vai trò hoặc vai trò [{Role}] không hợp lệ. Vi phạm nguyên tắc Single Role. Fail-closed.",
                    user.Email, user.Id, roles.Count, roles.FirstOrDefault());
                return AuthResult.Forbidden("Dữ liệu phân quyền của tài khoản không hợp lệ. Vui lòng liên hệ Quản trị viên.");
            }

            // 8. Tạo JWT Access Token
            var accessToken = _tokenService.GenerateAccessToken(user, roles);

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đăng nhập thành công với vai trò duy nhất: [{Role}]",
                user.Email, user.Id, roles[0]);

            var loginResponse = new LoginResponseDto
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
            };

            return AuthResult.Ok(loginResponse);
        }
    }
}
