namespace EventTicketBooking.Api.Metrics;

public interface IAppMetrics
{
    void RecordSeatHoldConflict(string reason);
    void RecordWebhookRejected(string gateway, string reason);
}
