using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services
{
    /// <summary>
    /// Centralized security audit logger for ownership denials (HTTP 403) across controllers.
    /// S-02 Invariant: All ownership rejections emit structured security audit warnings without leaking secrets.
    /// </summary>
    public static class SecurityAuditLogger
    {
        public static void LogOwnershipDenied(
            HttpContext context,
            string resourceType,
            string? resourceId = null,
            ILogger? logger = null)
        {
            if (context == null) return;

            logger ??= context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("EventTicketBooking.Security.OwnershipAudit")
                      ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

            var timestamp = DateTime.UtcNow.ToString("o");
            var userId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                         ?? context.User?.FindFirst("id")?.Value
                         ?? context.User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                         ?? "Unknown";
            var role = context.User?.FindFirst(ClaimTypes.Role)?.Value
                       ?? context.User?.FindFirst("role")?.Value
                       ?? "Unknown";
            var method = context.Request?.Method ?? "UNKNOWN";
            var path = context.Request?.Path.Value ?? string.Empty;
            var ip = context.Connection?.RemoteIpAddress?.ToString() ?? "Unknown";
            var traceId = context.TraceIdentifier ?? "Unknown";

            logger.LogWarning(
                "[SECURITY AUDIT 403 OWNERSHIP] OwnershipDenied. Timestamp: {Timestamp} | UserId: {UserId} | Role: {Role} | Method: {Method} | Path: {Path} | ResourceType: {ResourceType} | ResourceId: {ResourceId} | Reason: OwnershipDenied | IP: {IP} | TraceId: {TraceId}",
                timestamp, userId, role, method, path, resourceType, resourceId ?? "N/A", ip, traceId);
        }
    }
}
