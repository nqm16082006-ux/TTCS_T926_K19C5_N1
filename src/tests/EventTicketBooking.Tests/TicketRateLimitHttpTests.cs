using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventTicketBooking.Tests;

public sealed class TicketRateLimitHttpTests
{
    [Fact]
    public async Task HttpPipeline_ConcurrentAccountLimit_IndependentPolicies_AndExpiry()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMemoryCache();
        var clock = new TestClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ => null!);
        builder.Services.Configure<TicketRateLimitOptions>(o =>
        {
            o.AllowInMemoryFallback = true;
            o.Policies["seat-hold"].IpLimit = 100;
        });
        builder.Services.AddSingleton<ITicketRateLimiter, TicketRateLimiter>();
        await using var app = builder.Build();
        app.UseRouting();
        // Test identity boundary only: production uses RoleAuthorizationMiddleware.
        app.Use(async (context, next) =>
        {
            if (Guid.TryParse(context.Request.Headers["X-Test-Account"], out var account))
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, account.ToString()) }, "Test"));
            await next();
        });
        app.UseMiddleware<TicketRateLimitMiddleware>();
        app.MapPost("/hold", () => new { success = true }).WithMetadata(new TicketRateLimitAttribute("seat-hold"));
        app.MapPost("/order", () => new { success = true }).WithMetadata(new TicketRateLimitAttribute("order-create"));
        app.MapGet("/public", () => new { success = true });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/hold", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Account", Guid.NewGuid().ToString());
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.PostAsync("/hold", null)));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(15, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        var blocked = responses.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        var body = await blocked.Content.ReadFromJsonAsync<RateLimitBody>();
        Assert.Equal("RATE_LIMITED", body!.Code);
        Assert.Equal(60, body.RetryAfterSeconds);
        Assert.Equal(TimeSpan.FromSeconds(60), blocked.Headers.RetryAfter!.Delta);
        Assert.True(blocked.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/order", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/public")).StatusCode);
        clock.Advance(20);
        var again = await client.PostAsync("/hold", null);
        Assert.Equal(TimeSpan.FromSeconds(40), again.Headers.RetryAfter!.Delta);
        clock.Advance(40);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/hold", null)).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void ActualControllers_AttachExpectedPolicies()
    {
        var hold = typeof(ShowtimeSeatsController).GetMethod("HoldSeats")!;
        var order = typeof(OrdersController).GetMethod("CreateOrderFromHolds")!;
        Assert.Equal("seat-hold", hold.GetCustomAttributes(typeof(TicketRateLimitAttribute), true)
            .Cast<TicketRateLimitAttribute>().Single().Policy);
        Assert.Equal("order-create", order.GetCustomAttributes(typeof(TicketRateLimitAttribute), true)
            .Cast<TicketRateLimitAttribute>().Single().Policy);
    }

    private sealed record RateLimitBody(string Code, int RetryAfterSeconds);
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
