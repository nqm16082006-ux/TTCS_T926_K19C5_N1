using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EventTicketBooking.Api.BackgroundServices
{
    public class TicketEmailQueue : ITicketEmailQueue
    {
        private readonly Channel<Guid> _queue;

        public TicketEmailQueue()
        {
            var options = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _queue = Channel.CreateBounded<Guid>(options);
        }

        public async ValueTask EnqueueAsync(Guid orderId, CancellationToken cancellationToken = default)
        {
            await _queue.Writer.WriteAsync(orderId, cancellationToken);
        }

        public async ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
