using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using EventTicketBooking.Api.Hubs;
namespace EventTicketBooking.Api.BackgroundServices;

public class SeatHoldCleanupWorker : BackgroundService
{
    private readonly ILogger<SeatHoldCleanupWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<SeatStatusHub> _hubContext;

    public SeatHoldCleanupWorker(
        ILogger<SeatHoldCleanupWorker> logger,
        IServiceProvider serviceProvider,
        IHubContext<SeatStatusHub> hubContext)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job nền quét và nhả ghế quá hạn (T-27) đã khởi động.");

        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupExpiredHoldsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong quá trình quét nhả ghế quá hạn.");
            }
        }
    }

    private async Task CleanupExpiredHoldsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

        try
        {
            var now = DateTime.UtcNow;

            // 1. Lấy danh sách SeatId thuộc các lượt giữ chỗ quá hạn
            var expiredSeatsInfo = await dbContext.SeatHold
                .Where(sh => sh.Status == "ACTIVE" && sh.ExpiresAt <= now)
                .Select(sh => new { sh.SeatId, sh.Seat.ShowtimeId })
                .ToListAsync(stoppingToken);
            
            var expiredSeatIds = expiredSeatsInfo.Select(x => x.SeatId).ToList();

            if (expiredSeatIds.Any())
            {
                // 2. Chuyển trạng thái lượt giữ chỗ sang EXPIRED (nhả ghế)
                await dbContext.SeatHold
                    .Where(sh => sh.Status == "ACTIVE" && sh.ExpiresAt <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, "EXPIRED"), stoppingToken);

                // 3. Cập nhật trạng thái các ghế tương ứng trong bảng Seats về AVAILABLE
                await dbContext.Seats
                    .Where(s => expiredSeatIds.Contains(s.Id) && s.Status == "HELD")
                    .ExecuteUpdateAsync(s => s.SetProperty(seat => seat.Status, "AVAILABLE"), stoppingToken);

                await transaction.CommitAsync(stoppingToken);

                var grouped = expiredSeatsInfo.GroupBy(x => x.ShowtimeId);
                foreach (var group in grouped)
                {
                    await _hubContext.Clients.Group(group.Key.ToString())
                        .SendAsync("SeatReleased", new { ShowtimeId = group.Key, SeatIds = group.Select(x => x.SeatId).ToList() }, stoppingToken);
                }

                _logger.LogInformation("Đã nhả thành công {Count} ghế quá hạn giữ chỗ.", expiredSeatIds.Count);
            }
            else
            {
                _logger.LogDebug("Job T-27: Không phát hiện ghế nào quá hạn.");
            }
        }
        catch (Exception)
        {
            await transaction.RollbackAsync(stoppingToken);
            throw;
        }
    }
}