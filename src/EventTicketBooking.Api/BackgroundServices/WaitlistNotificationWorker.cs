using System;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.BackgroundServices
{
    /// <summary>
    /// Worker nền định kỳ quét kiểm tra ghế về kho và thông báo danh sách chờ theo thứ tự đăng ký (Story S-59).
    /// </summary>
    public class WaitlistNotificationWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WaitlistNotificationWorker> _logger;

        public WaitlistNotificationWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<WaitlistNotificationWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Job nền quét gửi email danh sách chờ khi có vé về kho (Story S-59) đã khởi động.");

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            do
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var waitlistService = scope.ServiceProvider.GetRequiredService<IWaitlistService>();
                    int notifiedCount = await waitlistService.ProcessAllPendingWaitlistsAsync(stoppingToken);

                    if (notifiedCount > 0)
                    {
                        _logger.LogInformation("WaitlistNotificationWorker đã gửi thông báo vé mới thành công cho {Count} khách chờ.", notifiedCount);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp WaitlistNotificationWorker.");
                }
            } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
