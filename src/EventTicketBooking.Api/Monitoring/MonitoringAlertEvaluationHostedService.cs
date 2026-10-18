using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Monitoring
{
    public class MonitoringAlertEvaluationHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptions<MonitoringOptions> _options;
        private readonly ILogger<MonitoringAlertEvaluationHostedService> _logger;
        private DateTime _lastRetentionCleanup = DateTime.MinValue;

        public MonitoringAlertEvaluationHostedService(
            IServiceScopeFactory scopeFactory,
            IOptions<MonitoringOptions> options,
            ILogger<MonitoringAlertEvaluationHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var config = _options.Value;
            if (!config.Enabled)
            {
                _logger.LogInformation("Dịch vụ đánh giá cảnh báo giám sát đã bị tắt qua cấu hình.");
                return;
            }

            _logger.LogInformation("Khởi động dịch vụ đánh giá cảnh báo giám sát tự động (Story S-43).");

            var interval = TimeSpan.FromMinutes(Math.Max(1, config.AlertEvaluationMinutes));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAlertsAsync(stoppingToken);

                    // Chạy dọn dẹp dữ liệu cũ (Retention) mỗi 1 giờ
                    if (DateTime.UtcNow - _lastRetentionCleanup > TimeSpan.FromHours(1))
                    {
                        await RunRetentionCleanupAsync(stoppingToken);
                        _lastRetentionCleanup = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong quá trình đánh giá cảnh báo hệ thống.");
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public async Task EvaluateAlertsAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var monitoringService = scope.ServiceProvider.GetRequiredService<IMonitoringService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var summary = await monitoringService.GetSummaryAsync("15m", cancellationToken);
            var thresholds = _options.Value.Thresholds;
            var now = DateTimeOffset.UtcNow;

            // 1. Tỉ lệ lỗi (ErrorRatePercent)
            var errorRate = summary.ErrorRatePercent ?? 0.0;
            await EvaluateSingleMetricAsync(
                dbContext,
                metric: "ErrorRatePercent",
                severity: "Critical",
                threshold: thresholds.ErrorRatePercent,
                observed: errorRate,
                windowMinutes: 15,
                isViolated: summary.RequestCount >= 10 && errorRate > thresholds.ErrorRatePercent,
                now: now,
                cancellationToken: cancellationToken);

            // 2. Độ trễ P95 (P95Milliseconds)
            var p95 = summary.P95Milliseconds ?? 0.0;
            await EvaluateSingleMetricAsync(
                dbContext,
                metric: "P95Milliseconds",
                severity: "Warning",
                threshold: thresholds.P95Milliseconds,
                observed: p95,
                windowMinutes: 15,
                isViolated: summary.RequestCount >= 5 && p95 > thresholds.P95Milliseconds,
                now: now,
                cancellationToken: cancellationToken);

            // 3. Tranh chấp ghế (SeatConflicts15m)
            await EvaluateSingleMetricAsync(
                dbContext,
                metric: "SeatConflicts15m",
                severity: "Warning",
                threshold: thresholds.SeatConflicts15m,
                observed: summary.SeatConflictCount,
                windowMinutes: 15,
                isViolated: summary.SeatConflictCount > thresholds.SeatConflicts15m,
                now: now,
                cancellationToken: cancellationToken);

            // 4. Webhook bị từ chối (RejectedWebhooks15m)
            await EvaluateSingleMetricAsync(
                dbContext,
                metric: "RejectedWebhooks15m",
                severity: "Critical",
                threshold: thresholds.RejectedWebhooks15m,
                observed: summary.RejectedWebhookCount,
                windowMinutes: 15,
                isViolated: summary.RejectedWebhookCount > thresholds.RejectedWebhooks15m,
                now: now,
                cancellationToken: cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        private static async Task EvaluateSingleMetricAsync(
            AppDbContext dbContext,
            string metric,
            string severity,
            double threshold,
            double observed,
            int windowMinutes,
            bool isViolated,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var existingAlert = await dbContext.MonitoringAlerts
                .FirstOrDefaultAsync(a => a.IsActive && a.Metric == metric, cancellationToken);

            if (isViolated)
            {
                if (existingAlert != null)
                {
                    // Đã có alert active -> Cập nhật thời điểm và giá trị quan sát mới nhất (chống tạo duplicate)
                    existingAlert.LastTriggeredAtUtc = now;
                    existingAlert.ObservedValue = observed;
                }
                else
                {
                    // Tạo mới alert
                    var alert = new MonitoringAlert
                    {
                        Id = Guid.NewGuid(),
                        Metric = metric,
                        Severity = severity,
                        Threshold = threshold,
                        ObservedValue = observed,
                        WindowMinutes = windowMinutes,
                        FirstTriggeredAtUtc = now,
                        LastTriggeredAtUtc = now,
                        IsActive = true,
                        ResolvedAtUtc = null
                    };
                    dbContext.MonitoringAlerts.Add(alert);
                }
            }
            else
            {
                // Chỉ số đã an toàn: Nếu đang có alert active thì tự động giải quyết (Resolve)
                if (existingAlert != null)
                {
                    existingAlert.IsActive = false;
                    existingAlert.ResolvedAtUtc = now;
                }
            }
        }

        public async Task RunRetentionCleanupAsync(CancellationToken cancellationToken)
        {
            var retentionHours = Math.Max(1, _options.Value.RetentionHours);
            var cutoff = DateTimeOffset.UtcNow.AddHours(-retentionHours);

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                var oldRequests = await dbContext.RequestTelemetries
                    .Where(r => r.OccurredAtUtc < cutoff)
                    .Take(1000)
                    .ToListAsync(cancellationToken);

                if (oldRequests.Count > 0)
                {
                    dbContext.RequestTelemetries.RemoveRange(oldRequests);
                }

                var oldEvents = await dbContext.MonitoringEvents
                    .Where(e => e.OccurredAtUtc < cutoff)
                    .Take(1000)
                    .ToListAsync(cancellationToken);

                if (oldEvents.Count > 0)
                {
                    dbContext.MonitoringEvents.RemoveRange(oldEvents);
                }

                if (oldRequests.Count > 0 || oldEvents.Count > 0)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Dọn dẹp retention monitoring ({Requests} requests, {Events} events cũ hơn {Cutoff}).",
                        oldRequests.Count, oldEvents.Count, cutoff);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi chạy dọn dẹp retention monitoring.");
            }
        }
    }
}
