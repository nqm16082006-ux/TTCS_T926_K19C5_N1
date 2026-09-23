using System;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class AuthService : IAuthService
    {
        private readonly AppDbContext _dbContext;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IConnectionMultiplexer _redis;
        private readonly ITokenService _tokenService;
        private readonly ILogger<AuthService> _logger;

        public AuthService(
            AppDbContext dbContext,
            IPasswordHasher passwordHasher,
            IConnectionMultiplexer redis,
            ITokenService tokenService,
            ILogger<AuthService> logger)
        {
            _dbContext = dbContext;
            _passwordHasher = passwordHasher;
            _redis = redis;
            _tokenService = tokenService;
            _logger = logger;
        }

        // Sinh mã xác thực email 6 số
        public async Task<string> GenerateAndSaveVerificationCodeAsync(User user)
        {
            var code = new Random().Next(100000, 999999).ToString();

            user.VerificationCode = code;
            user.VerificationCodeExpiresAt =
                DateTime.UtcNow.AddMinutes(15);

            await _dbContext.SaveChangesAsync();

            return code;
        }

        // Kiểm tra mã xác thực email
        public async Task<bool> VerifyEmailCodeAsync(
            string email,
            string code)
        {
            var user = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Email == email);

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

        // Đăng nhập
        public async Task<AuthResult> LoginAsync(
            LoginRequestDto request)
        {
            var normalizedEmail =
                request.Email.Trim().ToLowerInvariant();

            var db = _redis.GetDatabase();

            var lockoutKey =
                $"login:lockout:{normalizedEmail}";

            var attemptsKey =
                $"login:attempts:{normalizedEmail}";

            // Kiểm tra tài khoản đang bị khóa
            bool isLocked =
                await db.KeyExistsAsync(lockoutKey);

            if (isLocked)
            {
                var ttl =
                    await db.KeyTimeToLiveAsync(lockoutKey);

                var minutesRemaining =
                    ttl.HasValue && ttl.Value.TotalMinutes > 0
                        ? Math.Ceiling(ttl.Value.TotalMinutes)
                        : 15;

                _logger.LogWarning(
                    "Tài khoản {Email} đang bị khóa. Còn {Minutes} phút.",
                    normalizedEmail,
                    minutesRemaining);

                return AuthResult.Locked(
                    $"Tài khoản của bạn đã bị khóa tạm thời trong 15 phút. " +
                    $"Vui lòng thử lại sau khoảng {minutesRemaining} phút.");
            }

            // Tìm user
            var user = await _dbContext.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(
                    u => u.Email.ToLower() == normalizedEmail);

            // Kiểm tra tài khoản và mật khẩu
            if (user == null ||
                !_passwordHasher.Verify(
                    request.Password,
                    user.PasswordHash))
            {
                long attempts =
                    await db.StringIncrementAsync(attemptsKey);

                if (attempts == 1)
                {
                    await db.KeyExpireAsync(
                        attemptsKey,
                        TimeSpan.FromMinutes(15));
                }

                _logger.LogInformation(
                    "Đăng nhập thất bại {Email}. Lần sai: {Attempts}",
                    normalizedEmail,
                    attempts);

                // Sai 6 lần → khóa 15 phút
                if (attempts >= 6)
                {
                    await db.StringSetAsync(
                        lockoutKey,
                        "locked",
                        TimeSpan.FromMinutes(15));

                    await db.KeyDeleteAsync(attemptsKey);

                    _logger.LogWarning(
                        "Tài khoản {Email} bị khóa 15 phút.",
                        normalizedEmail);

                    return AuthResult.Locked(
                        "Tài khoản của bạn đã bị khóa tạm thời " +
                        "trong 15 phút do nhập sai quá nhiều lần.");
                }

                return AuthResult.Unauthorized(
                    "Email hoặc mật khẩu không chính xác.");
            }

            // Đăng nhập thành công → xóa bộ đếm
            await db.KeyDeleteAsync(attemptsKey);

            // Lấy Role
            var roles = user.UserRoles
                .Where(ur => ur.Role != null)
                .Select(ur => ur.Role.Name)
                .ToList();

            // Tạo JWT
            var accessToken =
                _tokenService.GenerateAccessToken(
                    user,
                    roles);

            _logger.LogInformation(
                "Người dùng {Email} đăng nhập thành công với vai trò [{Roles}]",
                user.Email,
                string.Join(", ", roles));

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