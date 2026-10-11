using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventTicketBooking.Api.Hubs;

public class SystemAlertHub : Hub
{
    public const string AdminGroupName = "Admins";
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;

    public SystemAlertHub(IConfiguration configuration, IServiceScopeFactory scopeFactory)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var token = httpContext?.Request.Query["access_token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            var authHeader = httpContext?.Request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader[bearerPrefix.Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            Context.Abort();
            return;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
            {
                Context.Abort();
                return;
            }

            var validationParameters = JwtValidation.Parameters(_configuration);
            var principal = handler.ValidateToken(token, validationParameters, out _);

            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? principal.FindFirst("id")?.Value
                              ?? principal.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            {
                Context.Abort();
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
            if (user == null)
            {
                Context.Abort();
                return;
            }

            var isAdmin = await db.UserRoles.AsNoTracking()
                .AnyAsync(ur => ur.UserId == userId && ur.Role.Name.ToLower() == "admin");

            if (!isAdmin)
            {
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroupName);
            await base.OnConnectedAsync();
        }
        catch
        {
            Context.Abort();
        }
    }
}
