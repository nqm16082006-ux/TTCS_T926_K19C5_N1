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

        /// <summary>
        /// Handler nhận kết quả thanh toán dùng chung (qua webhook và qua trang query về) (Task T-42).
        /// Đối chiếu số tiền, đồng thời cập nhật trạng thái đơn, đánh dấu ghế đã bán,
        /// và giải phóng/chuyển đổi giữ chỗ trong một database transaction duy nhất.
        /// </summary>
        Task<PaymentExecutionResult> HandlePaymentResultAsync(PaymentResultDto result, CancellationToken cancellationToken = default);

        /// <summary>
        /// Xác minh và xử lý kết quả thanh toán từ luồng return/query của khách hàng (Task T-42).
        /// Không tin tưởng query string từ client, thực hiện xác minh server-side qua gateway.
        /// </summary>
        Task<PaymentExecutionResult> VerifyAndProcessPaymentReturnAsync(PaymentReturnQueryDto query, CancellationToken cancellationToken = default);
    }
}
