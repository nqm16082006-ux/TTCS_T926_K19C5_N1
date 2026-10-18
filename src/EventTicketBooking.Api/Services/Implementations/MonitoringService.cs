using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Monitoring;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Monitoring;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class MonitoringService : IMonitoringService
    {
        private readonly AppDbContext _dbContext;
        private readonly ITelemetryChannel _channel;
        private readonly ILogger<MonitoringService> _logger;

        public MonitoringService(
            AppDbContext dbContext,
            ITelemetryChannel channel,
            ILogger<MonitoringService>? logger = null)
        {
            _dbContext = dbContext;
            _channel = channel;
            _logger = logger ?? NullLogger<MonitoringService>.Instance;
        }

        public async Task<MonitoringSummaryDto> GetSummaryAsync(string window = "15m", CancellationToken cancellationToken = default)
        {
            var windowMinutes = ParseWindowMinutes(window);
            var windowEnd = DateTimeOffset.UtcNow;
            var windowStart = windowEnd.AddMinutes(-windowMinutes);

            var requestQuery = _dbContext.RequestTelemetries
                .AsNoTracking()
                .Where(r => r.OccurredAtUtc >= windowStart && r.OccurredAtUtc <= windowEnd);

            var requestCount = await requestQuery.LongCountAsync(cancellationToken);
            var errorCount = await requestQuery.Where(r => r.StatusCode >= 500).LongCountAsync(cancellationToken);

            double errorRatePercent = requestCount > 0 ? Math.Round(errorCount * 100.0 / requestCount, 2) : 0.0;
            double p95 = 0.0;

            if (requestCount > 0)
            {
                if (_dbContext.Database.IsNpgsql())
                {
                    try
                    {
                        var sql = @"SELECT COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY ""DurationMs""), 0) AS ""Value""
                                    FROM ""RequestTelemetries""
                                    WHERE ""OccurredAtUtc"" >= {0} AND ""OccurredAtUtc"" <= {1}";
                        var result = await _dbContext.Database.SqlQueryRaw<double>(sql, windowStart, windowEnd).ToListAsync(cancellationToken);
                        p95 = result.Count > 0 ? Math.Round(result[0], 1) : 0.0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Không thể tính P95 qua SQL percentile_cont, chuyển sang in-memory calculation.");
                        p95 = await CalculateP95InMemoryAsync(windowStart, windowEnd, cancellationToken);
                    }
                }
                else
                {
                    p95 = await CalculateP95InMemoryAsync(windowStart, windowEnd, cancellationToken);
                }
            }

            var eventsQuery = _dbContext.MonitoringEvents
                .AsNoTracking()
                .Where(e => e.OccurredAtUtc >= windowStart && e.OccurredAtUtc <= windowEnd);

            var seatConflictCount = await eventsQuery.Where(e => e.Type == "SeatConflict").LongCountAsync(cancellationToken);
            var rejectedWebhookCount = await eventsQuery.Where(e => e.Type == "WebhookRejected").LongCountAsync(cancellationToken);

            var activeAlertCount = await _dbContext.MonitoringAlerts.CountAsync(a => a.IsActive, cancellationToken);

            return new MonitoringSummaryDto
            {
                RequestCount = requestCount,
                ErrorCount = errorCount,
                ErrorRatePercent = errorRatePercent,
                P95Milliseconds = p95,
                SeatConflictCount = seatConflictCount,
                RejectedWebhookCount = rejectedWebhookCount,
                ActiveAlertCount = activeAlertCount,
                WindowStartUtc = windowStart,
                WindowEndUtc = windowEnd,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                PrometheusAvailable = true,
                RequestRate = requestCount,
                P95ResponseTimeMs = p95,
                SeatConflicts = seatConflictCount,
                RejectedWebhooks = rejectedWebhookCount
            };
        }

        public async Task<MonitoringTimeSeriesDto> GetTimeSeriesAsync(string window = "1h", int stepSeconds = 60, CancellationToken cancellationToken = default)
        {
            var windowMinutes = ParseWindowMinutes(window);
            if (stepSeconds < 5) stepSeconds = 5;
            if (stepSeconds > 300) stepSeconds = 300;

            var windowEnd = DateTimeOffset.UtcNow;
            var windowStart = windowEnd.AddMinutes(-windowMinutes);

            var requests = await _dbContext.RequestTelemetries
                .AsNoTracking()
                .Where(r => r.OccurredAtUtc >= windowStart && r.OccurredAtUtc <= windowEnd)
                .Select(r => new { r.OccurredAtUtc, r.StatusCode, r.DurationMs })
                .ToListAsync(cancellationToken);

            var result = new MonitoringTimeSeriesDto
            {
                PrometheusAvailable = true,
                GeneratedAt = windowEnd
            };

            var startEpoch = windowStart.ToUnixTimeSeconds();
            var endEpoch = windowEnd.ToUnixTimeSeconds();

            // Bucket requests by stepSeconds intervals
            for (long t = startEpoch; t <= endEpoch; t += stepSeconds)
            {
                var bucketStart = DateTimeOffset.FromUnixTimeSeconds(t);
                var bucketEnd = bucketStart.AddSeconds(stepSeconds);

                var bucketRequests = requests.Where(r => r.OccurredAtUtc >= bucketStart && r.OccurredAtUtc < bucketEnd).ToList();
                int count = bucketRequests.Count;

                double rate = Math.Round((double)count / stepSeconds, 2);
                double? errRate = null;
                double? p95Ms = null;

                if (count > 0)
                {
                    int errCount = bucketRequests.Count(r => r.StatusCode >= 500);
                    errRate = Math.Round((double)errCount * 100.0 / count, 2);

                    var durations = bucketRequests.Select(r => r.DurationMs).OrderBy(d => d).ToList();
                    int idx = (int)Math.Ceiling(0.95 * durations.Count) - 1;
                    if (idx < 0) idx = 0;
                    if (idx >= durations.Count) idx = durations.Count - 1;
                    p95Ms = Math.Round(durations[idx], 1);
                }

                result.RequestRateSeries.Add(new MetricDataPointDto { Timestamp = t, Value = rate });
                result.ErrorRateSeries.Add(new MetricDataPointDto { Timestamp = t, Value = errRate });
                result.P95LatencySeries.Add(new MetricDataPointDto { Timestamp = t, Value = p95Ms });
            }

            return result;
        }

        public async Task<List<MonitoringAlert>> GetAlertsAsync(bool? isActive = null, int limit = 50, CancellationToken cancellationToken = default)
        {
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            var query = _dbContext.MonitoringAlerts.AsNoTracking();

            if (isActive.HasValue)
            {
                query = query.Where(a => a.IsActive == isActive.Value);
            }

            return await query
                .OrderByDescending(a => a.IsActive)
                .ThenByDescending(a => a.LastTriggeredAtUtc)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }

        public async Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var reqCount = await _dbContext.RequestTelemetries.LongCountAsync(cancellationToken);
            var evCount = await _dbContext.MonitoringEvents.LongCountAsync(cancellationToken);
            var activeAlerts = await _dbContext.MonitoringAlerts.CountAsync(a => a.IsActive, cancellationToken);

            return new MonitoringStatusDto
            {
                IsHealthy = true,
                StorageType = _dbContext.Database.IsNpgsql() ? "PostgreSQL (Shared Neon)" : "Relational DB",
                TotalPersistedRequests = reqCount,
                TotalPersistedEvents = evCount,
                ActiveAlertsCount = activeAlerts,
                DroppedRequestsCount = _channel.DroppedRequestsCount,
                DroppedEventsCount = _channel.DroppedEventsCount,
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private async Task<double> CalculateP95InMemoryAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
        {
            var durations = await _dbContext.RequestTelemetries
                .AsNoTracking()
                .Where(r => r.OccurredAtUtc >= windowStart && r.OccurredAtUtc <= windowEnd)
                .Select(r => r.DurationMs)
                .OrderBy(d => d)
                .ToListAsync(ct);

            if (durations.Count == 0) return 0.0;

            int index = (int)Math.Ceiling(0.95 * durations.Count) - 1;
            if (index < 0) index = 0;
            if (index >= durations.Count) index = durations.Count - 1;

            return Math.Round(durations[index], 1);
        }

        private static int ParseWindowMinutes(string window)
        {
            if (string.IsNullOrWhiteSpace(window)) return 15;
            var w = window.Trim().ToLowerInvariant();
            return w switch
            {
                "5m" => 5,
                "15m" => 15,
                "1h" or "60m" => 60,
                "24h" or "1d" => 1440,
                _ => int.TryParse(w.TrimEnd('m', 'h', 'd'), out var val) && val > 0 ? val : 15
            };
        }
    }
}
