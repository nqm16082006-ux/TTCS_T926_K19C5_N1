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
    public Task<RefundExecutionResult> ExecuteRefundAsync(RefundExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request != null && !string.IsNullOrWhiteSpace(request.Reason))
        {
            if (request.Reason.Contains("FAIL_RETRY", StringComparison.OrdinalIgnoreCase) ||
                request.Reason.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
                request.Reason.Contains("NETWORK_ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(RefundExecutionResult.CreateFailure(
                    "GATEWAY_TIMEOUT",
                    "Mô phỏng: Lỗi Timeout / Mạng kết nối cổng thanh toán (Lỗi tạm thời, cho phép retry).",
                    isRetryable: true));
            }

            if (request.Reason.Contains("FAIL_PERMANENT", StringComparison.OrdinalIgnoreCase) ||
                request.Reason.Contains("NON_RETRYABLE", StringComparison.OrdinalIgnoreCase) ||
                request.Reason.Contains("ACCOUNT_FROZEN", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(RefundExecutionResult.CreateFailure(
                    "ACCOUNT_FROZEN",
                    "Mô phỏng: Tài khoản thụ hưởng của khách hàng đã bị phong tỏa (Lỗi vĩnh viễn, không retry).",
                    isRetryable: false));
            }
        }

        return Task.FromResult(RefundExecutionResult.CreateSuccess($"MOCK_REF_{request?.OrderCode}_{Guid.NewGuid():N}"));
    }

    public Task<RefundStatusDto?> QueryRefundStatusAsync(string? refundTransactionId, long orderCode, CancellationToken cancellationToken = default) =>
        Task.FromResult<RefundStatusDto?>(new RefundStatusDto { Success = true, Status = "SUCCESS", RefundTransactionId = refundTransactionId ?? $"MOCK_REF_{orderCode}" });
}
