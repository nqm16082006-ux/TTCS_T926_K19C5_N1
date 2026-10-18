using System;
using EventTicketBooking.Api.Metrics;
using EventTicketBooking.Api.Models;
using Microsoft.Extensions.Hosting;

namespace EventTicketBooking.Api.Monitoring
{
    public sealed class DatabaseAppMetrics : IAppMetrics
    {
        private readonly ITelemetryChannel _channel;
        private readonly IAppMetrics? _innerMetrics;
        private readonly string _environment;

        public DatabaseAppMetrics(
            ITelemetryChannel channel,
            IHostEnvironment? hostEnvironment = null,
            IAppMetrics? innerMetrics = null)
        {
            _channel = channel;
            _innerMetrics = innerMetrics;
            _environment = hostEnvironment?.EnvironmentName ?? "Development";
        }

        public void RecordSeatHoldConflict(string reason)
        {
            _innerMetrics?.RecordSeatHoldConflict(reason);

            _channel.TryWrite(new MonitoringEvent
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Type = "SeatConflict",
                Category = reason,
                Environment = _environment
            });
        }

        public void RecordWebhookRejected(string gateway, string reason)
        {
            _innerMetrics?.RecordWebhookRejected(gateway, reason);

            _channel.TryWrite(new MonitoringEvent
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Type = "WebhookRejected",
                Category = gateway,
                ReferenceId = reason,
                Environment = _environment
            });
        }
    }
}
