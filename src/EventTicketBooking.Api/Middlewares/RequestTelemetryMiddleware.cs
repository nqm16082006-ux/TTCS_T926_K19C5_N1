using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace EventTicketBooking.Api.Middlewares
{
    public class RequestTelemetryMiddleware
    {
        private static readonly Regex GuidRegex = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
        private static readonly Regex NumericSegmentRegex = new(@"/(?:\d+)(?=/|$)", RegexOptions.Compiled);

        private readonly RequestDelegate _next;
        private readonly ITelemetryChannel _channel;
        private readonly string _environment;
        private readonly string _instanceId;

        public RequestTelemetryMiddleware(
            RequestDelegate next,
            ITelemetryChannel channel,
            IHostEnvironment? hostEnvironment = null)
        {
            _next = next;
            _channel = channel;
            _environment = hostEnvironment?.EnvironmentName ?? "Development";
            _instanceId = System.Environment.MachineName;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            if (ShouldExclude(path))
            {
                await _next(context);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var occurredAtUtc = DateTimeOffset.UtcNow;
            var statusCode = 200;

            try
            {
                await _next(context);
                statusCode = context.Response.StatusCode;
            }
            catch
            {
                statusCode = 500;
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var route = NormalizeRoute(context, path);

                var telemetry = new RequestTelemetry
                {
                    Id = Guid.NewGuid(),
                    OccurredAtUtc = occurredAtUtc,
                    Method = context.Request.Method.ToUpperInvariant(),
                    Route = route,
                    StatusCode = statusCode,
                    DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                    Environment = _environment,
                    InstanceId = _instanceId
                };

                _channel.TryWrite(telemetry);
            }
        }

        public static bool ShouldExclude(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return false; // Root API / homepage request is tracked
            }

            var lower = path.ToLowerInvariant();

            // Static files and assets
            if (lower.EndsWith(".html") || lower.EndsWith(".css") || lower.EndsWith(".js") ||
                lower.EndsWith(".ico") || lower.EndsWith(".png") || lower.EndsWith(".jpg") ||
                lower.EndsWith(".jpeg") || lower.EndsWith(".svg") || lower.EndsWith(".woff") ||
                lower.EndsWith(".woff2") || lower.EndsWith(".map") || lower.EndsWith(".json") && lower.Contains("/seats-"))
            {
                return true;
            }

            if (lower.StartsWith("/uploads/") || lower == "/favicon.ico")
            {
                return true;
            }

            // Exclude monitoring UI & APIs to avoid infinite polling telemetry feedback loops
            if (lower.StartsWith("/api/v1/monitoring") ||
                lower.StartsWith("/api/admin/monitoring") ||
                lower.StartsWith("/api/monitoring") ||
                lower == "/monitoring.html" ||
                lower == "/admin-monitoring.html")
            {
                return true;
            }

            // Exclude scrape & health check endpoints
            if (lower == "/metrics" || lower == "/health" || lower == "/healthz" || lower == "/ready")
            {
                return true;
            }

            // Exclude SignalR hub negotiations/connections
            if (lower.StartsWith("/hubs/"))
            {
                return true;
            }

            // Exclude Swagger UI assets
            if (lower.StartsWith("/swagger"))
            {
                return true;
            }

            return false;
        }

        public static string NormalizeRoute(HttpContext context, string rawPath)
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is RouteEndpoint routeEndpoint && !string.IsNullOrWhiteSpace(routeEndpoint.RoutePattern.RawText))
            {
                var template = routeEndpoint.RoutePattern.RawText.Trim();
                if (!template.StartsWith('/'))
                {
                    template = "/" + template;
                }
                return $"{context.Request.Method.ToUpperInvariant()} {template}";
            }

            // Fallback: Normalize rawPath by masking GUIDs and numeric path segments
            var normalized = GuidRegex.Replace(rawPath, "{id}");
            normalized = NumericSegmentRegex.Replace(normalized, "/{id}");
            return $"{context.Request.Method.ToUpperInvariant()} {normalized}";
        }
    }
}
