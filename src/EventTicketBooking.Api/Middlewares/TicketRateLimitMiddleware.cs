using System.Globalization;
using System.Security.Claims;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EventTicketBooking.Api.Middlewares;

public sealed class TicketRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITicketRateLimiter limiter,
        IOptions<TicketRateLimitOptions> options)
    {
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<TicketRateLimitAttribute>()?.Policy;
        if (!options.Value.Enabled || policy == null)
        {
            await next(context);
            return;
        }
        var account = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("id") ?? context.User.FindFirstValue("sub");
        if (context.User.Identity?.IsAuthenticated != true || !Guid.TryParse(account, out var accountId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { code = "UNAUTHORIZED", message = "Vui lòng đăng nhập." });
            return;
        }
        var address = context.Connection.RemoteIpAddress;
        var ip = address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4().ToString() : address?.ToString() ?? "unknown";
        int wait;
        try { wait = await limiter.CheckAsync(policy, accountId.ToString("D"), ip); }
        catch (Exception ex) when (ex is RedisException or InvalidOperationException)
        {
            context.Response.StatusCode = 503;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(new { code = "RATE_LIMIT_UNAVAILABLE", message = "Hệ thống đang bận. Vui lòng thử lại sau.", retryAfterSeconds = 5 });
            return;
        }
        if (wait > 0)
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { code = "RATE_LIMITED", message = "Bạn thao tác quá nhanh. Vui lòng thử lại sau.", retryAfterSeconds = wait });
            return;
        }
        await next(context);
    }
}
