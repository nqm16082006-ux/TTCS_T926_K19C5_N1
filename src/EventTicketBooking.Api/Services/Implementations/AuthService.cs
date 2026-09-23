using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Auth;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class AuthService : IAuthService
    {
        private readonly AppDbContext _context;
        private readonly ITokenService _tokenService;

        public AuthService(AppDbContext context, ITokenService tokenService)
        {
            _context = context;
            _tokenService = tokenService;
        }

        public async Task<ApiResponse<LoginResponseDto>> LoginAsync(LoginRequestDto request)
        {
            var normalizedEmail = request.Email.Trim().ToLower();

            // 1. Tìm user theo Email trong CSDL (kèm thông tin Role)
            var user = await _context.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            // 2. Kiểm tra nếu user không tồn tại (AC3)
            if (user == null)
            {
                return ApiResponse<LoginResponseDto>.FailureResult("Email hoặc mật khẩu không chính xác.");
            }

            // 3. Kiểm tra tính hợp lệ của mật khẩu bằng BCrypt (AC2)
            bool isPasswordValid = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
            if (!isPasswordValid)
            {
                return ApiResponse<LoginResponseDto>.FailureResult("Email hoặc mật khẩu không chính xác.");
            }

            // 4. Mật khẩu đúng -> Tạo JWT Token và trả về thông tin (AC1 & AC4)
            var token = _tokenService.GenerateToken(user);
            var expiresIn = _tokenService.GetExpiresInSeconds();

            var userDto = new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                FullName = user.FullName,
                Role = user.Role?.Name ?? string.Empty
            };

            var loginResponse = new LoginResponseDto
            {
                AccessToken = token,
                TokenType = "Bearer",
                ExpiresIn = expiresIn,
                User = userDto
            };

            return ApiResponse<LoginResponseDto>.SuccessResult(loginResponse, "Đăng nhập thành công.");
        }
    }
}
