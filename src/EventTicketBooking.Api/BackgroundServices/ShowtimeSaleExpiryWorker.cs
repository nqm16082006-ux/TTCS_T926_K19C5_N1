using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
namespace EventTicketBooking.Api.BackgroundServices;

public class ShowtimeSaleExpiryWorker(IServiceScopeFactory scopes, ILogger<ShowtimeSaleExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var expired = await db.Showtimes.Where(s => s.Status == ShowtimeStatus.OnSale &&
                    ((s.SalesEndTime ?? s.StartTime) <= now || s.EndTime <= now)).ToListAsync(stoppingToken);
                foreach (var showtime in expired) showtime.ChangeStatus(ShowtimeStatus.Closed);
                if (expired.Count > 0) await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Không thể cập nhật suất diễn hết hạn bán vé."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
