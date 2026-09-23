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
    public class TokenService : ITokenService
    {
        private readonly IConfiguration _configuration;

        public TokenService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string GenerateToken(User user)
        {
            var jwtKey = _configuration["Jwt:Key"] 
                         ?? Environment.GetEnvironmentVariable("JWT_KEY")
                         ?? "EventTicketBooking_Default_Super_Secret_Key_For_Jwt_Security_2026_!";
            var issuer = _configuration["Jwt:Issuer"] 
                         ?? Environment.GetEnvironmentVariable("JWT_ISSUER") 
                         ?? "EventTicketBooking.Api";
            var audience = _configuration["Jwt:Audience"] 
                           ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE") 
                           ?? "EventTicketBooking.Client";

            var expiryHoursStr = _configuration["Jwt:ExpiresInHours"] 
                                 ?? Environment.GetEnvironmentVariable("JWT_EXPIRES_IN_HOURS");
            int expiryHours = int.TryParse(expiryHoursStr, out int h) ? h : 24;

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var roleName = user.Role?.Name ?? string.Empty;

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Name, user.Username),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, roleName),
                new Claim("role", roleName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddHours(expiryHours),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public int GetExpiresInSeconds()
        {
            var expiryHoursStr = _configuration["Jwt:ExpiresInHours"] 
                                 ?? Environment.GetEnvironmentVariable("JWT_EXPIRES_IN_HOURS");
            int expiryHours = int.TryParse(expiryHoursStr, out int h) ? h : 24;
            return expiryHours * 3600;
        }
    }
}
