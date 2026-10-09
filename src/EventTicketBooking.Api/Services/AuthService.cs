using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EventTicketBooking.Api.DTOs;
using Microsoft.IdentityModel.Tokens;

namespace EventTicketBooking.Api.Services;

public class AuthService : IAuthService
{
    private readonly IConfiguration _configuration;

    public AuthService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<LoginResponseDto> LoginAsync(LoginRequestDto request)
    {
        var mockUser = new 
        { 
            Id = "USR-001", 
            Email = "user@gmail.com", 
            PasswordHash = "123456", 
            Name = "Nguyen Van A" 
        };

        // AC3: Kiểm tra tài khoản không tồn tại
        if (!string.Equals(request.Email, mockUser.Email, StringComparison.OrdinalIgnoreCase))
        {
            return new LoginResponseDto
            {
                Success = false,
                Message = "Tài khoản không tồn tại"
            };
        }

        // AC2: Kiểm tra mật khẩu sai
        if (request.Password != mockUser.PasswordHash) 
        {
            return new LoginResponseDto
            {
                Success = false,
                Message = "Email hoặc mật khẩu không chính xác"
            };
        }

        // AC1 & AC4: Đăng nhập thành công -> Trả về Token
        var token = GenerateJwtToken(mockUser.Id, mockUser.Email);

        return new LoginResponseDto
        {
            Success = true,
            Message = "Đăng nhập thành công",
            Token = token,
            User = new UserInfoDto
            {
                Id = mockUser.Id,
                Email = mockUser.Email,
                Name = mockUser.Name
            }
        };
    }

    private string GenerateJwtToken(string userId, string email)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["Secret"] ?? "SuperSecretKeyForEventTicketBookingSystem2026!";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"] ?? "EventTicketApi",
            audience: jwtSettings["Audience"] ?? "EventTicketClient",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}