using System;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.BackgroundServices
{
    /// <summary>
    /// Worker chạy nền định kỳ quét và thực thi các lệnh hoàn tiền cần retry
    /// và gửi lại thông báo kế toán nếu trước đó bị lỗi (Jira Story S-48).
    /// </summary>
    public class RefundProcessingWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly RefundOptions _options;
        private readonly ILogger<RefundProcessingWorker> _logger;

        public RefundProcessingWorker(
            IServiceProvider serviceProvider,
            IOptions<RefundOptions> options,
            ILogger<RefundProcessingWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Job nền xử lý hoàn tiền tự động và retry (Story S-48) đã khởi động. Chu kỳ: {Interval}s.",
                _options.WorkerIntervalSeconds);

            int intervalSeconds = _options.WorkerIntervalSeconds > 0 ? _options.WorkerIntervalSeconds : 30;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var refundService = scope.ServiceProvider.GetRequiredService<IRefundService>();

                    // 1. Quét và xử lý các refund đến lịch retry
                    int processedCount = await refundService.ProcessPendingAndRetryableRefundsAsync(stoppingToken);
                    if (processedCount > 0)
                    {
                        _logger.LogInformation("Job nền S-48 đã xử lý {Count} lệnh hoàn tiền đến hạn.", processedCount);
                    }

                    // 2. Thử gửi lại thông báo cho kế toán nếu có lệnh cần xử lý thủ công bị lỗi gửi email trước đó
                    int retriedNotifications = await refundService.RetryFailedAccountingNotificationsAsync(stoppingToken);
                    if (retriedNotifications > 0)
                    {
                        _logger.LogInformation("Job nền S-48 đã gửi lại thành công {Count} thông báo kế toán.", retriedNotifications);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Dừng bình thường khi ứng dụng shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp của RefundProcessingWorker (Story S-48).");
                }
            }
        }
    }
}
