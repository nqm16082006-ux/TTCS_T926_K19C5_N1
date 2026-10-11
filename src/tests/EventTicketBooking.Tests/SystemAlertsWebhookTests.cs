using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Hubs;
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

    private sealed class TestHttpContextFeature : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}
