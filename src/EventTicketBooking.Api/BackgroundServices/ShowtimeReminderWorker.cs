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
    /// Worker nền định kỳ quét và gửi email nhắc nhở trước 24 giờ diễn (Story S-61).
    /// </summary>
    public class ShowtimeReminderWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ShowtimeReminderWorker> _logger;

        public ShowtimeReminderWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<ShowtimeReminderWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Job nền nhắc người mua trước 24h giờ diễn (Story S-61) đã khởi động.");

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            do
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var reminderService = scope.ServiceProvider.GetRequiredService<IShowtimeReminderService>();
                    int sentCount = await reminderService.SendUpcomingShowtimeRemindersAsync(stoppingToken);

                    if (sentCount > 0)
                    {
                        _logger.LogInformation("ShowtimeReminderWorker đã gửi thành công {Count} email nhắc nhở.", sentCount);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp của ShowtimeReminderWorker.");
                }
            } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
