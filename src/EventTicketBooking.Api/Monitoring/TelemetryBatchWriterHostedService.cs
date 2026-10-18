using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Monitoring
{
    public class TelemetryBatchWriterHostedService : BackgroundService
    {
        private const int BatchSizeThreshold = 100;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

        private readonly ITelemetryChannel _channel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TelemetryBatchWriterHostedService> _logger;

        public TelemetryBatchWriterHostedService(
            ITelemetryChannel channel,
            IServiceScopeFactory scopeFactory,
            ILogger<TelemetryBatchWriterHostedService> logger)
        {
            _channel = channel;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Khởi động dịch vụ ghi hàng loạt Telemetry vào PostgreSQL (S-43).");

            var requestBuffer = new List<RequestTelemetry>(BatchSizeThreshold);
            var eventBuffer = new List<MonitoringEvent>(BatchSizeThreshold);
            var nextFlushTime = DateTime.UtcNow + FlushInterval;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Drain up to BatchSizeThreshold requests non-blocking
                    while (requestBuffer.Count < BatchSizeThreshold && _channel.RequestReader.TryRead(out var req))
                    {
                        requestBuffer.Add(req);
                    }

                    // Drain up to BatchSizeThreshold events non-blocking
                    while (eventBuffer.Count < BatchSizeThreshold && _channel.EventReader.TryRead(out var ev))
                    {
                        eventBuffer.Add(ev);
                    }

                    bool shouldFlush = (requestBuffer.Count >= BatchSizeThreshold || eventBuffer.Count >= BatchSizeThreshold)
                                       || (DateTime.UtcNow >= nextFlushTime && (requestBuffer.Count > 0 || eventBuffer.Count > 0));

                    if (shouldFlush)
                    {
                        await FlushBatchAsync(requestBuffer, eventBuffer, stoppingToken);
                        requestBuffer.Clear();
                        eventBuffer.Clear();
                        nextFlushTime = DateTime.UtcNow + FlushInterval;
                    }

                    // Wait a short interval or until data is available
                    if (requestBuffer.Count == 0 && eventBuffer.Count == 0)
                    {
                        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        delayCts.CancelAfter(500);
                        try
                        {
                            await _channel.RequestReader.WaitToReadAsync(delayCts.Token);
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            // Timeout expired, check flush interval again
                        }
                    }
                    else
                    {
                        await Task.Delay(50, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp thu thập telemetry.");
                    await Task.Delay(1000, stoppingToken);
                }
            }

            // Flush remaining items on shutdown
            try
            {
                while (_channel.RequestReader.TryRead(out var req)) requestBuffer.Add(req);
                while (_channel.EventReader.TryRead(out var ev)) eventBuffer.Add(ev);
                if (requestBuffer.Count > 0 || eventBuffer.Count > 0)
                {
                    await FlushBatchAsync(requestBuffer, eventBuffer, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi xả dữ liệu telemetry còn lại trong lúc dừng dịch vụ.");
            }
        }

        private async Task FlushBatchAsync(
            List<RequestTelemetry> requests,
            List<MonitoringEvent> events,
            CancellationToken cancellationToken)
        {
            if (requests.Count == 0 && events.Count == 0) return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                if (requests.Count > 0)
                {
                    await dbContext.RequestTelemetries.AddRangeAsync(requests, cancellationToken);
                }

                if (events.Count > 0)
                {
                    await dbContext.MonitoringEvents.AddRangeAsync(events, cancellationToken);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Đã lưu {RequestCount} requests và {EventCount} events vào PostgreSQL.", requests.Count, events.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể lưu batch telemetry vào cơ sở dữ liệu ({RequestCount} requests, {EventCount} events). Tiếp tục vận hành.",
                    requests.Count, events.Count);
            }
        }
    }
}
