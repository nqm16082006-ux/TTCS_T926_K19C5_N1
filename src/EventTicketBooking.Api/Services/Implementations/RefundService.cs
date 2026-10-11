using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Service xử lý nghiệp vụ hoàn tiền dùng chung (Story S-45 & S-47).
    /// Tuân thủ quy ước trạng thái Refund (Requested -> Succeeded / ManualPending / Failed)
    /// và ghi nhận OrderAuditLog tương ứng.
    /// </summary>
    public class RefundService : IRefundService
    {
        private readonly AppDbContext _context;
        private readonly IOrderAuditService _orderAuditService;
        private readonly IPaymentGateway? _paymentGateway;
        private readonly ILogger<RefundService> _logger;

        public RefundService(
            AppDbContext context,
            IOrderAuditService orderAuditService,
            IPaymentGateway? paymentGateway = null,
            ILogger<RefundService>? logger = null)
        {
            _context = context;
            _orderAuditService = orderAuditService;
            _paymentGateway = paymentGateway;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RefundService>.Instance;
        }

        public async Task<RefundExecutionResult> ProcessOrderRefundAsync(
            Guid orderId,
            int amount,
            string cancelReason,
            string actorType,
            string actorName,
            Guid? actorId = null,
            string? description = null,
            CancellationToken cancellationToken = default)
        {
            // 1. Kiểm tra đơn hàng
            var order = await _context.Orders
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

            if (order == null)
            {
                return RefundExecutionResult.CreateFailed(orderId, null, amount, "ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");
            }

            // 2. Chống hoàn tiền kép / Idempotency: Kiểm tra Refund hiện hữu
            var existingRefund = await _context.Refunds
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OrderId == orderId, cancellationToken);

            if (existingRefund != null)
            {
                _logger.LogInformation("Đơn hàng {OrderId} đã có bản ghi hoàn tiền {RefundId} với trạng thái {Status}. Bỏ qua hoàn tiền lần hai.",
                    orderId, existingRefund.Id, existingRefund.Status);
                return RefundExecutionResult.CreateSkipped(orderId, existingRefund.Id,
                    $"Đơn hàng đã có yêu cầu hoàn tiền trước đó (Trạng thái: {existingRefund.Status}). Không tạo Refund trùng.");
            }

            // Đảm bảo trạng thái đơn hàng là Cancelled và cập nhật lý do huỷ
            if (order.Status != OrderStatus.Cancelled)
            {
                order.Status = OrderStatus.Cancelled;
                order.CancelReason = cancelReason;
                order.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (string.IsNullOrEmpty(order.CancelReason))
            {
                order.CancelReason = cancelReason;
                order.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // 3. Pha 1: Tạo bản ghi Refund với trạng thái Requested và lưu vào DB
            var now = DateTimeOffset.UtcNow;
            var newRefund = new Refund
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Amount = amount,
                Status = "Requested",
                CreatedAt = now,
                UpdatedAt = now
            };

            try
            {
                _context.Refunds.Add(newRefund);

                _orderAuditService.Record(
                    order.Id,
                    "REFUND",
                    newRefund.Id.ToString(),
                    "REFUND_REQUESTED",
                    null,
                    "Requested",
                    actorType,
                    actorName,
                    actorId,
                    description ?? $"Tạo yêu cầu hoàn tiền {amount} VND do {cancelReason}.",
                    now);

                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Đơn hàng {OrderId} bị xung đột bản ghi Refund đồng thời (unique constraint). Bỏ qua hoàn tiền lặp.", orderId);
                var concurrentRefund = await _context.Refunds
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.OrderId == orderId, cancellationToken);
                return RefundExecutionResult.CreateSkipped(orderId, concurrentRefund?.Id, "Đơn hàng đã được tạo hoàn tiền đồng thời bởi tiến trình khác.");
            }

            // 4. Pha 2: Gọi cổng hoàn tiền NGOÀI transaction DB (không giữ lock DB khi chờ mạng)
            RefundCreationResult gatewayResult;
            bool gatewaySupportsRefund = _paymentGateway != null && _paymentGateway.SupportsRefund;

            if (_paymentGateway == null || !gatewaySupportsRefund)
            {
                gatewayResult = RefundCreationResult.CreateManualRequired(
                    "Cổng thanh toán hiện tại không hỗ trợ hoàn tiền tự động. Yêu cầu được ghi nhận chờ xử lý thủ công.");
            }
            else
            {
                try
                {
                    var paymentTx = await _context.PaymentTransactions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(pt => pt.OrderId == orderId, cancellationToken);

                    gatewayResult = await _paymentGateway.CreateRefundAsync(new RefundCreationRequest
                    {
                        OrderId = orderId,
                        OrderCode = paymentTx?.OrderCode ?? 0,
                        Amount = newRefund.Amount,
                        Description = description ?? $"Hoan tien don {orderId}"
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi ngoại lệ khi gọi cổng hoàn tiền cho đơn hàng {OrderId}", orderId);
                    gatewayResult = RefundCreationResult.CreateFailure(
                        "Không liên lạc được với cổng thanh toán. Yêu cầu hoàn tiền đang chờ xử lý.");
                }
            }

            // 5. Pha 3: Cập nhật trạng thái bản ghi Refund và ghi OrderAuditLog
            var refundEntity = await _context.Refunds.FirstOrDefaultAsync(r => r.Id == newRefund.Id, cancellationToken);
            if (refundEntity != null)
            {
                refundEntity.UpdatedAt = DateTimeOffset.UtcNow;
                string auditAction;
                string newStatus;

                if (gatewayResult.Success)
                {
                    refundEntity.Status = "Succeeded";
                    newStatus = "Succeeded";
                    auditAction = "REFUND_SUCCEEDED";
                }
                else if (!gatewaySupportsRefund || gatewayResult.ManualRequired)
                {
                    refundEntity.Status = "ManualPending";
                    newStatus = "ManualPending";
                    refundEntity.FailureReason = gatewayResult.ErrorMessage;
                    auditAction = "REFUND_MANUAL_PENDING";
                }
                else
                {
                    refundEntity.Status = "Failed";
                    newStatus = "Failed";
                    refundEntity.FailureReason = gatewayResult.ErrorMessage ?? "Cổng thanh toán trả về kết quả hoàn tiền thất bại.";
                    auditAction = "REFUND_FAILED";
                }

                _orderAuditService.Record(
                    orderId,
                    "REFUND",
                    refundEntity.Id.ToString(),
                    auditAction,
                    "Requested",
                    newStatus,
                    "SYSTEM",
                    $"gateway:{(_paymentGateway?.ProviderName ?? "none")}",
                    null,
                    refundEntity.FailureReason ?? $"Hoàn tiền {refundEntity.Amount} VND.");

                await _context.SaveChangesAsync(cancellationToken);

                if (newStatus == "Succeeded")
                {
                    return RefundExecutionResult.CreateSuccess(orderId, refundEntity.Id, refundEntity.Amount,
                        $"Hoàn tiền {refundEntity.Amount} VND thành công qua cổng {_paymentGateway?.ProviderName ?? "Mock"}.");
                }
                if (newStatus == "ManualPending")
                {
                    return RefundExecutionResult.CreateManualPending(orderId, refundEntity.Id, refundEntity.Amount,
                        refundEntity.FailureReason, "Yêu cầu hoàn tiền đang chờ xử lý thủ công (ManualPending).");
                }
                return RefundExecutionResult.CreateFailed(orderId, refundEntity.Id, refundEntity.Amount,
                    refundEntity.FailureReason ?? "Lỗi cổng thanh toán", "Hoàn tiền thất bại.");
            }

            return RefundExecutionResult.CreateFailed(orderId, newRefund.Id, newRefund.Amount, "REFUND_NOT_FOUND", "Không tìm thấy bản ghi Refund để cập nhật kết quả.");
        }

        public async Task<List<RefundExecutionResult>> ProcessShowtimeRefundsAsync(
            Guid showtimeId,
            IEnumerable<Guid> paidOrderIds,
            string actorType,
            string actorName,
            Guid? actorId = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<RefundExecutionResult>();
            var distinctOrderIds = paidOrderIds.Distinct().ToList();

            foreach (var orderId in distinctOrderIds)
            {
                try
                {
                    var order = await _context.Orders
                        .AsNoTracking()
                        .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

                    if (order == null)
                    {
                        results.Add(RefundExecutionResult.CreateFailed(orderId, null, 0, "ORDER_NOT_FOUND", "Không tìm thấy đơn hàng."));
                        continue;
                    }

                    // Hoàn 100% giá trị đơn hàng theo Order.TotalAmount
                    var result = await ProcessOrderRefundAsync(
                        orderId: order.Id,
                        amount: order.TotalAmount,
                        cancelReason: OrderCancelReasons.ShowtimeCancelled,
                        actorType: actorType,
                        actorName: actorName,
                        actorId: actorId,
                        description: $"Hoàn 100% giá trị đơn hàng ({order.TotalAmount} VND) do suất diễn bị huỷ (S-47).",
                        cancellationToken: cancellationToken);

                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi không mong muốn khi hoàn tiền cho đơn {OrderId} của suất diễn {ShowtimeId}", orderId, showtimeId);
                    results.Add(RefundExecutionResult.CreateFailed(orderId, null, 0, ex.Message, "Lỗi ngoại lệ khi xử lý đơn hàng."));
                }
            }

            return results;
        }
    }
}
