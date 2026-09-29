using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Business Service xử lý thanh toán (Task T-40).
    /// Tuân thủ nguyên tắc Dependency Inversion: Chỉ phụ thuộc vào abstraction IPaymentGateway,
    /// hoàn toàn không chứa SDK, cấu hình hay DTO đặc thù của bất kỳ nhà cung cấp nào.
    /// </summary>
    public class PaymentService : IPaymentService
    {
        private readonly AppDbContext _context;
        private readonly IPaymentGateway _paymentGateway;
        private readonly ILogger<PaymentService> _logger;

        public PaymentService(
            AppDbContext context,
            IPaymentGateway paymentGateway,
            ILogger<PaymentService> logger)
        {
            _context = context;
            _paymentGateway = paymentGateway;
            _logger = logger;
        }

        public async Task<PaymentCreationResult> CreatePaymentForOrderAsync(Guid orderId, Guid userId, CancellationToken cancellationToken = default)
        {
            var order = await _context.Orders
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

            if (order == null)
            {
                return PaymentCreationResult.CreateFailure("Không tìm thấy đơn hàng.");
            }

            if (order.UserId != userId)
            {
                return PaymentCreationResult.CreateFailure("Bạn không có quyền thanh toán cho đơn hàng này.");
            }

            if (order.Status == OrderStatus.Paid)
            {
                return PaymentCreationResult.CreateFailure("Đơn hàng đã được thanh toán thành công.");
            }

            if (order.Status != OrderStatus.Pending)
            {
                return PaymentCreationResult.CreateFailure($"Đơn hàng không ở trạng thái chờ thanh toán (Trạng thái: {order.Status}).");
            }

            if (order.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                order.Status = OrderStatus.Expired;
                await _context.SaveChangesAsync(cancellationToken);
                return PaymentCreationResult.CreateFailure("Đơn hàng đã hết hạn thanh toán.");
            }

            if (order.TotalAmount <= 0)
            {
                order.CalculateTotal();
            }

            if (order.TotalAmount <= 0)
            {
                return PaymentCreationResult.CreateFailure("Số tiền thanh toán của đơn hàng không hợp lệ.");
            }

            // T-41: Kiểm tra giao dịch thanh toán hiện tại của Order (Idempotency - chống duplicate giao dịch)
            var existingTransaction = await _context.PaymentTransactions
                .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);

            if (existingTransaction != null)
            {
                if (existingTransaction.Status == "PAID")
                {
                    return PaymentCreationResult.CreateFailure("Đơn hàng đã được thanh toán thành công.");
                }

                _logger.LogInformation("Tái sử dụng giao dịch thanh toán hiện có cho đơn hàng {OrderId}, OrderCode: {OrderCode}", order.Id, existingTransaction.OrderCode);

                return PaymentCreationResult.CreateSuccess(
                    existingTransaction.PaymentUrl ?? string.Empty,
                    existingTransaction.TransactionId,
                    null,
                    existingTransaction.OrderCode
                );
            }

            // Tạo mã số đơn hàng dạng số (thích hợp với các cổng như PayOS/VNPay)
            long numericOrderCode = Math.Abs(BitConverter.ToInt64(order.Id.ToByteArray(), 0) % 9000000000L) + 1000000000L;

            var request = new PaymentCreationRequest
            {
                OrderId = order.Id.ToString(),
                OrderCode = numericOrderCode,
                Amount = order.TotalAmount,
                Description = $"Ve xem #{order.Id.ToString()[..8]}",
                Items = order.OrderItems.Select(oi => new PaymentItemDto
                {
                    Name = oi.Seat != null ? $"Ghe {oi.Seat.Row}{oi.Seat.SeatNumber}" : "Ve su kien",
                    Quantity = 1,
                    Price = oi.Price
                }).ToList()
            };

            _logger.LogInformation("Đang gọi cổng thanh toán {Provider} cho đơn hàng {OrderId}", _paymentGateway.ProviderName, order.Id);

            // Giao tiếp qua abstraction IPaymentGateway (Task T-40 & T-41)
            var gatewayResult = await _paymentGateway.CreatePaymentAsync(request, cancellationToken);
            if (!gatewayResult.Success)
            {
                return gatewayResult;
            }

            gatewayResult.OrderCode = numericOrderCode;

            // Lưu giao dịch thanh toán vào cơ sở dữ liệu với unique constraint trên OrderId
            try
            {
                var transaction = new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    OrderCode = numericOrderCode,
                    Amount = order.TotalAmount,
                    PaymentUrl = gatewayResult.PaymentUrl,
                    TransactionId = gatewayResult.TransactionId,
                    Status = "PENDING",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                _context.PaymentTransactions.Add(transaction);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Xử lý race condition (người dùng double click hoặc mở 2 tab thanh toán đồng thời)
                _context.ChangeTracker.Clear();
                var concurrentTx = await _context.PaymentTransactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);

                if (concurrentTx != null)
                {
                    return PaymentCreationResult.CreateSuccess(
                        concurrentTx.PaymentUrl ?? string.Empty,
                        concurrentTx.TransactionId,
                        null,
                        concurrentTx.OrderCode
                    );
                }
            }

            return gatewayResult;
        }

        public async Task<bool> ProcessPaymentWebhookAsync(string webhookPayload, string signature, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Nhận webhook thanh toán từ {Provider}", _paymentGateway.ProviderName);

            // 1. Xác thực chữ ký qua gateway abstraction
            bool isSignatureValid = _paymentGateway.VerifyWebhookSignature(webhookPayload, signature);
            if (!isSignatureValid)
            {
                _logger.LogWarning("Chữ ký webhook từ {Provider} không hợp lệ", _paymentGateway.ProviderName);
                return false;
            }

            // 2. Parse dữ liệu qua gateway abstraction
            var parseResult = _paymentGateway.ParseWebhookData(webhookPayload);
            if (!parseResult.IsValid)
            {
                _logger.LogWarning("Dữ liệu webhook không hợp lệ: {Error}", parseResult.ErrorMessage);
                return false;
            }

            // 3. Cập nhật trạng thái đơn hàng trong DB
            Order? order = null;
            if (!string.IsNullOrEmpty(parseResult.OrderId) && Guid.TryParse(parseResult.OrderId, out var orderGuid))
            {
                order = await _context.Orders
                    .Include(o => o.OrderItems)
                    .FirstOrDefaultAsync(o => o.Id == orderGuid, cancellationToken);
            }

            if (order == null && parseResult.OrderCode.HasValue)
            {
                // Tìm kiếm theo bảng PaymentTransaction
                var paymentTx = await _context.PaymentTransactions
                    .Include(pt => pt.Order)
                        .ThenInclude(o => o.OrderItems)
                    .FirstOrDefaultAsync(pt => pt.OrderCode == parseResult.OrderCode.Value, cancellationToken);

                if (paymentTx != null)
                {
                    order = paymentTx.Order;
                }
            }

            if (order == null && parseResult.OrderCode.HasValue)
            {
                // Fallback tìm kiếm theo số tiền và trạng thái Pending
                order = await _context.Orders
                    .Include(o => o.OrderItems)
                    .FirstOrDefaultAsync(o => o.Status == OrderStatus.Pending && o.TotalAmount == parseResult.Amount, cancellationToken);
            }

            if (order == null)
            {
                _logger.LogWarning("Không tìm thấy đơn hàng tương ứng với webhook. OrderCode: {OrderCode}, OrderId: {OrderId}", parseResult.OrderCode, parseResult.OrderId);
                return false;
            }

            if (parseResult.Status == PaymentStatus.Success)
            {
                order.Status = OrderStatus.Paid;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                // Cập nhật trạng thái PaymentTransaction
                var paymentTx = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);

                if (paymentTx != null)
                {
                    paymentTx.Status = "PAID";
                    paymentTx.UpdatedAt = DateTimeOffset.UtcNow;
                }

                // Cập nhật trạng thái các giữ chỗ liên quan sang hoàn tất
                var seatIds = order.OrderItems.Select(oi => oi.SeatId).ToList();
                var holds = await _context.SeatHold
                    .Where(sh => seatIds.Contains(sh.SeatId) && sh.UserId == order.UserId)
                    .ToListAsync(cancellationToken);

                foreach (var hold in holds)
                {
                    hold.Status = "PAID";
                }

                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Đơn hàng {OrderId} đã được thanh toán thành công", order.Id);
            }
            else if (parseResult.Status == PaymentStatus.Cancelled || parseResult.Status == PaymentStatus.Failed)
            {
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                var paymentTx = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);

                if (paymentTx != null)
                {
                    paymentTx.Status = parseResult.Status == PaymentStatus.Cancelled ? "CANCELLED" : "FAILED";
                    paymentTx.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Đơn hàng {OrderId} thanh toán thất bại/đã bị hủy", order.Id);
            }

            return true;
        }
    }
}
