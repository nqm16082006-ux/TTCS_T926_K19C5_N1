using System.Threading;
using System.Threading.Channels;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Monitoring
{
    public sealed class TelemetryChannel : ITelemetryChannel
    {
        private readonly Channel<RequestTelemetry> _requestChannel;
        private readonly Channel<MonitoringEvent> _eventChannel;
        private long _droppedRequestsCount;
        private long _droppedEventsCount;

        public TelemetryChannel(int capacity = 10000)
        {
            var requestOptions = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            };
            _requestChannel = Channel.CreateBounded<RequestTelemetry>(requestOptions, OnRequestDropped);

            var eventOptions = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            };
            _eventChannel = Channel.CreateBounded<MonitoringEvent>(eventOptions, OnEventDropped);
        }

        public ChannelReader<RequestTelemetry> RequestReader => _requestChannel.Reader;

        public ChannelReader<MonitoringEvent> EventReader => _eventChannel.Reader;

        public long DroppedRequestsCount => Interlocked.Read(ref _droppedRequestsCount);

        public long DroppedEventsCount => Interlocked.Read(ref _droppedEventsCount);

        public bool TryWrite(RequestTelemetry telemetry)
        {
            return _requestChannel.Writer.TryWrite(telemetry);
        }

        public bool TryWrite(MonitoringEvent monitoringEvent)
        {
            return _eventChannel.Writer.TryWrite(monitoringEvent);
        }

        private void OnRequestDropped(RequestTelemetry item)
        {
            Interlocked.Increment(ref _droppedRequestsCount);
        }

        private void OnEventDropped(MonitoringEvent item)
        {
            Interlocked.Increment(ref _droppedEventsCount);
        }
    }
}
