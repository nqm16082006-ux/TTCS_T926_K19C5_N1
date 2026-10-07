using System;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.BackgroundServices
{
    public class ExpiredOrderCleanupWorker : BackgroundService
    {
        private readonly ILogger<ExpiredOrderCleanupWorker> _logger;
        private readonly IServiceProvider _serviceProvider;

        public ExpiredOrderCleanupWorker(
            ILogger<ExpiredOrderCleanupWorker> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Job nền quét và huỷ đơn quá hạn (T-52) đã khởi động.");

            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cleanupService = scope.ServiceProvider.GetRequiredService<IExpiredOrderCleanupService>();
                    await cleanupService.CleanupExpiredOrdersAsync(null, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong quá trình thực thi Job nền quét huỷ đơn quá hạn (T-52).");
                }
            }
        }
    }
}
