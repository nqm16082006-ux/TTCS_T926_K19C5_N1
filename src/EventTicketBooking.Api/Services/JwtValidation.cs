using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EventTicketBooking.Api.Services;

public static class JwtValidation
{
    public static TokenValidationParameters Parameters(IConfiguration configuration) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret(configuration))),
        ValidateIssuer = true,
        ValidIssuer = configuration["JwtSettings:Issuer"] ?? configuration["Jwt:Issuer"] ?? "EventTicketBooking.Api",
        ValidateAudience = true,
        ValidAudience = configuration["JwtSettings:Audience"] ?? configuration["Jwt:Audience"] ?? "EventTicketBooking.Client",
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        ClockSkew = TimeSpan.Zero
    };

    public static string Secret(IConfiguration configuration) =>
        configuration["JwtSettings:SecretKey"] ?? configuration["JWT_SECRET_KEY"] ?? configuration["JWT_KEY"] ??
        configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT signing key must be configured.");
}
