using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai tạo JWT Access Token theo chuẩn JWT Security (Task TTKN-25 & TTKN-26).
    /// </summary>
    public class TokenService : ITokenService
    {
        private readonly IConfiguration _configuration;

        public TokenService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string GenerateAccessToken(User user, IEnumerable<string> roles)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            var roleList = roles?.ToList();
            if (roleList == null || roleList.Count != 1)
            {
                throw new InvalidOperationException($"Single-role invariant violation: User must have exactly 1 role, but received {(roleList == null ? 0 : roleList.Count)}.");
            }

            var singleRole = roleList[0];
            if (!SystemRoles.IsValid(singleRole))
            {
                throw new InvalidOperationException($"Invalid system role '{singleRole}'. Allowed roles are: {string.Join(", ", SystemRoles.All)}.");
            }

            var secretKey = EventTicketBooking.Api.Services.JwtValidation.Secret(_configuration);

            var issuer = _configuration["JwtSettings:Issuer"]
                         ?? _configuration["Jwt:Issuer"]
                         ?? "EventTicketBooking.Api";

            var audience = _configuration["JwtSettings:Audience"]
                           ?? _configuration["Jwt:Audience"]
                           ?? "EventTicketBooking.Client";

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("id", user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.FullName ?? user.Username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, singleRole),
                new Claim("role", singleRole)
            };

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(24),
                Issuer = issuer,
                Audience = audience,
                SigningCredentials = credentials
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}
