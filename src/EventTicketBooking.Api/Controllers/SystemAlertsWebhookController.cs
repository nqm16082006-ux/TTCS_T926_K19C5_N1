using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventTicketBooking.Api.Controllers;

[ApiController]
[Route("api/system-alerts")]
public class SystemAlertsWebhookController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHubContext<SystemAlertHub> _hubContext;
    private readonly AppDbContext? _dbContext;
    private readonly ILogger<SystemAlertsWebhookController> _logger;

    public SystemAlertsWebhookController(
        IConfiguration configuration,
        IHubContext<SystemAlertHub> hubContext,
        AppDbContext? dbContext = null,
        ILogger<SystemAlertsWebhookController>? logger = null)
    {
        _configuration = configuration;
        _hubContext = hubContext;
        _dbContext = dbContext;
        _logger = logger ?? NullLogger<SystemAlertsWebhookController>.Instance;
    }

    [HttpPost("webhook")]
    [AllowAnonymous]
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

        // 3. Xử lý tất cả các alert trong payload: persist vào DB và gửi realtime qua SignalR tới nhóm Admins
        int processedCount = 0;
        foreach (var alert in payload.Alerts)
        {
            var fingerprint = alert.Fingerprint?.Trim();
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                fingerprint = Guid.NewGuid().ToString("N");
            }

            var rawStatus = (alert.Status ?? payload.Status ?? "firing").Trim().ToLowerInvariant();
            var status = rawStatus == "resolved" ? "resolved" : "firing";

            var alertName = alert.Labels != null && alert.Labels.TryGetValue("alertname", out var name)
                ? name.Trim()
                : "SystemAlert";

            var rawSeverity = alert.Labels != null && alert.Labels.TryGetValue("severity", out var sev)
                ? sev.Trim().ToLowerInvariant()
                : "warning";
            var severity = string.IsNullOrWhiteSpace(rawSeverity) ? "warning" : rawSeverity;

            var summary = alert.Annotations != null && alert.Annotations.TryGetValue("summary", out var sum)
                ? sum.Trim()
                : (alert.Annotations != null && alert.Annotations.TryGetValue("description", out var desc) ? desc.Trim() : alertName);

            // Persist vào Database (với resilience và concurrency retry)
            if (_dbContext != null)
            {
                await PersistAlertAsync(fingerprint, status, alertName, severity, summary, alert.StartsAt, alert.EndsAt, cancellationToken);
            }

            // Gửi realtime qua SignalR tới nhóm Admins
            var dto = new SystemAlertNotificationDto
            {
                Fingerprint = fingerprint,
                Status = status,
                AlertName = alertName,
                Severity = severity,
                Summary = summary,
                StartedAt = alert.StartsAt,
                ResolvedAt = alert.EndsAt
            };

            await _hubContext.Clients.Group(SystemAlertHub.AdminGroupName)
                .SendAsync("ReceiveSystemAlert", dto, cancellationToken);

            processedCount++;
        }

        return Ok(new { processed = processedCount });
    }

    private async Task PersistAlertAsync(
        string fingerprint,
        string status,
        string alertName,
        string severity,
        string summary,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecutePersistAsync(fingerprint, status, alertName, severity, summary, startsAt, endsAt, cancellationToken);
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
        {
            // Xử lý race condition: 2 webhook cùng lúc cố gắng tạo alert firing cho cùng fingerprint
            _logger.LogWarning("Concurrent firing insert detected for fingerprint {Fingerprint}. Reconciling tracking and retrying update.", fingerprint);
            try
            {
                _dbContext!.ChangeTracker.Clear();
                var existingFiring = await _dbContext.SystemAlerts
                    .FirstOrDefaultAsync(a => a.Fingerprint == fingerprint && a.Status == "firing", cancellationToken);

                if (existingFiring != null)
                {
                    if (status == "firing")
                    {
                        existingFiring.AlertName = alertName;
                        existingFiring.Severity = severity;
                        existingFiring.Summary = summary;
                        existingFiring.LastUpdatedAt = DateTimeOffset.UtcNow;
                    }
                    else if (status == "resolved")
                    {
                        existingFiring.Status = "resolved";
                        existingFiring.ResolvedAt = (endsAt.HasValue && endsAt.Value > DateTimeOffset.MinValue) ? endsAt.Value : DateTimeOffset.UtcNow;
                        existingFiring.LastUpdatedAt = DateTimeOffset.UtcNow;
                    }
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "System alert persistence retry failed for fingerprint {Fingerprint}, realtime delivery continued.", fingerprint);
            }
        }
        catch (Exception ex)
        {
            // Log rõ lỗi nhưng không làm crash toàn pipeline (không log token/secret)
            _logger.LogError(ex, "System alert persistence failed for fingerprint {Fingerprint}, realtime delivery continued.", fingerprint);
        }
    }

    private async Task ExecutePersistAsync(
        string fingerprint,
        string status,
        string alertName,
        string severity,
        string summary,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (status == "firing")
        {
            var existingFiring = await _dbContext!.SystemAlerts
                .FirstOrDefaultAsync(a => a.Fingerprint == fingerprint && a.Status == "firing", cancellationToken);

            if (existingFiring != null)
            {
                // Firing lặp lại cùng fingerprint đang active -> Cập nhật thông tin, KHÔNG tạo record mới
                existingFiring.AlertName = alertName;
                existingFiring.Severity = severity;
                existingFiring.Summary = summary;
                existingFiring.LastUpdatedAt = now;
            }
            else
            {
                // Firing lần đầu hoặc incident mới (incident cũ đã resolved) -> Tạo record mới
                var newAlert = new SystemAlert
                {
                    Id = Guid.NewGuid(),
                    Fingerprint = fingerprint,
                    AlertName = alertName,
                    Severity = severity,
                    Summary = summary,
                    Status = "firing",
                    StartedAt = (startsAt.HasValue && startsAt.Value > DateTimeOffset.MinValue) ? startsAt.Value : now,
                    ResolvedAt = null,
                    CreatedAt = now,
                    LastUpdatedAt = now
                };
                _dbContext.SystemAlerts.Add(newAlert);
            }
        }
        else if (status == "resolved")
        {
            var existingFiring = await _dbContext!.SystemAlerts
                .FirstOrDefaultAsync(a => a.Fingerprint == fingerprint && a.Status == "firing", cancellationToken);

            if (existingFiring != null)
            {
                existingFiring.Status = "resolved";
                existingFiring.ResolvedAt = (endsAt.HasValue && endsAt.Value > DateTimeOffset.MinValue) ? endsAt.Value : now;
                existingFiring.LastUpdatedAt = now;
            }
            else
            {
                // Edge case: nhận được resolved nhưng không tìm thấy firing record active tương ứng.
                // Lưu bản ghi resolved để đảm bảo không mất lịch sử incident.
                var resolvedAlert = new SystemAlert
                {
                    Id = Guid.NewGuid(),
                    Fingerprint = fingerprint,
                    AlertName = alertName,
                    Severity = severity,
                    Summary = summary,
                    Status = "resolved",
                    StartedAt = (startsAt.HasValue && startsAt.Value > DateTimeOffset.MinValue) ? startsAt.Value : ((endsAt.HasValue && endsAt.Value > DateTimeOffset.MinValue) ? endsAt.Value : now),
                    ResolvedAt = (endsAt.HasValue && endsAt.Value > DateTimeOffset.MinValue) ? endsAt.Value : now,
                    CreatedAt = now,
                    LastUpdatedAt = now
                };
                _dbContext.SystemAlerts.Add(resolvedAlert);
            }
        }

        await _dbContext!.SaveChangesAsync(cancellationToken);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        if (message.Contains("IX_SystemAlerts_Fingerprint_Firing", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
