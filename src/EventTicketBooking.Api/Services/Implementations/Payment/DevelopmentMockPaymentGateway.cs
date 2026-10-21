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

    /// <summary>
    /// Story S-45: Mock gateway mô phỏng kết quả hoàn tiền để demo/test.
    /// Kết quả mô phỏng đọc từ cấu hình "PaymentSettings:Mock:RefundOutcome" ("Succeeded" mặc định hoặc "Failed").
    /// </summary>
    public bool SupportsRefund => true;

    public Task<RefundCreationResult> CreateRefundAsync(RefundCreationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || request.Amount <= 0)
        {
            return Task.FromResult(RefundCreationResult.CreateFailure("Yêu cầu hoàn tiền không hợp lệ.", "INVALID_REQUEST", false));
        }

        bool simulateFailure = request.SimulateFailure
            || string.Equals(_mockOptions.RefundOutcome, "Failed", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(request.Description) && (
                request.Description.Contains("FAIL_RETRY", StringComparison.OrdinalIgnoreCase) ||
                request.Description.Contains("SIMULATE", StringComparison.OrdinalIgnoreCase) ||
                request.Description.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)));

        if (simulateFailure)
        {
            return Task.FromResult(RefundCreationResult.CreateFailure(
                "Mô phỏng: Cổng thanh toán phản hồi lỗi Timeout kết nối (Lỗi tạm thời, cho phép retry).",
                "GATEWAY_TIMEOUT",
                isRetryable: true));
        }

        return Task.FromResult(RefundCreationResult.CreateSuccess($"MOCK-REFUND-{request.OrderCode}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
    }
}
