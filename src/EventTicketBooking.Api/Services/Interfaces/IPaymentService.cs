using System;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Payment;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface IPaymentService
    {
        /// <summary>
        /// Tạo link thanh toán cho đơn hàng.
        /// Business service chỉ phụ thuộc vào IPaymentGateway để gọi cổng thanh toán.
        /// </summary>
        Task<PaymentCreationResult> CreatePaymentForOrderAsync(Guid orderId, Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Tiếp nhận và xử lý webhook từ cổng thanh toán.
        /// Xác thực chữ ký số và cập nhật trạng thái đơn hàng.
        /// </summary>
        Task<bool> ProcessPaymentWebhookAsync(string webhookPayload, string signature, CancellationToken cancellationToken = default);
    }
}
