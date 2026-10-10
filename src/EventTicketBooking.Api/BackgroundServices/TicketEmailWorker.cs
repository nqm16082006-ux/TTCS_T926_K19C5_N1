using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace EventTicketBooking.Api.BackgroundServices
{
    public class TicketEmailWorker : BackgroundService
    {
        private readonly ITicketEmailQueue _queue;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TicketEmailWorker> _logger;

        public TicketEmailWorker(
            ITicketEmailQueue queue,
            IServiceProvider serviceProvider,
            ILogger<TicketEmailWorker> logger)
        {
            _queue = queue;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var orderId = await _queue.DequeueAsync(stoppingToken);

                    _ = ProcessOrderEmailAsync(orderId, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Tránh lỗi khi ứng dụng shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp chính của TicketEmailWorker.");
                }
            }
        }

        private async Task ProcessOrderEmailAsync(Guid orderId, CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

            var order = await context.Orders
                .Include(o => o.User)
                .Include(o => o.Showtime)
                    .ThenInclude(s => s.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                .FirstOrDefaultAsync(o => o.Id == orderId, stoppingToken);

            if (order == null)
            {
                _logger.LogWarning("Không tìm thấy đơn hàng {OrderId}", orderId);
                return;
            }

            if (order.Status != OrderStatus.Paid)
            {
                _logger.LogWarning("Đơn hàng {OrderId} chưa thanh toán (Status: {Status})", orderId, order.Status);
                return;
            }

            if (!order.OrderItems.Any())
            {
                _logger.LogWarning("Đơn hàng {OrderId} không có vé nào.", orderId);
                return;
            }

            var vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Ho_Chi_Minh");
            var showtimeLocal = TimeZoneInfo.ConvertTimeFromUtc(order.Showtime.StartTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(order.Showtime.StartTime, DateTimeKind.Utc) : order.Showtime.StartTime.ToUniversalTime(), vnTimeZone).ToString("HH:mm dd/MM/yyyy");

            foreach (var item in order.OrderItems)
            {
                await ProcessSingleTicketEmailWithRetryAsync(order, item, showtimeLocal, emailService, context, stoppingToken);
            }
        }

        private async Task ProcessSingleTicketEmailWithRetryAsync(Order order, OrderItem item, string showtimeLocal, IEmailService emailService, AppDbContext context, CancellationToken stoppingToken)
        {
            int maxRetries = 3;
            int currentRetry = 0;
            TimeSpan[] delays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };

            while (currentRetry <= maxRetries && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    byte[] qrCodeBytes = GenerateQRCode(item.Id.ToString());
                    string seatName = item.Seat != null ? $"{item.Seat.Row}{item.Seat.SeatNumber}" : "Không ghế";

                    await emailService.SendTicketEmailAsync(
                        toEmail: order.User.Email,
                        toName: order.User.FullName ?? order.User.Username,
                        eventTitle: order.Showtime.Event.Title,
                        location: order.Showtime.Event.Location,
                        showtime: showtimeLocal,
                        seatNames: new[] { seatName },
                        qrCodeBytes: qrCodeBytes
                    );

                    return; // Nếu thành công thì dừng retry
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi gửi email vé {TicketId} cho Order {OrderId}. Lần thử {Attempt}.", item.Id, order.Id, currentRetry + 1);

                    if (currentRetry < maxRetries)
                    {
                        await Task.Delay(delays[currentRetry], stoppingToken);
                        currentRetry++;
                    }
                    else
                    {
                        // Đã hết lần thử, ghi nhận failure
                        await LogFailureAsync(context, order.Id, order.User.Email, ex.Message, currentRetry + 1, stoppingToken);
                        break;
                    }
                }
            }
        }

        private byte[] GenerateQRCode(string payload)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(qrCodeData);
            return qrCode.GetGraphic(20);
        }

        private async Task LogFailureAsync(AppDbContext context, Guid orderId, string targetEmail, string errorMessage, int attempts, CancellationToken stoppingToken)
        {
            try
            {
                var log = new EmailFailureLog
                {
                    OrderId = orderId,
                    TargetEmail = targetEmail ?? "Unknown",
                    ErrorMessage = errorMessage,
                    Attempts = attempts,
                    Resolved = false,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                context.EmailFailureLogs.Add(log);
                await context.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu EmailFailureLog cho Order {OrderId}", orderId);
            }
        }
    }
}
