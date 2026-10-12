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

        /// <summary>
        /// Truy vấn trạng thái thanh toán trực tiếp từ cổng thanh toán (Task T-42).
        /// Phục vụ đối soát server-side, không tin tưởng dữ liệu client tự gửi lên.
        /// </summary>
        Task<PaymentResultDto?> QueryPaymentStatusAsync(long orderCode, CancellationToken cancellationToken = default);

        /// <summary>
        /// Thực thi lệnh hoàn tiền qua cổng thanh toán (Jira Story S-48).
        /// </summary>
        Task<RefundExecutionResult> ExecuteRefundAsync(RefundExecutionRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Truy vấn trạng thái hoàn tiền trực tiếp từ cổng thanh toán (Jira Story S-48).
        /// Phục vụ đối soát idempotency trước khi retry để tránh hoàn tiền trùng lặp.
        /// </summary>
        Task<RefundStatusDto?> QueryRefundStatusAsync(string? refundTransactionId, long orderCode, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cổng hiện tại có hỗ trợ hoàn tiền tự động không (Story S-45).
        /// payOS theo tài liệu công bố không có API hoàn tiền → false.
        /// </summary>
        bool SupportsRefund { get; }

        /// <summary>
        /// Gửi yêu cầu hoàn tiền tới cổng thanh toán (Story S-45).
        /// Chỉ gọi khi SupportsRefund = true; kết quả không thành công không được ghi nhận là đã hoàn tiền.
        /// </summary>
        Task<RefundCreationResult> CreateRefundAsync(RefundCreationRequest request, CancellationToken cancellationToken = default);
    }
}
