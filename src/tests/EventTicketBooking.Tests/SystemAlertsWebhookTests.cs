using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests;

public sealed class SystemAlertsWebhookTests
{
    private const string ExpectedSecret = "test-webhook-secret-token-xyz-123";

    private IConfiguration CreateConfig(string? secret = ExpectedSecret)
    {
        var dict = new Dictionary<string, string?>();
        if (secret != null)
        {
            dict["SystemAlerts:WebhookToken"] = secret;
        }
        dict["JwtSettings:SecretKey"] = "SuperSecretKeyForTestingJwtTokens1234567890!";
        dict["JwtSettings:Issuer"] = "EventTicketBooking.Api";
        dict["JwtSettings:Audience"] = "EventTicketBooking.Client";

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Webhook_MissingAuthorizationHeader_ReturnsForbidden()
    {
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();

        var controller = new SystemAlertsWebhookController(config, mockHub.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var payload = new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem> { new() { Status = "firing" } }
        };

        var result = await controller.HandleAlertmanagerWebhook(payload, CancellationToken.None);
        var statusResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task Webhook_InvalidToken_ReturnsForbidden()
    {
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer wrong-secret-token";

        var controller = new SystemAlertsWebhookController(config, mockHub.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var payload = new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem> { new() { Status = "firing" } }
        };

        var result = await controller.HandleAlertmanagerWebhook(payload, CancellationToken.None);
        var statusResult = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task Webhook_EmptyOrMalformedPayload_ReturnsBadRequest()
    {
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = $"Bearer {ExpectedSecret}";

        var controller = new SystemAlertsWebhookController(config, mockHub.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // Null payload
        var resultNull = await controller.HandleAlertmanagerWebhook(null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(resultNull);

        // Empty alerts list
        var emptyPayload = new AlertmanagerWebhookPayload { Alerts = new List<AlertmanagerAlertItem>() };
        var resultEmpty = await controller.HandleAlertmanagerWebhook(emptyPayload, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(resultEmpty);
    }

    [Fact]
    public async Task Webhook_ValidToken_ProcessesMultipleAlertsAndDispatchesToAdmins()
    {
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAdminGroup = new Mock<IClientProxy>();

        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(SystemAlertHub.AdminGroupName)).Returns(mockAdminGroup.Object);

        var dispatchedDtos = new List<SystemAlertNotificationDto>();
        mockAdminGroup.Setup(g => g.SendCoreAsync("ReceiveSystemAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((method, args, ct) =>
            {
                if (args.Length > 0 && args[0] is SystemAlertNotificationDto dto)
                {
                    dispatchedDtos.Add(dto);
                }
            })
            .Returns(Task.CompletedTask);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = $"Bearer {ExpectedSecret}";

        var controller = new SystemAlertsWebhookController(config, mockHub.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var payload = new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-1",
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "Http5xxErrorRateWarning", ["severity"] = "warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "Tỉ lệ lỗi 5xx vượt quá 2%" }
                },
                new()
                {
                    Fingerprint = "fp-2",
                    Status = "critical",
                    Labels = new Dictionary<string, string> { ["alertname"] = "HttpLatencyP95Critical", ["severity"] = "critical" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "Độ trễ p95 vượt quá 2.5s" }
                },
                new()
                {
                    Fingerprint = "fp-1",
                    Status = "resolved",
                    Labels = new Dictionary<string, string> { ["alertname"] = "Http5xxErrorRateWarning", ["severity"] = "warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "Lỗi 5xx đã trở lại bình thường" }
                }
            }
        };

        var result = await controller.HandleAlertmanagerWebhook(payload, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);

        Assert.Equal(3, dispatchedDtos.Count);

        Assert.Equal("fp-1", dispatchedDtos[0].Fingerprint);
        Assert.Equal("firing", dispatchedDtos[0].Status);
        Assert.Equal("warning", dispatchedDtos[0].Severity);
        Assert.Equal("Http5xxErrorRateWarning", dispatchedDtos[0].AlertName);

        Assert.Equal("fp-2", dispatchedDtos[1].Fingerprint);
        Assert.Equal("critical", dispatchedDtos[1].Severity);

        Assert.Equal("fp-1", dispatchedDtos[2].Fingerprint);
        Assert.Equal("resolved", dispatchedDtos[2].Status);
    }

    [Fact]
    public void WebhookTokenResolver_ReadsFromFile_TrimsTrailingNewline()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "webhook-secret-token-from-file\r\n\n");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SystemAlerts:WebhookTokenFile"] = tempFile
                })
                .Build();

            var token = SystemAlertsTokenResolver.ResolveWebhookToken(config);
            Assert.Equal("webhook-secret-token-from-file", token);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SystemAlertHub_AdminToken_ConnectsAndJoinsAdminGroup()
    {
        var config = CreateConfig();
        var adminUserId = Guid.NewGuid();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(dbOptions);

        var adminRole = new Role { Id = Guid.NewGuid(), Name = "Admin" };
        var adminUser = new User
        {
            Id = adminUserId,
            Email = "admin@example.com",
            Username = "admin",
            PasswordHash = "hash",
            FullName = "Admin User",
            IsActive = true
        };
        db.Roles.Add(adminRole);
        db.Users.Add(adminUser);
        db.UserRoles.Add(new UserRole { UserId = adminUserId, RoleId = adminRole.Id });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtValidation.Secret(config)));
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, adminUserId.ToString()),
                new Claim(ClaimTypes.Role, "Admin")
            }),
            Issuer = "EventTicketBooking.Api",
            Audience = "EventTicketBooking.Client",
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
        var jwtString = tokenHandler.WriteToken(token);

        var hub = new SystemAlertHub(config, scopeFactory);

        var mockCallerContext = new Mock<HubCallerContext>();
        mockCallerContext.Setup(c => c.ConnectionId).Returns("conn-admin-1");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["access_token"] = jwtString
        });
        var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
            new TestHttpContextFeature { HttpContext = httpContext });
        mockCallerContext.Setup(c => c.Features).Returns(features);

        var mockGroupManager = new Mock<IGroupManager>();
        string? joinedGroup = null;
        mockGroupManager.Setup(g => g.AddToGroupAsync("conn-admin-1", SystemAlertHub.AdminGroupName, default))
            .Callback<string, string, CancellationToken>((connId, group, ct) => joinedGroup = group)
            .Returns(Task.CompletedTask);

        hub.Context = mockCallerContext.Object;
        hub.Groups = mockGroupManager.Object;

        await hub.OnConnectedAsync();

        Assert.Equal(SystemAlertHub.AdminGroupName, joinedGroup);
    }

    [Fact]
    public async Task SystemAlertHub_NonAdminToken_AbortsConnection()
    {
        var config = CreateConfig();
        var customerUserId = Guid.NewGuid();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(dbOptions);

        var customerRole = new Role { Id = Guid.NewGuid(), Name = "Customer" };
        var customerUser = new User
        {
            Id = customerUserId,
            Email = "customer@example.com",
            Username = "customer",
            PasswordHash = "hash",
            FullName = "Customer User",
            IsActive = true
        };
        db.Roles.Add(customerRole);
        db.Users.Add(customerUser);
        db.UserRoles.Add(new UserRole { UserId = customerUserId, RoleId = customerRole.Id });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtValidation.Secret(config)));
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, customerUserId.ToString()),
                new Claim(ClaimTypes.Role, "Customer")
            }),
            Issuer = "EventTicketBooking.Api",
            Audience = "EventTicketBooking.Client",
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
        var jwtString = tokenHandler.WriteToken(token);

        var hub = new SystemAlertHub(config, scopeFactory);

        var mockCallerContext = new Mock<HubCallerContext>();
        mockCallerContext.Setup(c => c.ConnectionId).Returns("conn-customer-1");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["access_token"] = jwtString
        });
        var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
            new TestHttpContextFeature { HttpContext = httpContext });
        mockCallerContext.Setup(c => c.Features).Returns(features);

        bool aborted = false;
        mockCallerContext.Setup(c => c.Abort()).Callback(() => aborted = true);

        hub.Context = mockCallerContext.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        await hub.OnConnectedAsync();

        Assert.True(aborted, "Customer user must have connection aborted.");
    }

    #region S-43: Database Persistence & Deduplication Tests

    [Fact]
    public async Task Webhook_FiringAlert_InsertsRecordIntoDatabase()
    {
        using var db = CreateInMemoryDb();
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAdminGroup = new Mock<IClientProxy>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(SystemAlertHub.AdminGroupName)).Returns(mockAdminGroup.Object);

        var controller = new SystemAlertsWebhookController(config, mockHub.Object, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Headers = { ["Authorization"] = $"Bearer {ExpectedSecret}" } }
                }
            }
        };

        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var payload = new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-persist-1",
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "Http5xxErrorRateWarning", ["severity"] = "warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "5xx error rate exceeded 2%" },
                    StartsAt = startsAt
                }
            }
        };

        var result = await controller.HandleAlertmanagerWebhook(payload, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var alertInDb = await db.SystemAlerts.FirstOrDefaultAsync(a => a.Fingerprint == "fp-persist-1");
        Assert.NotNull(alertInDb);
        Assert.Equal("firing", alertInDb.Status);
        Assert.Equal("Http5xxErrorRateWarning", alertInDb.AlertName);
        Assert.Equal("warning", alertInDb.Severity);
        Assert.Equal("5xx error rate exceeded 2%", alertInDb.Summary);
        Assert.Equal(startsAt, alertInDb.StartedAt);
        Assert.Null(alertInDb.ResolvedAt);
        Assert.Equal(1, await db.SystemAlerts.CountAsync());
    }

    [Fact]
    public async Task Webhook_RepeatFiringAlert_UpdatesExistingActiveRecordWithoutDuplication()
    {
        using var db = CreateInMemoryDb();
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAdminGroup = new Mock<IClientProxy>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(SystemAlertHub.AdminGroupName)).Returns(mockAdminGroup.Object);

        var controller = new SystemAlertsWebhookController(config, mockHub.Object, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Headers = { ["Authorization"] = $"Bearer {ExpectedSecret}" } }
                }
            }
        };

        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var payload1 = new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-repeat-1",
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "Http5xxErrorRateWarning", ["severity"] = "warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "5xx rate 2%" },
                    StartsAt = startsAt
                }
            }
        };

        await controller.HandleAlertmanagerWebhook(payload1, CancellationToken.None);
        var countAfterFirst = await db.SystemAlerts.CountAsync();
        Assert.Equal(1, countAfterFirst);

        // Gửi lặp lại cùng fingerprint đang firing với thông tin cập nhật
        var payload2 = new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-repeat-1",
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "Http5xxErrorRateWarning", ["severity"] = "critical" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "5xx rate escalated to 5%" },
                    StartsAt = startsAt
                }
            }
        };

        await controller.HandleAlertmanagerWebhook(payload2, CancellationToken.None);
        var countAfterSecond = await db.SystemAlerts.CountAsync();
        Assert.Equal(1, countAfterSecond); // Không tạo thêm bản ghi!

        var updated = await db.SystemAlerts.FirstAsync(a => a.Fingerprint == "fp-repeat-1");
        Assert.Equal("critical", updated.Severity);
        Assert.Equal("5xx rate escalated to 5%", updated.Summary);
        Assert.Equal("firing", updated.Status);
    }

    [Fact]
    public async Task Webhook_ResolvedAlert_UpdatesActiveFiringRecordToResolved_AndKeepsHistory()
    {
        using var db = CreateInMemoryDb();
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAdminGroup = new Mock<IClientProxy>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(SystemAlertHub.AdminGroupName)).Returns(mockAdminGroup.Object);

        var controller = new SystemAlertsWebhookController(config, mockHub.Object, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Headers = { ["Authorization"] = $"Bearer {ExpectedSecret}" } }
                }
            }
        };

        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        await controller.HandleAlertmanagerWebhook(new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-resolved-1",
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "SeatHoldConflictsRateWarning", ["severity"] = "warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "15 conflicts/min" },
                    StartsAt = startsAt
                }
            }
        }, CancellationToken.None);

        var endsAt = DateTimeOffset.UtcNow;
        await controller.HandleAlertmanagerWebhook(new AlertmanagerWebhookPayload
        {
            Status = "resolved",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-resolved-1",
                    Status = "resolved",
                    Labels = new Dictionary<string, string> { ["alertname"] = "SeatHoldConflictsRateWarning", ["severity"] = "warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "Seat conflicts back to normal" },
                    StartsAt = startsAt,
                    EndsAt = endsAt
                }
            }
        }, CancellationToken.None);

        // Kiểm tra lịch sử vẫn còn, không bị xóa
        var alertInDb = await db.SystemAlerts.FirstOrDefaultAsync(a => a.Fingerprint == "fp-resolved-1");
        Assert.NotNull(alertInDb);
        Assert.Equal("resolved", alertInDb.Status);
        Assert.Equal(endsAt, alertInDb.ResolvedAt);
        Assert.Equal(1, await db.SystemAlerts.CountAsync());
    }

    [Fact]
    public async Task Webhook_FiringAgainAfterResolved_CreatesNewIncidentRecord()
    {
        using var db = CreateInMemoryDb();
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAdminGroup = new Mock<IClientProxy>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(SystemAlertHub.AdminGroupName)).Returns(mockAdminGroup.Object);

        var controller = new SystemAlertsWebhookController(config, mockHub.Object, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Headers = { ["Authorization"] = $"Bearer {ExpectedSecret}" } }
                }
            }
        };

        const string fp = "fp-multi-incident";

        // 1. Firing incident 1
        await controller.HandleAlertmanagerWebhook(new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = fp,
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "HttpLatencyP95Warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "p95 > 1s" }
                }
            }
        }, CancellationToken.None);

        // 2. Resolved incident 1
        await controller.HandleAlertmanagerWebhook(new AlertmanagerWebhookPayload
        {
            Status = "resolved",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = fp,
                    Status = "resolved",
                    Labels = new Dictionary<string, string> { ["alertname"] = "HttpLatencyP95Warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "Latency recovered" }
                }
            }
        }, CancellationToken.None);

        Assert.Equal(1, await db.SystemAlerts.CountAsync());

        // 3. Firing incident 2 (cùng fingerprint nhưng incident mới sau khi incident cũ đã resolved)
        await controller.HandleAlertmanagerWebhook(new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = fp,
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "HttpLatencyP95Warning" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "p95 > 1s again" }
                }
            }
        }, CancellationToken.None);

        // Phải có đúng 2 bản ghi: 1 resolved và 1 firing
        var alerts = await db.SystemAlerts.Where(a => a.Fingerprint == fp).ToListAsync();
        Assert.Equal(2, alerts.Count);
        Assert.Single(alerts, a => a.Status == "resolved");
        Assert.Single(alerts, a => a.Status == "firing");
    }

    [Fact]
    public async Task Webhook_EdgeCase_ResolvedWithoutFiring_InsertsResolvedHistoryRecord()
    {
        using var db = CreateInMemoryDb();
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAdminGroup = new Mock<IClientProxy>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(SystemAlertHub.AdminGroupName)).Returns(mockAdminGroup.Object);

        var controller = new SystemAlertsWebhookController(config, mockHub.Object, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Headers = { ["Authorization"] = $"Bearer {ExpectedSecret}" } }
                }
            }
        };

        var endsAt = DateTimeOffset.UtcNow;
        var payload = new AlertmanagerWebhookPayload
        {
            Status = "resolved",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-resolved-standalone",
                    Status = "resolved",
                    Labels = new Dictionary<string, string> { ["alertname"] = "StandAloneResolved" },
                    Annotations = new Dictionary<string, string> { ["summary"] = "Auto recovered" },
                    EndsAt = endsAt
                }
            }
        };

        var result = await controller.HandleAlertmanagerWebhook(payload, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var alert = await db.SystemAlerts.FirstOrDefaultAsync(a => a.Fingerprint == "fp-resolved-standalone");
        Assert.NotNull(alert);
        Assert.Equal("resolved", alert.Status);
        Assert.Equal(endsAt, alert.ResolvedAt);
    }

    [Fact]
    public async Task Webhook_DatabaseFailure_DoesNotCrash_AndContinuesSignalRBroadcast()
    {
        var config = CreateConfig();
        var mockHub = new Mock<IHubContext<SystemAlertHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAdminGroup = new Mock<IClientProxy>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(SystemAlertHub.AdminGroupName)).Returns(mockAdminGroup.Object);

        var signalRSent = false;
        mockAdminGroup.Setup(g => g.SendCoreAsync("ReceiveSystemAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback(() => signalRSent = true)
            .Returns(Task.CompletedTask);

        // Tạo dbContext bị dispose để gây ra database exception
        var faultyDb = CreateInMemoryDb();
        faultyDb.Dispose();

        var controller = new SystemAlertsWebhookController(config, mockHub.Object, faultyDb)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Headers = { ["Authorization"] = $"Bearer {ExpectedSecret}" } }
                }
            }
        };

        var payload = new AlertmanagerWebhookPayload
        {
            Status = "firing",
            Alerts = new List<AlertmanagerAlertItem>
            {
                new()
                {
                    Fingerprint = "fp-db-fail",
                    Status = "firing",
                    Labels = new Dictionary<string, string> { ["alertname"] = "DbFailAlert" }
                }
            }
        };

        // Không crash 500, trả về 200 OK và SignalR vẫn được broadcast
        var result = await controller.HandleAlertmanagerWebhook(payload, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.True(signalRSent, "SignalR broadcast must continue even when DB persistence fails.");
    }

    #endregion

    #region S-43: Admin API Tests (GET /api/system-alerts)

    [Fact]
    public async Task AdminApi_GetAlerts_Returns200WithApiResponse_OrderedByLastUpdatedAt()
    {
        using var db = CreateInMemoryDb();
        var now = DateTimeOffset.UtcNow;

        db.SystemAlerts.AddRange(
            new SystemAlert { Id = Guid.NewGuid(), Fingerprint = "fp-1", AlertName = "Alert 1", Status = "firing", LastUpdatedAt = now.AddMinutes(-10) },
            new SystemAlert { Id = Guid.NewGuid(), Fingerprint = "fp-2", AlertName = "Alert 2", Status = "resolved", LastUpdatedAt = now.AddMinutes(-5) },
            new SystemAlert { Id = Guid.NewGuid(), Fingerprint = "fp-3", AlertName = "Alert 3", Status = "firing", LastUpdatedAt = now }
        );
        await db.SaveChangesAsync();

        var controller = new SystemAlertsController(db);
        var result = await controller.GetAlerts(limit: 50);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<EventTicketBooking.Api.DTOs.Common.ApiResponse<List<SystemAlertResponseDto>>>(okResult.Value);

        Assert.True(apiResponse.Success);
        Assert.Equal(3, apiResponse.Data!.Count);
        // Mới nhất trước
        Assert.Equal("Alert 3", apiResponse.Data[0].AlertName);
        Assert.Equal("Alert 2", apiResponse.Data[1].AlertName);
        Assert.Equal("Alert 1", apiResponse.Data[2].AlertName);
    }

    [Fact]
    public async Task AdminApi_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        using var db = CreateInMemoryDb();
        db.SystemAlerts.AddRange(
            new SystemAlert { Id = Guid.NewGuid(), Fingerprint = "fp-1", AlertName = "Alert 1", Status = "firing" },
            new SystemAlert { Id = Guid.NewGuid(), Fingerprint = "fp-2", AlertName = "Alert 2", Status = "resolved" },
            new SystemAlert { Id = Guid.NewGuid(), Fingerprint = "fp-3", AlertName = "Alert 3", Status = "firing" }
        );
        await db.SaveChangesAsync();

        var controller = new SystemAlertsController(db);

        // Filter firing
        var resultFiring = await controller.GetAlerts(limit: 50, status: "firing");
        var okFiring = Assert.IsType<OkObjectResult>(resultFiring);
        var resFiring = Assert.IsType<EventTicketBooking.Api.DTOs.Common.ApiResponse<List<SystemAlertResponseDto>>>(okFiring.Value);
        Assert.Equal(2, resFiring.Data!.Count);
        Assert.All(resFiring.Data, a => Assert.Equal("firing", a.Status));

        // Filter resolved
        var resultResolved = await controller.GetAlerts(limit: 50, status: "resolved");
        var okResolved = Assert.IsType<OkObjectResult>(resultResolved);
        var resResolved = Assert.IsType<EventTicketBooking.Api.DTOs.Common.ApiResponse<List<SystemAlertResponseDto>>>(okResolved.Value);
        Assert.Single(resResolved.Data!);
        Assert.Equal("resolved", resResolved.Data[0].Status);
    }

    [Fact]
    public async Task AdminApi_InvalidStatus_Returns400BadRequest()
    {
        using var db = CreateInMemoryDb();
        var controller = new SystemAlertsController(db);

        var result = await controller.GetAlerts(limit: 50, status: "invalid_status");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var apiResponse = Assert.IsType<EventTicketBooking.Api.DTOs.Common.ApiResponse<object>>(badRequest.Value);
        Assert.False(apiResponse.Success);
        Assert.Contains("không hợp lệ", apiResponse.Message);
    }

    [Fact]
    public async Task AdminApi_LimitClamping_ClampsBelow1AndAbove100()
    {
        using var db = CreateInMemoryDb();
        for (int i = 0; i < 110; i++)
        {
            db.SystemAlerts.Add(new SystemAlert
            {
                Id = Guid.NewGuid(),
                Fingerprint = $"fp-{i}",
                AlertName = $"Alert {i}",
                Status = "firing"
            });
        }
        await db.SaveChangesAsync();

        var controller = new SystemAlertsController(db);

        // limit = -10 -> clamp thành 1
        var resClampLow = await controller.GetAlerts(limit: -10);
        var okLow = Assert.IsType<OkObjectResult>(resClampLow);
        var dataLow = Assert.IsType<EventTicketBooking.Api.DTOs.Common.ApiResponse<List<SystemAlertResponseDto>>>(okLow.Value);
        Assert.Single(dataLow.Data!);

        // limit = 200 -> clamp thành 100
        var resClampHigh = await controller.GetAlerts(limit: 200);
        var okHigh = Assert.IsType<OkObjectResult>(resClampHigh);
        var dataHigh = Assert.IsType<EventTicketBooking.Api.DTOs.Common.ApiResponse<List<SystemAlertResponseDto>>>(okHigh.Value);
        Assert.Equal(100, dataHigh.Data!.Count);
    }

    [Theory]
    [InlineData("Admin", 200)]
    [InlineData("Organizer", 403)]
    [InlineData("Customer", 403)]
    [InlineData("Staff", 403)]
    [InlineData("no_auth", 401)]
    public async Task AdminApi_Authorization_EnforcesAdminRoleOnly(string roleName, int expectedStatusCode)
    {
        using var db = CreateInMemoryDb();
        var config = CreateConfig();

        var role = new Role { Id = Guid.NewGuid(), Name = roleName };
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = $"{roleName.ToLower()}@example.com",
            Username = roleName.ToLower(),
            PasswordHash = "hash",
            FullName = $"{roleName} User",
            IsActive = true
        };
        db.Roles.Add(role);
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        if (roleName != "no_auth")
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtValidation.Secret(config)));
            var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Role, roleName)
                }),
                Issuer = "EventTicketBooking.Api",
                Audience = "EventTicketBooking.Client",
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            });
            context.Request.Headers.Authorization = "Bearer " + tokenHandler.WriteToken(token);
        }

        // Endpoint metadata mang [RequireRole("Admin")] tương tự SystemAlertsController
        var endpoint = new Endpoint(
            _ => { context.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
            new EndpointMetadataCollection(new RequireRoleAttribute("Admin")),
            "SystemAlerts_GetAlerts");
        context.SetEndpoint(endpoint);

        var middleware = new RoleAuthorizationMiddleware(nextContext =>
        {
            nextContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, db, config);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
    }

    #endregion

    private sealed class TestHttpContextFeature : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}
