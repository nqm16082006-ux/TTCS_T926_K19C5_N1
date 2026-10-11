using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;

namespace EventTicketBooking.Api.Controllers;

[ApiController]
[Route("api/system-alerts")]
public class SystemAlertsWebhookController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHubContext<SystemAlertHub> _hubContext;

    public SystemAlertsWebhookController(
        IConfiguration configuration,
        IHubContext<SystemAlertHub> hubContext)
    {
        _configuration = configuration;
        _hubContext = hubContext;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleAlertmanagerWebhook(
        [FromBody] AlertmanagerWebhookPayload? payload,
        CancellationToken cancellationToken)
    {
        // 1. Kiểm tra xác thực Bearer token bí mật
        var configuredToken = SystemAlertsTokenResolver.ResolveWebhookToken(_configuration);
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var authHeader = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var providedToken = authHeader[bearerPrefix.Length..].Trim();
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);

        if (providedBytes.Length != configuredBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // 2. Validate payload: empty / malformed payload trả về 400
        if (payload == null || payload.Alerts == null || payload.Alerts.Count == 0)
        {
            return BadRequest(new { message = "Invalid or empty alerts payload." });
        }

        // 3. Xử lý tất cả các alert trong payload và gửi realtime tới nhóm Admins
        int processedCount = 0;
        foreach (var alert in payload.Alerts)
        {
            var fingerprint = alert.Fingerprint?.Trim();
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                fingerprint = Guid.NewGuid().ToString("N");
            }

            var status = (alert.Status ?? payload.Status ?? "firing").Trim().ToLowerInvariant();
            var alertName = alert.Labels != null && alert.Labels.TryGetValue("alertname", out var name)
                ? name
                : "SystemAlert";
            var severity = alert.Labels != null && alert.Labels.TryGetValue("severity", out var sev)
                ? sev
                : "warning";
            var summary = alert.Annotations != null && alert.Annotations.TryGetValue("summary", out var sum)
                ? sum
                : (alert.Annotations != null && alert.Annotations.TryGetValue("description", out var desc) ? desc : alertName);

            var dto = new SystemAlertNotificationDto
            {
                Fingerprint = fingerprint,
                Status = status,
                AlertName = alertName,
                Severity = severity,
                Summary = summary
            };

            await _hubContext.Clients.Group(SystemAlertHub.AdminGroupName)
                .SendAsync("ReceiveSystemAlert", dto, cancellationToken);

            processedCount++;
        }

        return Ok(new { processed = processedCount });
    }
}

public static class SystemAlertsTokenResolver
{
    public const string DefaultSecretPath = "/run/secrets/alertmanager_webhook_token";

    public static string? ResolveWebhookToken(IConfiguration configuration, string? defaultSecretPath = null)
    {
        var direct = (configuration["SystemAlerts:WebhookToken"] ?? configuration["SYSTEM_ALERTS_WEBHOOK_TOKEN"])?.Trim();
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var filePath = (configuration["SystemAlerts:WebhookTokenFile"] ?? configuration["SYSTEM_ALERTS_WEBHOOK_TOKEN_FILE"])?.Trim();
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                var content = File.ReadAllText(filePath).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }
            catch
            {
                // Không log token hoặc chi tiết lỗi nhạy cảm
            }
        }

        var fallback = defaultSecretPath ?? DefaultSecretPath;
        if (File.Exists(fallback))
        {
            try
            {
                var content = File.ReadAllText(fallback).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }
            catch
            {
                // Không log token
            }
        }

        return null;
    }
}

public sealed class AlertmanagerWebhookPayload
{
    public string? Version { get; set; }
    public string? Status { get; set; }
    public string? Receiver { get; set; }
    public List<AlertmanagerAlertItem>? Alerts { get; set; }
}

public sealed class AlertmanagerAlertItem
{
    public string? Status { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? Annotations { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public string? Fingerprint { get; set; }
}

public sealed class SystemAlertNotificationDto
{
    public string Fingerprint { get; set; } = string.Empty;
    public string Status { get; set; } = "firing";
    public string AlertName { get; set; } = string.Empty;
    public string Severity { get; set; } = "warning";
    public string Summary { get; set; } = string.Empty;
}
