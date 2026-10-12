using Prometheus;

namespace EventTicketBooking.Api.Metrics;

public sealed class PrometheusAppMetrics : IAppMetrics
{
    private readonly Counter _seatHoldConflictsCounter;
    private readonly Counter _paymentWebhooksRejectedCounter;

    public PrometheusAppMetrics(CollectorRegistry? registry = null)
    {
        var factory = registry != null ? global::Prometheus.Metrics.WithCustomRegistry(registry) : global::Prometheus.Metrics.DefaultFactory;

        _seatHoldConflictsCounter = factory.CreateCounter(
            "seat_hold_conflicts_total",
            "Tong so lan yeu cau giu ghe bi tu choi do tranh chap ghe voi nguoi khac.",
            new CounterConfiguration
            {
                LabelNames = new[] { "reason" }
            });

        _paymentWebhooksRejectedCounter = factory.CreateCounter(
            "payment_webhooks_rejected_total",
            "Tong so lan webhook thanh toan bi tu choi tiep nhan.",
            new CounterConfiguration
            {
                LabelNames = new[] { "gateway", "reason" }
            });
    }

    public void RecordSeatHoldConflict(string reason)
    {
        _seatHoldConflictsCounter.WithLabels(reason).Inc();
    }

    public void RecordWebhookRejected(string gateway, string reason)
    {
        _paymentWebhooksRejectedCounter.WithLabels(gateway, reason).Inc();
    }
}
