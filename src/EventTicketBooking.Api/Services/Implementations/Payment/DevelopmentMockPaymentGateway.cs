using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Api.Services.Implementations.Payment;

// Registered only for explicitly configured Development mock payments.
public sealed class DevelopmentMockPaymentGateway : IPaymentGateway
{
    public string ProviderName => "Mock";
    public Task<PaymentCreationResult> CreatePaymentAsync(PaymentCreationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(PaymentCreationResult.CreateSuccess(
            $"/mock-payment.html?orderId={Uri.EscapeDataString(request.OrderId)}", null, null, request.OrderCode));
    public bool VerifyWebhookSignature(string webhookPayload, string signature, string? webhookSecret = null) => false;
    public WebhookParseResult ParseWebhookData(string webhookPayload) => WebhookParseResult.CreateFailure("Mock webhooks are disabled.");
    public Task<PaymentResultDto?> QueryPaymentStatusAsync(long orderCode, CancellationToken cancellationToken = default) => Task.FromResult<PaymentResultDto?>(null);
}
