using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Metrics;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Prometheus;
using StackExchange.Redis;
using Xunit;
using AspNetServer = Microsoft.AspNetCore.Hosting.Server.IServer;

namespace EventTicketBooking.Tests;

public sealed class MonitoringMetricsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public MonitoringMetricsTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }
    [Fact]
    public async Task PrometheusAppMetrics_RecordsSeatConflictAndWebhookRejections_IntoCustomRegistry()
    {
        var registry = global::Prometheus.Metrics.NewCustomRegistry();
        var metrics = new PrometheusAppMetrics(registry);

        metrics.RecordSeatHoldConflict("redis");
        metrics.RecordSeatHoldConflict("db_active");
        metrics.RecordSeatHoldConflict("db_constraint");

        metrics.RecordWebhookRejected("payos", "missing_signature");
        metrics.RecordWebhookRejected("payos", "invalid_signature");
        metrics.RecordWebhookRejected("payos", "empty_payload");
        metrics.RecordWebhookRejected("payos", "invalid_data");

        using var ms = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(ms);
        var output = Encoding.UTF8.GetString(ms.ToArray());

        Assert.Contains("seat_hold_conflicts_total{reason=\"redis\"} 1", output);
        Assert.Contains("seat_hold_conflicts_total{reason=\"db_active\"} 1", output);
        Assert.Contains("seat_hold_conflicts_total{reason=\"db_constraint\"} 1", output);

        Assert.Contains("payment_webhooks_rejected_total{gateway=\"payos\",reason=\"missing_signature\"} 1", output);
        Assert.Contains("payment_webhooks_rejected_total{gateway=\"payos\",reason=\"invalid_signature\"} 1", output);
        Assert.Contains("payment_webhooks_rejected_total{gateway=\"payos\",reason=\"empty_payload\"} 1", output);
        Assert.Contains("payment_webhooks_rejected_total{gateway=\"payos\",reason=\"invalid_data\"} 1", output);
    }

    [Fact]
    public async Task SeatHoldService_RedisConflict_IncrementsCounterOnceWithRedisReason()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        await using var db = new AppDbContext(options);

        var showtimeId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var showtime = new Showtime
        {
            Id = showtimeId,
            AvailableSeats = 100,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            Event = new Event { Id = Guid.NewGuid(), Title = "Test Event", MaxTicketsPerUser = 5 }
        };
        showtime.ChangeStatus(ShowtimeStatus.OnSale);

        var seat = new Seat { Id = seatId, ShowtimeId = showtimeId, Status = "AVAILABLE", Row = "A", SeatNumber = 1 };
        db.Showtimes.Add(showtime);
        db.Seats.Add(seat);
        await db.SaveChangesAsync();

        var mockMetrics = new Mock<IAppMetrics>();
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var mockDb = new Mock<IDatabase>();
        mockRedis.Setup(r => r.IsConnected).Returns(true);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
        // Lua script returns 0 (conflict)
        mockDb.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create(0));

        var service = new SeatHoldService(
            db,
            NullLogger<SeatHoldService>.Instance,
            new Mock<IHubContext<SeatStatusHub>>().Object,
            mockRedis.Object,
            mockMetrics.Object);

        var result = await service.HoldSeatsAsync(showtimeId, new List<Guid> { seatId }, userId);

        Assert.Equal(HoldSeatsResultStatus.Conflict, result.Status);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("redis"), Times.Once);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("db_active"), Times.Never);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("db_constraint"), Times.Never);
    }

    [Fact]
    public async Task SeatHoldService_DbActiveConflict_IncrementsCounterOnceWithDbActiveReason()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        await using var db = new AppDbContext(options);

        var showtimeId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        var showtime = new Showtime
        {
            Id = showtimeId,
            AvailableSeats = 100,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            Event = new Event { Id = Guid.NewGuid(), Title = "Test Event", MaxTicketsPerUser = 5 }
        };
        showtime.ChangeStatus(ShowtimeStatus.OnSale);

        var seat = new Seat { Id = seatId, ShowtimeId = showtimeId, Status = "AVAILABLE", Row = "A", SeatNumber = 1 };
        var existingHold = new SeatHolds
        {
            Id = Guid.NewGuid(),
            SeatId = seatId,
            UserId = user1,
            Status = "ACTIVE",
            HeldAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
        db.Showtimes.Add(showtime);
        db.Seats.Add(seat);
        db.SeatHold.Add(existingHold);
        await db.SaveChangesAsync();

        var mockMetrics = new Mock<IAppMetrics>();

        var service = new SeatHoldService(
            db,
            NullLogger<SeatHoldService>.Instance,
            new Mock<IHubContext<SeatStatusHub>>().Object,
            redis: null,
            mockMetrics.Object);

        var result = await service.HoldSeatsAsync(showtimeId, new List<Guid> { seatId }, user2);

        Assert.Equal(HoldSeatsResultStatus.Conflict, result.Status);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("db_active"), Times.Once);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("redis"), Times.Never);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("db_constraint"), Times.Never);
    }

    [Fact]
    public async Task SeatHoldService_DbConstraintConflict_IncrementsCounterOnceWithDbConstraintReason()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;

        // Seed data using base context
        var showtimeId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var seedDb = new AppDbContext(options))
        {
            var showtime = new Showtime
            {
                Id = showtimeId,
                AvailableSeats = 100,
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
                Event = new Event { Id = Guid.NewGuid(), Title = "Test Event", MaxTicketsPerUser = 5 }
            };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);

            var seat = new Seat { Id = seatId, ShowtimeId = showtimeId, Status = "AVAILABLE", Row = "A", SeatNumber = 1 };
            seedDb.Showtimes.Add(showtime);
            seedDb.Seats.Add(seat);
            await seedDb.SaveChangesAsync();
        }

        // Use context that throws DbUpdateException on SaveChangesAsync (simulating simultaneous race condition)
        await using var conflictDb = new ConcurrencyConflictDbContext(options);
        var mockMetrics = new Mock<IAppMetrics>();

        var service = new SeatHoldService(
            conflictDb,
            NullLogger<SeatHoldService>.Instance,
            new Mock<IHubContext<SeatStatusHub>>().Object,
            redis: null,
            mockMetrics.Object);

        var result = await service.HoldSeatsAsync(showtimeId, new List<Guid> { seatId }, userId);

        Assert.Equal(HoldSeatsResultStatus.Conflict, result.Status);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("db_constraint"), Times.Once);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("redis"), Times.Never);
        mockMetrics.Verify(m => m.RecordSeatHoldConflict("db_active"), Times.Never);
    }

    private sealed class ConcurrencyConflictDbContext : AppDbContext
    {
        public ConcurrencyConflictDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("Simulated unique constraint conflict IX_seat_holds_SeatId_Active", new Exception());
        }
    }

    [Fact]
    public async Task PaymentsController_WebhookRejections_IncrementCorrespondingMetrics()
    {
        var mockService = new Mock<IPaymentService>();
        var mockGateway = new Mock<IPaymentGateway>();
        mockGateway.Setup(g => g.ProviderName).Returns("PayOS");
        var mockMetrics = new Mock<IAppMetrics>();

        var controller = new PaymentsController(
            mockService.Object,
            mockGateway.Object,
            NullLogger<PaymentsController>.Instance,
            null,
            mockMetrics.Object);

        // 1. Empty payload
        var httpContextEmpty = new DefaultHttpContext();
        httpContextEmpty.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(""));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContextEmpty };
        var resultEmpty = await controller.HandlePaymentWebhook();
        Assert.IsType<BadRequestObjectResult>(resultEmpty);
        mockMetrics.Verify(m => m.RecordWebhookRejected("payos", "empty_payload"), Times.Once);

        // 2. Missing signature
        var httpContextNoSig = new DefaultHttpContext();
        httpContextNoSig.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContextNoSig };
        var resultNoSig = await controller.HandlePaymentWebhook();
        Assert.IsType<UnauthorizedObjectResult>(resultNoSig);
        mockMetrics.Verify(m => m.RecordWebhookRejected("payos", "missing_signature"), Times.Once);

        // 3. Invalid signature
        var httpContextBadSig = new DefaultHttpContext();
        httpContextBadSig.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        httpContextBadSig.Request.Headers["x-signature"] = "invalid_sig";
        mockGateway.Setup(g => g.VerifyWebhookSignature(It.IsAny<string>(), "invalid_sig", It.IsAny<string?>())).Returns(false);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContextBadSig };
        var resultBadSig = await controller.HandlePaymentWebhook();
        Assert.IsType<UnauthorizedObjectResult>(resultBadSig);
        mockMetrics.Verify(m => m.RecordWebhookRejected("payos", "invalid_signature"), Times.Once);

        // 4. Invalid data / process returns false
        var httpContextBadData = new DefaultHttpContext();
        httpContextBadData.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        httpContextBadData.Request.Headers["x-signature"] = "valid_sig";
        mockGateway.Setup(g => g.VerifyWebhookSignature(It.IsAny<string>(), "valid_sig", It.IsAny<string?>())).Returns(true);
        mockService.Setup(s => s.ProcessPaymentWebhookAsync(It.IsAny<string>(), "valid_sig", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContextBadData };
        var resultBadData = await controller.HandlePaymentWebhook();
        Assert.IsType<BadRequestObjectResult>(resultBadData);
        mockMetrics.Verify(m => m.RecordWebhookRejected("payos", "invalid_data"), Times.Once);

        // 5. Exception (500) -> Does NOT increment rejected webhook counter
        var httpContextEx = new DefaultHttpContext();
        httpContextEx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"test\":\"data\"}"));
        httpContextEx.Request.Headers["x-signature"] = "valid_sig";
        mockService.Setup(s => s.ProcessPaymentWebhookAsync(It.IsAny<string>(), "valid_sig", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB explosion"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContextEx };
        var resultEx = await controller.HandlePaymentWebhook();
        var statusResult = Assert.IsType<ObjectResult>(resultEx);
        Assert.Equal(500, statusResult.StatusCode);
        // Ensure no new call was made for this 500
        mockMetrics.Verify(m => m.RecordWebhookRejected("payos", "internal_error"), Times.Never);
    }

    [Fact]
    public void MetricsTokenResolver_ReadsDirectToken_Success()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Metrics:ScrapeToken"] = "  direct-secret-token-123  \r\n"
            })
            .Build();

        var token = MetricsTokenResolver.ResolveScrapeToken(config);
        Assert.Equal("direct-secret-token-123", token);
    }

    [Fact]
    public void MetricsTokenResolver_ReadsFromFile_TrimsTrailingNewline_Success()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "file-token-xyz-789\r\n\n  \r\n");

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Metrics:ScrapeTokenFile"] = tempFile
                })
                .Build();

            var token = MetricsTokenResolver.ResolveScrapeToken(config);
            Assert.Equal("file-token-xyz-789", token);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void MetricsTokenResolver_DirectConfig_PrecedesTokenFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "file-token-should-be-ignored\r\n");

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Metrics:ScrapeToken"] = "direct-token-winner",
                    ["Metrics:ScrapeTokenFile"] = tempFile
                })
                .Build();

            var token = MetricsTokenResolver.ResolveScrapeToken(config);
            Assert.Equal("direct-token-winner", token);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void MetricsTokenResolver_FallbackToDefaultSecretPath_WhenExists()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "default-fallback-token\n");

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var token = MetricsTokenResolver.ResolveScrapeToken(config, defaultSecretPath: tempFile);
            Assert.Equal("default-fallback-token", token);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void MetricsTokenResolver_EmptyOrWhitespaceToken_TreatedAsUnconfigured()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "   \r\n\t  ");

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Metrics:ScrapeTokenFile"] = tempFile
                })
                .Build();

            var token = MetricsTokenResolver.ResolveScrapeToken(config);
            Assert.Null(token);

            var directConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Metrics:ScrapeToken"] = "   "
                })
                .Build();

            var directNullToken = MetricsTokenResolver.ResolveScrapeToken(directConfig);
            Assert.Null(directNullToken);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task HttpPipeline_WhenTokenConfiguredViaFileWithTrailingNewline_SecuresMetricsCorrectly()
    {
        const string expectedScrapeToken = "secret-file-token-with-newline-456";
        var tempTokenFile = Path.GetTempFileName();
        File.WriteAllText(tempTokenFile, $"{expectedScrapeToken}\r\n\n");

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration["Metrics:ScrapeTokenFile"] = tempTokenFile;

            builder.Services.AddRouting();

            await using var app = builder.Build();

            app.UseRouting();

            // S-43: Auth check middleware identical to Program.cs
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
                {
                    var configuredToken = MetricsTokenResolver.ResolveScrapeToken(app.Configuration);

                    if (string.IsNullOrWhiteSpace(configuredToken))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    var authHeader = context.Request.Headers.Authorization.ToString();
                    const string bearerPrefix = "Bearer ";
                    if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    var providedToken = authHeader[bearerPrefix.Length..].Trim();
                    var providedBytes = Encoding.UTF8.GetBytes(providedToken);
                    var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);

                    if (providedBytes.Length != configuredBytes.Length ||
                        !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }

                await next();
            });

            app.UseHttpMetrics();

            app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy" }));
            app.MapGet("/api/test-ok", () => Results.Ok(new { ok = true }));
            app.MapGet("/api/test-error", () => Results.StatusCode(StatusCodes.Status500InternalServerError));
            app.MapMetrics("/metrics");

            await app.StartAsync();

            var address = app.Services.GetRequiredService<AspNetServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            // 1. /api/health works without token
            var healthRes = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);

            // 2. /metrics without token returns 403 Forbidden
            var noTokenRes = await client.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.Forbidden, noTokenRes.StatusCode);

            // 3. /metrics with wrong token returns 403 Forbidden
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
            var badTokenRes = await client.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.Forbidden, badTokenRes.StatusCode);

            // 4. Generate traffic to test-ok and test-error
            client.DefaultRequestHeaders.Authorization = null;
            await client.GetAsync("/api/test-ok");
            await client.GetAsync("/api/test-error");

            // 5. /metrics with valid token (read from file and trimmed) returns 200 OK
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expectedScrapeToken);
            var okTokenRes = await client.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.OK, okTokenRes.StatusCode);
            var metricsText = await okTokenRes.Content.ReadAsStringAsync();

            // Check Prometheus format
            Assert.Contains("# TYPE", metricsText);

            foreach (var line in metricsText.Split('\n').Where(l => l.StartsWith("# TYPE")))
            {
                _output.WriteLine(line.Trim());
            }

            // Check for HTTP metrics generated by UseHttpMetrics
            Assert.True(
                metricsText.Contains("http_requests_received_total") || metricsText.Contains("http_request_duration_seconds"),
                "Expected prometheus-net HTTP metrics to be present in output.");
        }
        finally
        {
            if (File.Exists(tempTokenFile))
            {
                File.Delete(tempTokenFile);
            }
        }
    }

    [Fact]
    public async Task HttpPipeline_WhenNoTokenConfigured_ReturnsForbidden()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // No scrape token configured

        builder.Services.AddRouting();

        await using var app = builder.Build();
        app.UseRouting();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                var configuredToken = MetricsTokenResolver.ResolveScrapeToken(app.Configuration, defaultSecretPath: "/non-existent-secret-file");

                if (string.IsNullOrWhiteSpace(configuredToken))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next();
        });

        app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy" }));
        app.MapMetrics("/metrics");

        await app.StartAsync();

        var address = app.Services.GetRequiredService<AspNetServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        // /api/health still works
        var healthRes = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);

        // /metrics returns 403 even if client passes an authorization header
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "any-arbitrary-token");
        var metricsRes = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Forbidden, metricsRes.StatusCode);
    }
}
