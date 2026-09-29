using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Payment;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Abstraction cho cổng thanh toán độc lập nhà cung cấp (Task T-40).
    /// Business logic chỉ phụ thuộc vào interface này, không phụ thuộc vào SDK của bên thứ ba.
    /// </summary>
    public interface IPaymentGateway
    {
        /// <summary>
        /// Tên nhà cung cấp cổng thanh toán hiện tại (ví dụ: "PayOS", "VNPay", "FakeGateway").
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Tạo yêu cầu thanh toán và trả về link thanh toán (checkout URL / QR code).
        /// </summary>
        Task<PaymentCreationResult> CreatePaymentAsync(PaymentCreationRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Kiểm tra tính hợp lệ của chữ ký số (HMAC/Checksum) gửi kèm webhook.
        /// </summary>
        bool VerifyWebhookSignature(string webhookPayload, string signature, string? webhookSecret = null);

        /// <summary>
        /// Trích xuất và chuẩn hóa kết quả thanh toán từ payload webhook sang model chung của ứng dụng.
        /// </summary>
        WebhookParseResult ParseWebhookData(string webhookPayload);
    }
}
