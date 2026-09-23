using System;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ xác thực đăng nhập người dùng (Task TTKN-25):
    /// - Quản lý số lần đăng nhập sai và cờ khóa tạm thời 15 phút độc lập trên Redis.
    /// - Ngưỡng sai: Sai 5 lần cho phép, lần thứ 6 kích hoạt cờ khóa 15 phút.
    /// - NFR: Trả về thông báo lỗi chung không tiết lộ email có tồn tại hay không.
    /// - Tạo JWT Access Token chứa Claims (UserId, Email, Roles) tương thích với TTKN-26.
    /// </summary>
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

        public async Task<AuthResult> LoginAsync(LoginRequestDto request)
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var db = _redis.GetDatabase();

            var lockoutKey = $"login:lockout:{normalizedEmail}";
            var attemptsKey = $"login:attempts:{normalizedEmail}";

            // a. Kiểm tra key login:lockout:{email} trên Redis
            bool isLocked = await db.KeyExistsAsync(lockoutKey);
            if (isLocked)
            {
                var ttl = await db.KeyTimeToLiveAsync(lockoutKey);
                var minutesRemaining = ttl.HasValue && ttl.Value.TotalMinutes > 0 
                    ? Math.Ceiling(ttl.Value.TotalMinutes) 
                    : 15;

                _logger.LogWarning("Tài khoản {Email} đang bị khóa tạm thời trên Redis. Thời gian còn lại: {Minutes} phút.", normalizedEmail, minutesRemaining);
                return AuthResult.Locked($"Tài khoản của bạn đã bị khóa tạm thời trong 15 phút do nhập sai quá nhiều lần. Vui lòng thử lại sau khoảng {minutesRemaining} phút.");
            }

            // b. Tìm user trong CSDL kèm danh sách Roles và kiểm tra mật khẩu qua Argon2id
            var user = await _dbContext.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            // c. Nếu User không tồn tại HOẶC mật khẩu không khớp
            if (user == null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            {
                // Tăng bộ đếm lỗi: INCR login:attempts:{email}
                long attempts = await db.StringIncrementAsync(attemptsKey);

                // Nếu là lần sai đầu tiên, đặt TTL 15 phút (900 giây)
                if (attempts == 1)
                {
                    await db.KeyExpireAsync(attemptsKey, TimeSpan.FromMinutes(15));
                }

                _logger.LogInformation("Đăng nhập thất bại cho email {Email}. Lần sai thứ: {Attempts}", normalizedEmail, attempts);

                // Nếu số lần sai >= 6: Đặt cờ khóa login:lockout:{email} thời hạn 15 phút và xóa key đếm
                if (attempts >= 6)
                {
                    await db.StringSetAsync(lockoutKey, "locked", TimeSpan.FromMinutes(15));
                    await db.KeyDeleteAsync(attemptsKey);

                    _logger.LogWarning("Tài khoản {Email} đã đạt 6 lần sai liên tiếp và bị khóa 15 phút trên Redis.", normalizedEmail);

                    return AuthResult.Locked("Tài khoản của bạn đã bị khóa tạm thời trong 15 phút do nhập sai 6 lần liên tiếp. Vui lòng thử lại sau.");
                }

                // NFR: Trả về HTTP 401 với thông báo chung bảo mật (không tiết lộ email có tồn tại hay không)
                return AuthResult.Unauthorized("Email hoặc mật khẩu không chính xác.");
            }

            // d. Đăng nhập đúng thông tin:
            // Xóa key đếm login:attempts:{email} trên Redis
            await db.KeyDeleteAsync(attemptsKey);

            // Lấy danh sách Roles
            var roles = user.UserRoles
                .Where(ur => ur.Role != null)
                .Select(ur => ur.Role.Name)
                .ToList();

            // Sinh JWT Access Token
            var accessToken = _tokenService.GenerateAccessToken(user, roles);

            _logger.LogInformation("Người dùng {Email} (Id: {UserId}) đăng nhập thành công với vai trò: [{Roles}]", 
                user.Email, user.Id, string.Join(", ", roles));

            var loginResponse = new LoginResponseDto
            {
                AccessToken = accessToken,
                TokenType = "Bearer",
                ExpiresIn = 86400, // 24 giờ
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
