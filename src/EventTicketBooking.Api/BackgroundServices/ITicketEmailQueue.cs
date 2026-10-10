using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventTicketBooking.Api.BackgroundServices
{
    public interface ITicketEmailQueue
    {
        ValueTask EnqueueAsync(Guid orderId, CancellationToken cancellationToken = default);
        ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken);
    }
}
