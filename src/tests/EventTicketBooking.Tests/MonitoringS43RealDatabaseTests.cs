using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Monitoring;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Monitoring;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class MonitoringS43RealDatabaseTests
    {
        private AppDbContext CreateInMemoryDbContext()
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;

            return new AppDbContext(options);
        }

        #region Middleware Tests

        [Fact]
        public async Task Middleware_CapturesRequest_WithValidDurationAndNormalizedRoute()
        {
            var channel = new TelemetryChannel();
            var middleware = new RequestTelemetryMiddleware(
                next: (innerHttpContext) =>
                {
                    innerHttpContext.Response.StatusCode = 200;
                    return Task.CompletedTask;
                },
                channel: channel);

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/api/v1/orders/12345678-1234-1234-1234-123456789abc";

            await middleware.InvokeAsync(context);

            Assert.True(channel.RequestReader.TryRead(out var telemetry));
            Assert.NotNull(telemetry);
            Assert.Equal("GET", telemetry.Method);
            Assert.Contains("/api/v1/orders/{id}", telemetry.Route);
            Assert.Equal(200, telemetry.StatusCode);
            Assert.True(telemetry.DurationMs >= 0);
        }

        [Fact]
        public async Task Middleware_Captures500_WhenExceptionThrown_AndRethrows()
        {
            var channel = new TelemetryChannel();
            var middleware = new RequestTelemetryMiddleware(
                next: (innerHttpContext) => throw new InvalidOperationException("Simulated business error"),
                channel: channel);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/v1/orders";

            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

            Assert.True(channel.RequestReader.TryRead(out var telemetry));
            Assert.NotNull(telemetry);
            Assert.Equal("POST", telemetry.Method);
            Assert.Equal(500, telemetry.StatusCode);
            Assert.True(telemetry.DurationMs >= 0);
        }

        [Fact]
        public async Task Middleware_NeverStoresQueryStringOrAuthorizationOrSecret()
        {
            var channel = new TelemetryChannel();
            var middleware = new RequestTelemetryMiddleware(
                next: (ctx) => Task.CompletedTask,
                channel: channel);

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/api/v1/orders/123";
            context.Request.QueryString = new QueryString("?token=S56SECRET_TEST&apiKey=VERY_SECRET_KEY");
            context.Request.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.SECRET_TOKEN";
            context.Request.Headers.Cookie = "session_id=SECRET_SESSION; auth=SECRET_COOKIE";

            await middleware.InvokeAsync(context);

            Assert.True(channel.RequestReader.TryRead(out var telemetry));
            Assert.NotNull(telemetry);
            Assert.DoesNotContain("S56SECRET_TEST", telemetry.Route);
            Assert.DoesNotContain("VERY_SECRET_KEY", telemetry.Route);
            Assert.DoesNotContain("SECRET_TOKEN", telemetry.Route);
            Assert.DoesNotContain("SECRET_SESSION", telemetry.Route);
            Assert.DoesNotContain("?", telemetry.Route);
        }

        [Fact]
        public async Task Middleware_ExcludesMonitoringAndStaticEndpoints()
        {
            var channel = new TelemetryChannel();
            var middleware = new RequestTelemetryMiddleware(
                next: (ctx) => Task.CompletedTask,
                channel: channel);

            var excludedPaths = new[]
            {
                "/api/v1/monitoring/summary",
                "/api/admin/monitoring/alerts",
                "/monitoring.html",
                "/admin-monitoring.html",
                "/metrics",
                "/health",
                "/styles.css",
                "/app.js",
                "/favicon.ico"
            };

            foreach (var path in excludedPaths)
            {
                var context = new DefaultHttpContext();
                context.Request.Method = "GET";
                context.Request.Path = path;

                await middleware.InvokeAsync(context);
            }

            // Must have 0 items captured since all were excluded
            Assert.False(channel.RequestReader.TryRead(out _));
        }

        #endregion

        #region Aggregation & Real Metrics Tests

        [Fact]
        public async Task MonitoringService_Summary_CalculatesCount_ErrorRate_P95_Correctly()
        {
            using var db = CreateInMemoryDbContext();
            var channel = new TelemetryChannel();
            var service = new MonitoringService(db, channel);

            var now = DateTimeOffset.UtcNow;

            // Seed 100 requests with controlled durations and status codes
            // 90 status 200 (durations 10ms to 99ms)
            // 10 status 500 (durations 100ms to 109ms) -> Error rate = 10%
            var requests = new List<RequestTelemetry>();
            for (int i = 1; i <= 90; i++)
            {
                requests.Add(new RequestTelemetry
                {
                    Id = Guid.NewGuid(),
                    OccurredAtUtc = now.AddMinutes(-5),
                    Method = "GET",
                    Route = "GET /api/v1/events",
                    StatusCode = 200,
                    DurationMs = i * 10.0 // 10ms to 900ms
                });
            }
            for (int i = 91; i <= 100; i++)
            {
                requests.Add(new RequestTelemetry
                {
                    Id = Guid.NewGuid(),
                    OccurredAtUtc = now.AddMinutes(-2),
                    Method = "POST",
                    Route = "POST /api/v1/orders",
                    StatusCode = 500,
                    DurationMs = i * 10.0 // 910ms to 1000ms
                });
            }

            db.RequestTelemetries.AddRange(requests);
            await db.SaveChangesAsync();

            var summary = await service.GetSummaryAsync("15m");

            Assert.Equal(100, summary.RequestCount);
            Assert.Equal(10, summary.ErrorCount);
            Assert.Equal(10.0, summary.ErrorRatePercent);
            // 95th percentile of 10..1000 with step 10 is 950ms
            Assert.Equal(950.0, summary.P95Milliseconds);
        }

        [Fact]
        public async Task MonitoringService_Summary_FiltersByTimeWindow()
        {
            using var db = CreateInMemoryDbContext();
            var channel = new TelemetryChannel();
            var service = new MonitoringService(db, channel);

            var now = DateTimeOffset.UtcNow;

            // 5 requests within last 3 minutes (in 5m window)
            // 5 requests 10 minutes ago (in 15m window, outside 5m)
            // 5 requests 40 minutes ago (in 1h window, outside 15m)
            // 5 requests 2 hours ago (outside 1h)
            for (int i = 0; i < 5; i++)
            {
                db.RequestTelemetries.Add(new RequestTelemetry { OccurredAtUtc = now.AddMinutes(-2), Route = "GET /recent", StatusCode = 200 });
                db.RequestTelemetries.Add(new RequestTelemetry { OccurredAtUtc = now.AddMinutes(-10), Route = "GET /mid", StatusCode = 200 });
                db.RequestTelemetries.Add(new RequestTelemetry { OccurredAtUtc = now.AddMinutes(-40), Route = "GET /older", StatusCode = 200 });
                db.RequestTelemetries.Add(new RequestTelemetry { OccurredAtUtc = now.AddHours(-2), Route = "GET /ancient", StatusCode = 200 });
            }
            await db.SaveChangesAsync();

            var sum5m = await service.GetSummaryAsync("5m");
            var sum15m = await service.GetSummaryAsync("15m");
            var sum1h = await service.GetSummaryAsync("1h");

            Assert.Equal(5, sum5m.RequestCount);
            Assert.Equal(10, sum15m.RequestCount);
            Assert.Equal(15, sum1h.RequestCount);
        }

        #endregion

        #region Business Event Emission Tests

        [Fact]
        public void SeatHoldConflict_EmitsMonitoringEvent_SeatConflict()
        {
            var channel = new TelemetryChannel();
            var dbMetrics = new DatabaseAppMetrics(channel);

            dbMetrics.RecordSeatHoldConflict("db_active");

            Assert.True(channel.EventReader.TryRead(out var ev));
            Assert.NotNull(ev);
            Assert.Equal("SeatConflict", ev.Type);
            Assert.Equal("db_active", ev.Category);
        }

        [Fact]
        public void WebhookRejected_EmitsMonitoringEvent_WebhookRejected()
        {
            var channel = new TelemetryChannel();
            var dbMetrics = new DatabaseAppMetrics(channel);

            dbMetrics.RecordWebhookRejected("payos", "invalid_signature");

            Assert.True(channel.EventReader.TryRead(out var ev));
            Assert.NotNull(ev);
            Assert.Equal("WebhookRejected", ev.Type);
            Assert.Equal("payos", ev.Category);
            Assert.Equal("invalid_signature", ev.ReferenceId);
        }

        #endregion

        #region Alert Evaluator Tests

        [Fact]
        public async Task AlertEvaluator_CreatesAlert_WhenThresholdExceeded()
        {
            using var db = CreateInMemoryDbContext();
            var channel = new TelemetryChannel();
            var now = DateTimeOffset.UtcNow;

            // Seed requests with high error rate (10 errors out of 10 requests = 100% > 5%)
            for (int i = 0; i < 10; i++)
            {
                db.RequestTelemetries.Add(new RequestTelemetry
                {
                    OccurredAtUtc = now.AddMinutes(-5),
                    Route = "POST /api/test",
                    StatusCode = 500,
                    DurationMs = 100
                });
            }
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton<ITelemetryChannel>(channel);
            services.AddScoped<IMonitoringService, MonitoringService>();
            var sp = services.BuildServiceProvider();

            var options = Options.Create(new MonitoringOptions
            {
                Thresholds = new MonitoringThresholds { ErrorRatePercent = 5.0 }
            });

            var evaluator = new MonitoringAlertEvaluationHostedService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                options,
                NullLogger<MonitoringAlertEvaluationHostedService>.Instance);

            await evaluator.EvaluateAlertsAsync(CancellationToken.None);

            var alert = await db.MonitoringAlerts.FirstOrDefaultAsync(a => a.Metric == "ErrorRatePercent");
            Assert.NotNull(alert);
            Assert.True(alert.IsActive);
            Assert.Equal(100.0, alert.ObservedValue);
            Assert.Equal(5.0, alert.Threshold);
            Assert.Null(alert.ResolvedAtUtc);
        }

        [Fact]
        public async Task AlertEvaluator_DoesNotDuplicateAlert_OnSubsequentEvaluation()
        {
            using var db = CreateInMemoryDbContext();
            var channel = new TelemetryChannel();
            var now = DateTimeOffset.UtcNow;

            for (int i = 0; i < 10; i++)
            {
                db.RequestTelemetries.Add(new RequestTelemetry
                {
                    OccurredAtUtc = now.AddMinutes(-5),
                    Route = "POST /api/test",
                    StatusCode = 500,
                    DurationMs = 100
                });
            }
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton<ITelemetryChannel>(channel);
            services.AddScoped<IMonitoringService, MonitoringService>();
            var sp = services.BuildServiceProvider();

            var options = Options.Create(new MonitoringOptions
            {
                Thresholds = new MonitoringThresholds { ErrorRatePercent = 5.0 }
            });

            var evaluator = new MonitoringAlertEvaluationHostedService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                options,
                NullLogger<MonitoringAlertEvaluationHostedService>.Instance);

            // First run
            await evaluator.EvaluateAlertsAsync(CancellationToken.None);
            Assert.Equal(1, await db.MonitoringAlerts.CountAsync(a => a.Metric == "ErrorRatePercent"));

            // Second run (still violating)
            await evaluator.EvaluateAlertsAsync(CancellationToken.None);
            // Must NOT create duplicate row
            Assert.Equal(1, await db.MonitoringAlerts.CountAsync(a => a.Metric == "ErrorRatePercent"));
        }

        [Fact]
        public async Task AlertEvaluator_ResolvesAlert_WhenMetricReturnsToNormal()
        {
            using var db = CreateInMemoryDbContext();
            var channel = new TelemetryChannel();
            var now = DateTimeOffset.UtcNow;

            // Existing active alert
            var alert = new MonitoringAlert
            {
                Id = Guid.NewGuid(),
                Metric = "ErrorRatePercent",
                Severity = "Critical",
                Threshold = 5.0,
                ObservedValue = 50.0,
                FirstTriggeredAtUtc = now.AddMinutes(-30),
                LastTriggeredAtUtc = now.AddMinutes(-10),
                IsActive = true
            };
            db.MonitoringAlerts.Add(alert);

            // Now error rate is 0% (20 healthy requests)
            for (int i = 0; i < 20; i++)
            {
                db.RequestTelemetries.Add(new RequestTelemetry
                {
                    OccurredAtUtc = now.AddMinutes(-2),
                    Route = "GET /api/healthy",
                    StatusCode = 200,
                    DurationMs = 50
                });
            }
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton<ITelemetryChannel>(channel);
            services.AddScoped<IMonitoringService, MonitoringService>();
            var sp = services.BuildServiceProvider();

            var options = Options.Create(new MonitoringOptions
            {
                Thresholds = new MonitoringThresholds { ErrorRatePercent = 5.0 }
            });

            var evaluator = new MonitoringAlertEvaluationHostedService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                options,
                NullLogger<MonitoringAlertEvaluationHostedService>.Instance);

            await evaluator.EvaluateAlertsAsync(CancellationToken.None);

            var reloaded = await db.MonitoringAlerts.FirstAsync(a => a.Id == alert.Id);
            Assert.False(reloaded.IsActive);
            Assert.NotNull(reloaded.ResolvedAtUtc);
        }

        #endregion

        #region Multi-Instance & Shared Database Simulation Tests

        [Fact]
        public async Task SharedDatabase_MultiInstanceSimulation_WriterContextA_ReaderContextB_SeesSameData()
        {
            // Simulate 2 instances pointing to the exact same database
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;

            var orderId = Guid.NewGuid();

            // Instance A (Writer) records request & event
            await using (var contextA = new AppDbContext(options))
            {
                contextA.RequestTelemetries.Add(new RequestTelemetry
                {
                    Id = Guid.NewGuid(),
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Method = "GET",
                    Route = "GET /api/v1/events",
                    StatusCode = 200,
                    DurationMs = 45.2,
                    InstanceId = "instance-A"
                });

                contextA.MonitoringEvents.Add(new MonitoringEvent
                {
                    Id = Guid.NewGuid(),
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Type = "SeatConflict",
                    Category = "db_active"
                });

                await contextA.SaveChangesAsync();
            }

            // Instance B (Reader / Monitoring Dashboard of another developer) queries metrics
            await using (var contextB = new AppDbContext(options))
            {
                var serviceB = new MonitoringService(contextB, new TelemetryChannel());
                var summaryB = await serviceB.GetSummaryAsync("15m");

                Assert.Equal(1, summaryB.RequestCount);
                Assert.Equal(0, summaryB.ErrorCount);
                Assert.Equal(45.2, summaryB.P95Milliseconds);
                Assert.Equal(1, summaryB.SeatConflictCount);
            }
        }

        [Fact]
        public async Task RetentionCleanup_DeletesOldTelemetry_PreservesActiveAlerts()
        {
            using var db = CreateInMemoryDbContext();
            var channel = new TelemetryChannel();
            var now = DateTimeOffset.UtcNow;

            // 1 telemetry older than 72 hours
            db.RequestTelemetries.Add(new RequestTelemetry { OccurredAtUtc = now.AddHours(-100), Route = "GET /old", StatusCode = 200 });
            // 1 recent telemetry
            db.RequestTelemetries.Add(new RequestTelemetry { OccurredAtUtc = now.AddHours(-1), Route = "GET /new", StatusCode = 200 });

            // Active alert (must never be deleted)
            var activeAlert = new MonitoringAlert { Metric = "TestAlert", IsActive = true, FirstTriggeredAtUtc = now.AddHours(-100) };
            db.MonitoringAlerts.Add(activeAlert);
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton<ITelemetryChannel>(channel);
            services.AddScoped<IMonitoringService, MonitoringService>();
            var sp = services.BuildServiceProvider();

            var evaluator = new MonitoringAlertEvaluationHostedService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new MonitoringOptions { RetentionHours = 72 }),
                NullLogger<MonitoringAlertEvaluationHostedService>.Instance);

            await evaluator.RunRetentionCleanupAsync(CancellationToken.None);

            Assert.Equal(1, await db.RequestTelemetries.CountAsync());
            Assert.Equal("GET /new", (await db.RequestTelemetries.FirstAsync()).Route);
            Assert.Equal(1, await db.MonitoringAlerts.CountAsync(a => a.IsActive));
        }

        #endregion

        #region Authorization & Controller Tests

        [Fact]
        public void MonitoringController_HasRequireRoleAdminAttribute()
        {
            var attrs = typeof(MonitoringController).GetCustomAttributes(typeof(RequireRoleAttribute), true);
            Assert.NotEmpty(attrs);
            var requireRole = (RequireRoleAttribute)attrs[0];
            Assert.Contains("Admin", requireRole.Roles);
        }

        [Fact]
        public async Task MonitoringController_AdminRole_Returns200WithSummary()
        {
            var mockService = new Mock<IMonitoringService>();
            mockService.Setup(s => s.GetSummaryAsync("15m", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MonitoringSummaryDto
                {
                    RequestCount = 50,
                    ErrorCount = 2,
                    ErrorRatePercent = 4.0,
                    P95Milliseconds = 120.5
                });

            var controller = new MonitoringController(mockService.Object);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            var result = await controller.GetSummary("15m", CancellationToken.None);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<MonitoringSummaryDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Equal(50, response.Data!.RequestCount);
            Assert.Equal(4.0, response.Data.ErrorRatePercent);
        }

        #endregion
    }
}
