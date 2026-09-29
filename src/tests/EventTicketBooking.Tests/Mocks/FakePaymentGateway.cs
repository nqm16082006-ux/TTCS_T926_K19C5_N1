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
    }
}
