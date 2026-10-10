using System;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.BackgroundServices
{
    /// <summary>
    /// Worker định kỳ kiểm tra dọn dẹp các phiên phòng chờ bỏ dở và cấp lượt cho người tiếp theo (Story S-40).
    /// </summary>
    public class WaitingRoomCleanupWorker : BackgroundService
    {
        private readonly IWaitingRoomService _waitingRoomService;
        private readonly ILogger<WaitingRoomCleanupWorker> _logger;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);

        public WaitingRoomCleanupWorker(
            IWaitingRoomService waitingRoomService,
            ILogger<WaitingRoomCleanupWorker> logger)
        {
            _waitingRoomService = waitingRoomService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("WaitingRoomCleanupWorker đã khởi động.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _waitingRoomService.CleanupStaleSessions();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi thực hiện dọn dẹp phòng chờ.");
                }

                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("WaitingRoomCleanupWorker đang dừng.");
        }
    }
}
