using System.Net;
using System.Security.Claims;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventTicketBooking.Tests;

public sealed class TicketRateLimitTests
{
    private static TicketRateLimiter Create(MemoryCache cache, TimeProvider clock, int account = 5, int ip = 600) =>
        new(null, cache, Options.Create(new TicketRateLimitOptions
        {
            AllowInMemoryFallback = true,
            Policies = new() { ["test"] = new() { AccountLimit = account, IpLimit = ip, WindowSeconds = 60 } }
        }), clock, NullLogger<TicketRateLimiter>.Instance);

    [Fact]
    public async Task ConcurrentRequests_AllowExactlyFive_AndAccountCannotBypassByChangingIp()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = Create(cache, TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(i => Task.Run(() => limiter.CheckAsync("test", "account", $"ip-{i}"))));
        Assert.Equal(5, results.Count(r => r == 0));
        Assert.All(results.Where(r => r > 0), r => Assert.InRange(r, 1, 60));
        Assert.Equal(0, await limiter.CheckAsync("test", "other-account", "other-ip"));
    }

    [Fact]
    public async Task SharedIp_HasIndependentLimit_AndRejectedRequestsDoNotExtendWait()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new TestClock();
        var limiter = Create(cache, clock, account: 5, ip: 2);
        Assert.Equal(0, await limiter.CheckAsync("test", "a", "shared"));
        Assert.Equal(0, await limiter.CheckAsync("test", "b", "shared"));
        Assert.Equal(60, await limiter.CheckAsync("test", "c", "shared"));
        clock.Advance(20);
        Assert.Equal(40, await limiter.CheckAsync("test", "d", "shared"));
        clock.Advance(40);
        Assert.Equal(0, await limiter.CheckAsync("test", "a", "shared"));
    }

    [Fact]
    public async Task RedisUnavailable_DoesNotSilentlyAllowProductionRequests()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new TicketRateLimiter(null, cache, Options.Create(new TicketRateLimitOptions()),
            TimeProvider.System, NullLogger<TicketRateLimiter>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => limiter.CheckAsync("seat-hold", "a", "ip"));
    }

    [Fact]
    public async Task Middleware_Returns429WithWait_AndIgnoresForgedForwardedHeader()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = Create(cache, TimeProvider.System, ip: 1);
        var calls = 0;
        var middleware = new TicketRateLimitMiddleware(_ => { calls++; return Task.CompletedTask; });
        var settings = Options.Create(new TicketRateLimitOptions());
        async Task<DefaultHttpContext> Send(string account, string forwarded)
        {
            var context = new DefaultHttpContext();
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new TicketRateLimitAttribute("test")), "test"));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, account) }, "Bearer"));
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Headers["X-Forwarded-For"] = forwarded;
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context, limiter, settings);
            return context;
        }
        await Send(Guid.NewGuid().ToString(), "1.2.3.4");
        var blocked = await Send(Guid.NewGuid().ToString(), "5.6.7.8");
        Assert.Equal(1, calls);
        Assert.Equal(429, blocked.Response.StatusCode);
        Assert.InRange(int.Parse(blocked.Response.Headers.RetryAfter!), 1, 60);
        blocked.Response.Body.Position = 0;
        var body = await new StreamReader(blocked.Response.Body).ReadToEndAsync();
        Assert.Contains("RATE_LIMITED", body);
        Assert.Contains("retryAfterSeconds", body);
    }

    [Fact]
    public async Task Middleware_UnprotectedEndpointPasses_AndRedisOutageReturns503()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = Options.Create(new TicketRateLimitOptions());
        var limiter = new TicketRateLimiter(null, cache, settings, TimeProvider.System,
            NullLogger<TicketRateLimiter>.Instance);
        var calls = 0;
        var middleware = new TicketRateLimitMiddleware(_ => { calls++; return Task.CompletedTask; });
        await middleware.InvokeAsync(new DefaultHttpContext(), limiter, settings);
        Assert.Equal(1, calls);
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new TicketRateLimitAttribute("seat-hold")), "hold"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "Bearer"));
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context, limiter, settings);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal(1, calls);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
