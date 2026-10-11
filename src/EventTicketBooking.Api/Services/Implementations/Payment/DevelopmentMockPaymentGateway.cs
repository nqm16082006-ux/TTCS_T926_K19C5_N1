using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Services.Implementations.Payment;

// Registered only for explicitly configured Development mock payments.
public sealed class DevelopmentMockPaymentGateway : IPaymentGateway
{
    private readonly MockPaymentOptions _mockOptions;

    public DevelopmentMockPaymentGateway(IOptions<MockPaymentOptions>? mockOptions = null)
    {
        _mockOptions = mockOptions?.Value ?? new MockPaymentOptions();
    }

    public string ProviderName => "Mock";
    public Task<PaymentCreationResult> CreatePaymentAsync(PaymentCreationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(PaymentCreationResult.CreateSuccess(
            $"/mock-payment.html?orderId={Uri.EscapeDataString(request.OrderId)}", null, null, request.OrderCode));
    public bool VerifyWebhookSignature(string webhookPayload, string signature, string? webhookSecret = null) => false;
    public WebhookParseResult ParseWebhookData(string webhookPayload) => WebhookParseResult.CreateFailure("Mock webhooks are disabled.");
    public Task<PaymentResultDto?> QueryPaymentStatusAsync(long orderCode, CancellationToken cancellationToken = default) => Task.FromResult<PaymentResultDto?>(null);

    /// <summary>
    /// Story S-45: Mock gateway mô phỏng kết quả hoàn tiền để demo/test.
    /// Kết quả mô phỏng đọc từ cấu hình "PaymentSettings:Mock:RefundOutcome" ("Succeeded" mặc định hoặc "Failed").
    /// </summary>
    public bool SupportsRefund => true;

    public Task<RefundCreationResult> CreateRefundAsync(RefundCreationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || request.Amount <= 0)
        {
            return Task.FromResult(RefundCreationResult.CreateFailure("Yêu cầu hoàn tiền không hợp lệ."));
        }

        bool simulateFailure = string.Equals(_mockOptions.RefundOutcome, "Failed", StringComparison.OrdinalIgnoreCase);
        if (simulateFailure)
        {
            return Task.FromResult(RefundCreationResult.CreateFailure("Mock gateway mô phỏng hoàn tiền thất bại (RefundOutcome=Failed)."));
        }

        return Task.FromResult(RefundCreationResult.CreateSuccess($"MOCK-REFUND-{request.OrderCode}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
    }
}
