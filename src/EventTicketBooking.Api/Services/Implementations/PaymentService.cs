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

            // Trích xuất mã giao dịch từ cổng để kiểm tra trùng trong payment_events (Task T-45 & T-46)
            string transactionId = !string.IsNullOrWhiteSpace(parseResult.TransactionId)
                ? parseResult.TransactionId
                : (parseResult.OrderCode?.ToString() ?? parseResult.OrderId ?? Guid.NewGuid().ToString());

            // 3. Chèn vào payment_events để kiểm tra tính unique và khóa theo mã giao dịch
            var paymentEvent = new PaymentEvent
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                RawPayload = SanitizeRawPayload(webhookPayload),
                CreatedAt = DateTimeOffset.UtcNow
            };

            try
            {
                _context.PaymentEvents.Add(paymentEvent);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Chèn bị từ chối vì trùng mã giao dịch -> Trả true (200 OK) và dừng, không gọi T-42
                _context.ChangeTracker.Clear();
                _logger.LogInformation("Webhook với mã giao dịch {TransactionId} đã được ghi nhận trong payment_events trước đó. Trả 200 OK và dừng.", transactionId);
                return true;
            }

            // 4. Chèn được thì gọi T-42 handler
            var resultDto = PaymentResultDto.FromWebhookParseResult(parseResult);
            await HandlePaymentResultAsync(resultDto, cancellationToken);

            return true;
        }

        private static string SanitizeRawPayload(string rawPayload)
        {
            if (string.IsNullOrEmpty(rawPayload)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(rawPayload, @"\b(?:\d[ -]*?){13,19}\b", "****-****-****-****");
        }

        public async Task<PaymentExecutionResult> HandlePaymentResultAsync(PaymentResultDto result, CancellationToken cancellationToken = default)
        {
            if (result == null)
            {
                return PaymentExecutionResult.CreateFailure("Kết quả thanh toán không hợp lệ.");
            }

            // 1. Xác định đơn hàng từ OrderId hoặc OrderCode
            Order? order = null;
            if (!string.IsNullOrEmpty(result.OrderId) && Guid.TryParse(result.OrderId, out var orderGuid))
            {
                order = await _context.Orders
                    .Include(o => o.OrderItems)
                    .Include(o => o.PaymentTransaction)
                    .FirstOrDefaultAsync(o => o.Id == orderGuid, cancellationToken);
            }

            if (order == null && result.OrderCode.HasValue)
            {
                var paymentTx = await _context.PaymentTransactions
                    .Include(pt => pt.Order)
                        .ThenInclude(o => o.OrderItems)
                    .FirstOrDefaultAsync(pt => pt.OrderCode == result.OrderCode.Value, cancellationToken);

                if (paymentTx != null)
                {
                    order = paymentTx.Order;
                }
            }

            if (order == null)
            {
                _logger.LogWarning("Không tìm thấy đơn hàng tương ứng với kết quả thanh toán. OrderId: {OrderId}, OrderCode: {OrderCode}", result.OrderId, result.OrderCode);
                return PaymentExecutionResult.CreateFailure("Không tìm thấy đơn hàng tương ứng với kết quả thanh toán.", null, result.OrderCode);
            }

            if (order.OrderItems == null || !order.OrderItems.Any())
            {
                order.OrderItems = await _context.OrderItems
                    .Where(oi => oi.OrderId == order.Id)
                    .ToListAsync(cancellationToken);
            }

            // 2. Kiểm tra tính Idempotent: Nếu đơn đã được xác nhận thanh toán trước đó
            if (order.Status == OrderStatus.Paid)
            {
                _logger.LogInformation("Đơn hàng {OrderId} đã được ghi nhận thanh toán thành công trước đó (Idempotent). Bỏ qua xử lý trùng.", order.Id);
                return PaymentExecutionResult.CreateSuccess(
                    order.Id,
                    result.OrderCode ?? order.PaymentTransaction?.OrderCode,
                    "Đơn hàng đã được thanh toán thành công trước đó.",
                    isAlreadyProcessed: true
                );
            }

            // 2.5 Kiểm tra đơn hàng đã bị hủy vì hết hạn hay không (Task T-46 & S-20)
            if (order.Status == OrderStatus.Expired || (order.Status == OrderStatus.Pending && order.ExpiresAt <= DateTimeOffset.UtcNow))
            {
                order.Status = OrderStatus.NeedsAttention;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                var paymentTx = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);

                if (paymentTx != null)
                {
                    paymentTx.Status = "NEEDS_ATTENTION";
                    paymentTx.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogWarning("Webhook thanh toán tới cho đơn hàng đã hết hạn {OrderId}. Đã đánh dấu đơn cần kiểm tra để hoàn tiền.", order.Id);

                return PaymentExecutionResult.CreateFailure(
                    "Đơn hàng đã hết hạn. Đã đánh dấu đơn cần kiểm tra để hoàn tiền.",
                    order.Id,
                    result.OrderCode,
                    order.Status.ToString()
                );
            }

            // 3. Xử lý trường hợp thanh toán thất bại hoặc bị hủy từ cổng
            if (result.Status == PaymentStatus.Cancelled || result.Status == PaymentStatus.Failed)
            {
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                var paymentTx = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);

                if (paymentTx != null)
                {
                    paymentTx.Status = result.Status == PaymentStatus.Cancelled ? "CANCELLED" : "FAILED";
                    paymentTx.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Đơn hàng {OrderId} thanh toán thất bại/bị hủy (Status: {Status}).", order.Id, result.Status);
                return PaymentExecutionResult.CreateFailure($"Thanh toán không thành công ({result.Status}).", order.Id, result.OrderCode, order.Status.ToString());
            }

            if (result.Status != PaymentStatus.Success)
            {
                return PaymentExecutionResult.CreateFailure($"Giao dịch chưa hoàn tất (Trạng thái: {result.Status}).", order.Id, result.OrderCode, order.Status.ToString());
            }

            // 4. Đối chiếu số tiền thanh toán (Payment amount) với Order.TotalAmount từ backend
            if (order.TotalAmount <= 0)
            {
                order.CalculateTotal();
            }

            if (result.Amount != order.TotalAmount)
            {
                _logger.LogWarning("Số tiền thanh toán ({PaymentAmount}) KHÔNG khớp với số tiền đơn hàng {OrderId} ({OrderAmount}). Giao dịch bị từ chối.",
                    result.Amount, order.Id, order.TotalAmount);
                return PaymentExecutionResult.CreateFailure(
                    $"Số tiền thanh toán ({result.Amount}) không khớp với giá trị đơn hàng ({order.TotalAmount}).",
                    order.Id,
                    result.OrderCode,
                    order.Status.ToString()
                );
            }

            // 5. Cập nhật trạng thái đơn hàng, đánh dấu ghế đã bán, xoá giữ chỗ trong cùng MỘT Database Transaction duy nhất
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? dbTransaction = null;
            if (_context.Database.IsRelational())
            {
                dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }
            else
            {
                try
                {
                    dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // Nhà cung cấp InMemory có thể cảnh báo/ném lỗi nếu chưa bật bỏ qua cảnh báo giao dịch
                }
            }

            try
            {
                if (_context.Database.IsNpgsql())
                {
                    // Khoá dòng đơn hàng trong PostgreSQL (FOR UPDATE) để giải quyết tranh chấp với background job huỷ đơn
                    await _context.Orders
                        .FromSqlRaw("SELECT * FROM orders WHERE \"Id\" = {0} FOR UPDATE", order.Id)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                // Đảm bảo Re-check trạng thái nếu Job vừa huỷ đơn ngay trước khi lấy được lock (S-23 AC3)
                if (order.Status == OrderStatus.Expired || order.Status == OrderStatus.Cancelled)
                {
                    var paymentTxExpired = await _context.PaymentTransactions
                        .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);
                    if (paymentTxExpired != null)
                    {
                        paymentTxExpired.Status = "REFUND_REQUIRED";
                        paymentTxExpired.UpdatedAt = DateTimeOffset.UtcNow;
                        if (!string.IsNullOrEmpty(result.TransactionId))
                        {
                            paymentTxExpired.TransactionId = result.TransactionId;
                        }
                        await _context.SaveChangesAsync(cancellationToken);
                    }

                    if (dbTransaction != null)
                    {
                        await dbTransaction.CommitAsync(cancellationToken);
                    }

                    _logger.LogWarning("Đơn hàng {OrderId} đã bị hết hạn/huỷ trước khi webhook thanh toán thành công tới. Đã đánh dấu giao dịch cần hoàn tiền (REFUND_REQUIRED).", order.Id);

                    return PaymentExecutionResult.CreateFailure(
                        "Đơn hàng đã hết hạn. Giao dịch thanh toán được ghi nhận và đánh dấu cần hoàn tiền (REFUND_REQUIRED).",
                        order.Id,
                        result.OrderCode,
                        order.Status.ToString()
                    );
                }

                // A. Cập nhật Order -> Paid
                order.Status = OrderStatus.Paid;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                // B. Cập nhật PaymentTransaction -> PAID
                var paymentTx = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);
                if (paymentTx != null)
                {
                    paymentTx.Status = "PAID";
                    paymentTx.UpdatedAt = DateTimeOffset.UtcNow;
                    if (!string.IsNullOrEmpty(result.TransactionId))
                    {
                        paymentTx.TransactionId = result.TransactionId;
                    }
                }

                // C. Đánh dấu ghế tương ứng chuyển Status = "SOLD"
                var seatIds = order.OrderItems.Select(oi => oi.SeatId).Distinct().ToList();
                var seats = await _context.Seats
                    .Where(s => seatIds.Contains(s.Id))
                    .ToListAsync(cancellationToken);

                foreach (var seat in seats)
                {
                    seat.Status = "SOLD";
                }

                // D. Cập nhật SeatHold tương ứng sang Status = "CONVERTED" (xoá/giải phóng giữ chỗ)
                var holds = await _context.SeatHold
                    .Where(sh => seatIds.Contains(sh.SeatId) && sh.UserId == order.UserId)
                    .ToListAsync(cancellationToken);

                foreach (var hold in holds)
                {
                    hold.Status = "CONVERTED";
                }

                await _context.SaveChangesAsync(cancellationToken);

                if (dbTransaction != null)
                {
                    await dbTransaction.CommitAsync(cancellationToken);
                }

                _logger.LogInformation("Giao dịch thanh toán thành công cho đơn hàng {OrderId}. Số ghế đã bán: {SeatsCount}, Giữ chỗ đã chuyển đổi: {HoldsCount}.",
                    order.Id, seats.Count, holds.Count);

                return PaymentExecutionResult.CreateSuccess(
                    order.Id,
                    paymentTx?.OrderCode ?? result.OrderCode,
                    "Thanh toán thành công và đã xuất vé."
                );
            }
            catch (Exception ex)
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.RollbackAsync(cancellationToken);
                }
                _logger.LogError(ex, "Lỗi xảy ra trong giao dịch cập nhật thanh toán đơn hàng {OrderId}. Toàn bộ giao dịch đã được rollback.", order.Id);
                throw;
            }
            finally
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.DisposeAsync();
                }
            }
        }

        public async Task<PaymentExecutionResult> VerifyAndProcessPaymentReturnAsync(PaymentReturnQueryDto query, CancellationToken cancellationToken = default)
        {
            if (query == null || (!query.OrderId.HasValue && !query.OrderCode.HasValue))
            {
                return PaymentExecutionResult.CreateFailure("Thiếu thông tin mã đơn hàng để đối soát.");
            }

            // 1. Tìm Order trong cơ sở dữ liệu
            Order? order = null;
            if (query.OrderId.HasValue)
            {
                order = await _context.Orders
                    .Include(o => o.OrderItems)
                    .Include(o => o.PaymentTransaction)
                    .FirstOrDefaultAsync(o => o.Id == query.OrderId.Value, cancellationToken);
            }
            else if (query.OrderCode.HasValue)
            {
                var pt = await _context.PaymentTransactions
                    .Include(p => p.Order)
                        .ThenInclude(o => o.OrderItems)
                    .FirstOrDefaultAsync(p => p.OrderCode == query.OrderCode.Value, cancellationToken);

                if (pt != null)
                {
                    order = pt.Order;
                }
            }

            if (order == null)
            {
                return PaymentExecutionResult.CreateFailure("Không tìm thấy đơn hàng cần xác minh.");
            }

            // 2. Nếu đơn đã ở trạng thái Paid (ví dụ webhook đã đến và xử lý trước), trả về kết quả thành công ngay (Idempotent)
            if (order.Status == OrderStatus.Paid)
            {
                _logger.LogInformation("Đơn hàng {OrderId} đã được xác nhận thanh toán trước đó (Idempotent).", order.Id);
                return PaymentExecutionResult.CreateSuccess(
                    order.Id,
                    query.OrderCode ?? order.PaymentTransaction?.OrderCode,
                    "Đơn hàng đã được xác nhận thanh toán thành công.",
                    isAlreadyProcessed: true
                );
            }

            // 3. KHÔNG TIN TƯỞNG query.Status từ client! Phải truy vấn trực tiếp cổng thanh toán server-side
            long? orderCodeToQuery = query.OrderCode;
            if (!orderCodeToQuery.HasValue)
            {
                var pt = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(p => p.OrderId == order.Id, cancellationToken);
                orderCodeToQuery = pt?.OrderCode;
            }

            if (!orderCodeToQuery.HasValue)
            {
                return PaymentExecutionResult.CreateFailure("Không tìm thấy mã giao dịch thanh toán để đối soát với cổng.", order.Id, null, order.Status.ToString());
            }

            _logger.LogInformation("Đang đối soát trạng thái giao dịch với cổng thanh toán cho đơn hàng {OrderId}, OrderCode: {OrderCode}", order.Id, orderCodeToQuery.Value);

            var gatewayStatus = await _paymentGateway.QueryPaymentStatusAsync(orderCodeToQuery.Value, cancellationToken);

            if (gatewayStatus == null)
            {
                _logger.LogWarning("Không thể đối soát với cổng thanh toán hoặc giao dịch chưa được xác nhận trên gateway cho OrderCode: {OrderCode}", orderCodeToQuery.Value);
                return PaymentExecutionResult.CreateFailure("Không thể xác minh trạng thái thanh toán từ cổng thanh toán.", order.Id, orderCodeToQuery, order.Status.ToString());
            }

            gatewayStatus.OrderId = order.Id.ToString();
            gatewayStatus.OrderCode = orderCodeToQuery.Value;

            // 4. Ủy thác cho Payment Result Handler dùng chung thực hiện đối chiếu số tiền và cập nhật DB trong 1 transaction
            return await HandlePaymentResultAsync(gatewayStatus, cancellationToken);
        }
    }
}
