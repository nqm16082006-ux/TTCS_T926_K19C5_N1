using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Monitoring
{
    public interface ITelemetryChannel
    {
        bool TryWrite(RequestTelemetry telemetry);
        bool TryWrite(MonitoringEvent monitoringEvent);
        ChannelReader<RequestTelemetry> RequestReader { get; }
        ChannelReader<MonitoringEvent> EventReader { get; }
        long DroppedRequestsCount { get; }
        long DroppedEventsCount { get; }
    }
}
