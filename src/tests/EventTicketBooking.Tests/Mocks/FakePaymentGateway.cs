using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Tests.Mocks
{
    /// <summary>
    /// Fake implementation của IPaymentGateway phục vụ kiểm thử (Task T-40).
    /// Cho phép test toàn bộ business logic và flow thanh toán mà KHÔNG cần phụ thuộc hay gọi đến cổng thật.
    /// </summary>
    public class FakePaymentGateway : IPaymentGateway
    {
        public string ProviderName => "FakeGateway";

        public bool ShouldSucceed { get; set; } = true;
        public string SimulatedPaymentUrl { get; set; } = "https://fake-payment.example.com/checkout/test";
        public string SimulatedTransactionId { get; set; } = "TXN_FAKE_999";
        public string SimulatedErrorMessage { get; set; } = "Thanh toán mô phỏng thất bại.";
        public bool ShouldVerifySignature { get; set; } = true;
        public WebhookParseResult? CustomWebhookParseResult { get; set; }
        public PaymentResultDto? SimulatedQueryResult { get; set; }

        public List<PaymentCreationRequest> RecordedCreationRequests { get; } = new();

        public Task<PaymentCreationResult> CreatePaymentAsync(PaymentCreationRequest request, CancellationToken cancellationToken = default)
        {
            RecordedCreationRequests.Add(request);

            if (ShouldSucceed)
            {
                return Task.FromResult(PaymentCreationResult.CreateSuccess(SimulatedPaymentUrl, SimulatedTransactionId, null, request.OrderCode));
            }
            else
            {
                return Task.FromResult(PaymentCreationResult.CreateFailure(SimulatedErrorMessage));
            }
        }

        public bool VerifyWebhookSignature(string webhookPayload, string signature, string? webhookSecret = null)
        {
            if (string.IsNullOrWhiteSpace(signature) || signature == "INVALID_SIGNATURE")
                return false;

            return ShouldVerifySignature;
        }

        public WebhookParseResult ParseWebhookData(string webhookPayload)
        {
            if (CustomWebhookParseResult != null)
                return CustomWebhookParseResult;

            return WebhookParseResult.CreateSuccess(
                orderCode: 123456,
                amount: 500000,
                transactionId: SimulatedTransactionId,
                paidAt: DateTimeOffset.UtcNow
            );
        }

        public Task<PaymentResultDto?> QueryPaymentStatusAsync(long orderCode, CancellationToken cancellationToken = default)
        {
            if (SimulatedQueryResult != null)
                return Task.FromResult<PaymentResultDto?>(SimulatedQueryResult);

            return Task.FromResult<PaymentResultDto?>(null);
        }

        public bool RefundShouldSucceed { get; set; } = true;
        public string SimulatedRefundTransactionId { get; set; } = "REF_FAKE_123";
        public string SimulatedRefundErrorCode { get; set; } = "NETWORK_TIMEOUT";
        public string SimulatedRefundErrorMessage { get; set; } = "Mô phỏng timeout mạng.";
        public bool SimulatedRefundIsRetryable { get; set; } = true;
        public Func<RefundExecutionRequest, Task<RefundExecutionResult>>? CustomRefundHandler { get; set; }
        public Func<string?, long, Task<RefundStatusDto?>>? CustomQueryRefundHandler { get; set; }
        public List<RefundExecutionRequest> RecordedRefundRequests { get; } = new();

        public Task<RefundExecutionResult> ExecuteRefundAsync(RefundExecutionRequest request, CancellationToken cancellationToken = default)
        {
            RecordedRefundRequests.Add(request);

            if (CustomRefundHandler != null)
            {
                return CustomRefundHandler(request);
            }

            if (RefundShouldSucceed)
            {
                return Task.FromResult(RefundExecutionResult.CreateSuccess(SimulatedRefundTransactionId));
            }
            else
            {
                return Task.FromResult(RefundExecutionResult.CreateFailure(
                    SimulatedRefundErrorCode,
                    SimulatedRefundErrorMessage,
                    SimulatedRefundIsRetryable));
            }
        }

        public Task<RefundStatusDto?> QueryRefundStatusAsync(string? refundTransactionId, long orderCode, CancellationToken cancellationToken = default)
        {
            if (CustomQueryRefundHandler != null)
            {
                return CustomQueryRefundHandler(refundTransactionId, orderCode);
            }

            return Task.FromResult<RefundStatusDto?>(null);
        }

        // Story S-45: mô phỏng hoàn tiền cho kiểm thử
        public bool SupportsRefund { get; set; } = true;
        public bool ShouldRefundSucceed { get; set; } = true;
        public List<RefundCreationRequest> RecordedCreationRefundRequests { get; } = new();

        public Task<RefundCreationResult> CreateRefundAsync(RefundCreationRequest request, CancellationToken cancellationToken = default)
        {
            RecordedCreationRefundRequests.Add(request);

            if (!SupportsRefund)
                return Task.FromResult(RefundCreationResult.CreateManualRequired("Gateway không hỗ trợ hoàn tiền tự động."));

            if (ShouldRefundSucceed)
                return Task.FromResult(RefundCreationResult.CreateSuccess($"FAKE-REFUND-{request.OrderCode}"));

            return Task.FromResult(RefundCreationResult.CreateFailure(SimulatedRefundErrorMessage));
        }
    }
}
