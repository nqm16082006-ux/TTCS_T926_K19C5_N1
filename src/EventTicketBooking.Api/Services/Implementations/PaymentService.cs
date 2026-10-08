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
using Microsoft.AspNetCore.SignalR;
using EventTicketBooking.Api.Hubs;

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
        private readonly IHubContext<SeatStatusHub> _hubContext;
        private readonly EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue _ticketEmailQueue;
        private readonly ITicketService _ticketService;

        public PaymentService(
            AppDbContext context,
            IPaymentGateway paymentGateway,
            ILogger<PaymentService> logger,
            IHubContext<SeatStatusHub> hubContext,
            EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue ticketEmailQueue,
            ITicketService? ticketService = null)
        {
            _context = context;
            _paymentGateway = paymentGateway;
            _logger = logger;
            _hubContext = hubContext;
            _ticketEmailQueue = ticketEmailQueue;
            _ticketService = ticketService ?? new TicketService();
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
                var cleanup = new ExpiredOrderCleanupService(_context,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ExpiredOrderCleanupService>.Instance,
                    _hubContext);
                await cleanup.CleanupExpiredOrdersAsync(cancellationToken: cancellationToken);
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

                if (existingTransaction.Status == "FAILED" || existingTransaction.Status == "CANCELLED")
                {
                    var oldStatus = existingTransaction.Status;
                    existingTransaction.Status = "PENDING";
                    existingTransaction.UpdatedAt = DateTimeOffset.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Cập nhật lại giao dịch thanh toán từ {OldStatus} sang PENDING cho đơn hàng {OrderId}", oldStatus, order.Id);
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
                return PaymentCreationResult.CreateFailure("Không thể lưu giao dịch thanh toán. Vui lòng thử lại.");
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

            await using var webhookTransaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(cancellationToken) : null;

            try
            {
                _context.PaymentEvents.Add(paymentEvent);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                if (webhookTransaction != null) await webhookTransaction.RollbackAsync(cancellationToken);
                // Chèn bị từ chối vì trùng mã giao dịch -> Trả true (200 OK) và dừng, không gọi T-42
                _context.ChangeTracker.Clear();
                if (!await _context.PaymentEvents.AnyAsync(e => e.TransactionId == transactionId, cancellationToken))
                    throw;
                _logger.LogInformation("Webhook với mã giao dịch {TransactionId} đã được ghi nhận trong payment_events trước đó. Trả 200 OK và dừng.", transactionId);
                return true;
            }

            // 4. Chèn được thì gọi T-42 handler
            var resultDto = PaymentResultDto.FromWebhookParseResult(parseResult);
            await HandlePaymentResultAsync(resultDto, cancellationToken);
            if (webhookTransaction != null) await webhookTransaction.CommitAsync(cancellationToken);

            return true;
        }

        private static string SanitizeRawPayload(string rawPayload)
        {
            if (string.IsNullOrEmpty(rawPayload)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(rawPayload, @"\b(?:\d[ -]*?){13,19}\b", "****-****-****-****");
        }

        public async Task<PaymentExecutionResult> HandlePaymentResultAsync(PaymentResultDto result, CancellationToken cancellationToken = default)
        {
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
                return await HandlePaymentResultCoreAsync(result, cancellationToken);
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            var execution = await HandlePaymentResultCoreAsync(result, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return execution;
        }

        private async Task<PaymentExecutionResult> HandlePaymentResultCoreAsync(PaymentResultDto result, CancellationToken cancellationToken)
        {
            if (result == null)
            {
                return PaymentExecutionResult.CreateFailure("Kết quả thanh toán không hợp lệ.");
            }

            // Bước 1: Bắt/Tra cứu OrderId sơ bộ từ payload (OrderId hoặc OrderCode)
            Guid? targetOrderId = null;
            if (!string.IsNullOrEmpty(result.OrderId) && Guid.TryParse(result.OrderId, out var orderGuid))
            {
                targetOrderId = orderGuid;
            }
            else if (result.OrderCode.HasValue)
            {
                var paymentTx = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(pt => pt.OrderCode == result.OrderCode.Value, cancellationToken);
                if (paymentTx != null)
                {
                    targetOrderId = paymentTx.OrderId;
                }
            }

            if (!targetOrderId.HasValue)
            {
                _logger.LogWarning("Không tìm thấy đơn hàng tương ứng với kết quả thanh toán. OrderId: {OrderId}, OrderCode: {OrderCode}", result.OrderId, result.OrderCode);
                return PaymentExecutionResult.CreateFailure("Không tìm thấy đơn hàng tương ứng với kết quả thanh toán.", null, result.OrderCode);
            }

            // Bước 2: Bắt đầu Transaction và Lock record (FOR UPDATE trên DB thật)
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? dbTransaction = null;
            if (_context.Database.IsRelational() && _context.Database.CurrentTransaction == null)
            {
                dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                // Bước 3: Lấy dữ liệu Order mới nhất từ DB (Fetch/Reload)
                IQueryable<Order> orderQuery = _context.Orders
                    .Include(o => o.OrderItems)
                    .Include(o => o.PaymentTransaction)
                    .Where(o => o.Id == targetOrderId.Value);

                if (_context.Database.IsNpgsql())
                {
                    // Chạy SQL thô có FOR UPDATE để khóa record đơn hàng trong PostgreSQL (tránh Race Condition)
                    orderQuery = _context.Orders
                        .FromSqlRaw("SELECT * FROM orders WHERE \"Id\" = {0} FOR UPDATE", targetOrderId.Value)
                        .Include(o => o.OrderItems)
                        .Include(o => o.PaymentTransaction);
                }

                var order = await orderQuery.FirstOrDefaultAsync(cancellationToken);

                if (order == null)
                {
                    if (dbTransaction != null) await dbTransaction.RollbackAsync(cancellationToken);
                    return PaymentExecutionResult.CreateFailure("Không tìm thấy đơn hàng tương ứng với kết quả thanh toán.", null, result.OrderCode);
                }

                if (order.OrderItems == null || !order.OrderItems.Any())
                {
                    order.OrderItems = await _context.OrderItems
                        .Where(oi => oi.OrderId == order.Id)
                        .ToListAsync(cancellationToken);
                }

                // Bước 4: Thực hiện Re-check các điều kiện

                // 4.1. Đã thanh toán trước đó chưa? (Idempotency)
                if (order.Status == OrderStatus.Paid)
                {
                    _logger.LogInformation("Đơn hàng {OrderId} đã được ghi nhận thanh toán thành công trước đó (Idempotent). Bỏ qua xử lý trùng.", order.Id);
                    if (dbTransaction != null) await dbTransaction.CommitAsync(cancellationToken);
                    return PaymentExecutionResult.CreateSuccess(
                        order.Id,
                        result.OrderCode ?? order.PaymentTransaction?.OrderCode,
                        "Đơn hàng đã được thanh toán thành công trước đó.",
                        isAlreadyProcessed: true
                    );
                }

                // 4.2. Xử lý trường hợp thanh toán bị hủy từ cổng (Cancelled)
                if (result.Status == PaymentStatus.Cancelled)
                {
                    order.Status = OrderStatus.Cancelled;
                    order.UpdatedAt = DateTimeOffset.UtcNow;

                    var cancelledTx = await _context.PaymentTransactions
                        .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);
                    if (cancelledTx != null)
                    {
                        cancelledTx.Status = "CANCELLED";
                        cancelledTx.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    await _context.SaveChangesAsync(cancellationToken);
                    if (dbTransaction != null) await dbTransaction.CommitAsync(cancellationToken);
                    _logger.LogInformation("Đơn hàng {OrderId} thanh toán bị hủy (Status: {Status}).", order.Id, result.Status);
                    return PaymentExecutionResult.CreateFailure($"Thanh toán không thành công ({result.Status}).", order.Id, result.OrderCode, order.Status.ToString());
                }

                // 4.3. Xử lý trường hợp thanh toán thất bại từ cổng (Failed)
                if (result.Status == PaymentStatus.Failed)
                {
                    _logger.LogInformation("Đơn hàng {OrderId} thanh toán thất bại (Status: {Status}). Giữ nguyên trạng thái đơn hàng.", order.Id, result.Status);
                    if (dbTransaction != null) await dbTransaction.CommitAsync(cancellationToken);
                    return PaymentExecutionResult.CreateFailure($"Thanh toán không thành công ({result.Status}).", order.Id, result.OrderCode, order.Status.ToString());
                }

                // 4.4. Các trạng thái không phải Success
                if (result.Status != PaymentStatus.Success)
                {
                    if (dbTransaction != null) await dbTransaction.CommitAsync(cancellationToken);
                    return PaymentExecutionResult.CreateFailure($"Giao dịch chưa hoàn tất (Trạng thái: {result.Status}).", order.Id, result.OrderCode, order.Status.ToString());
                }

                // 4.5. Re-check đơn hàng đã bị hết hạn hoặc bị hủy chưa (ExpiresAt <= UtcNow)
                if (order.Status == OrderStatus.Expired || order.Status == OrderStatus.Cancelled || (order.Status == OrderStatus.Pending && order.ExpiresAt <= DateTimeOffset.UtcNow))
                {
                    order.Status = OrderStatus.NeedsAttention;
                    order.UpdatedAt = DateTimeOffset.UtcNow;

                    var paymentTxExpired = await _context.PaymentTransactions
                        .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);
                    if (paymentTxExpired != null)
                    {
                        paymentTxExpired.Status = "NEEDS_ATTENTION";
                        paymentTxExpired.UpdatedAt = DateTimeOffset.UtcNow;
                        if (!string.IsNullOrEmpty(result.TransactionId))
                        {
                            paymentTxExpired.TransactionId = result.TransactionId;
                        }
                    }

                    var expiredSeatIds = order.OrderItems.Select(oi => oi.SeatId).Distinct().ToList();
                    var expiredHolds = await _context.SeatHold.Where(h => expiredSeatIds.Contains(h.SeatId) &&
                        h.UserId == order.UserId && h.Status == "ACTIVE").ToListAsync(cancellationToken);
                    foreach (var hold in expiredHolds) hold.Status = "EXPIRED";
                    var expiredSeats = await _context.Seats.Where(s => expiredSeatIds.Contains(s.Id) && s.Status == "HELD")
                        .ToListAsync(cancellationToken);
                    foreach (var seat in expiredSeats) seat.Status = "AVAILABLE";

                    await _context.SaveChangesAsync(cancellationToken);

                    foreach (var seat in expiredSeats)
                    {
                        await _hubContext.Clients.Group($"Showtime_{seat.ShowtimeId}")
                            .SendAsync("SeatStatusChanged", new { SeatId = seat.Id, Status = "Available" }, cancellationToken);
                    }
                    if (dbTransaction != null) await dbTransaction.CommitAsync(cancellationToken);

                    _logger.LogWarning("Webhook thanh toán tới cho đơn hàng đã hết hạn hoặc bị hủy {OrderId}. Trạng thái Order: {OrderStatus}, Transaction: {TxStatus}", order.Id, order.Status, paymentTxExpired?.Status);

                    return PaymentExecutionResult.CreateFailure(
                        "Đơn hàng đã hết hạn hoặc bị huỷ. Giao dịch thanh toán được ghi nhận để hoàn tiền.",
                        order.Id,
                        result.OrderCode,
                        order.Status.ToString()
                    );
                }

                // 4.6. So sánh số tiền (Amount mismatch)
                if (order.TotalAmount <= 0)
                {
                    order.CalculateTotal();
                }

                if (result.Amount != order.TotalAmount)
                {
                    _logger.LogWarning("Số tiền thanh toán ({PaymentAmount}) KHÔNG khớp với số tiền đơn hàng {OrderId} ({OrderAmount}). Giao dịch bị từ chối.",
                        result.Amount, order.Id, order.TotalAmount);
                    if (dbTransaction != null) await dbTransaction.CommitAsync(cancellationToken);
                    return PaymentExecutionResult.CreateFailure(
                        $"Số tiền thanh toán ({result.Amount}) không khớp với giá trị đơn hàng ({order.TotalAmount}).",
                        order.Id,
                        result.OrderCode,
                        order.Status.ToString()
                    );
                }

                // Bước 5: Cập nhật trạng thái (Paid) và Commit Transaction

                // A. Cập nhật Order -> Paid
                order.Status = OrderStatus.Paid;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                // B. Cập nhật PaymentTransaction -> PAID
                var successTx = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(pt => pt.OrderId == order.Id, cancellationToken);
                if (successTx != null)
                {
                    successTx.Status = "PAID";
                    successTx.UpdatedAt = DateTimeOffset.UtcNow;
                    if (!string.IsNullOrEmpty(result.TransactionId))
                    {
                        successTx.TransactionId = result.TransactionId;
                    }
                }

                // C. Đánh dấu ghế tương ứng chuyển Status = "SOLD"
                var seatIds = order.OrderItems.Select(oi => oi.SeatId).Distinct().ToList();
                var seats = await _context.Seats
                    .Where(s => seatIds.Contains(s.Id))
                    .ToListAsync(cancellationToken);

                var activeSeatHolds = await _context.SeatHold.AsNoTracking()
                    .Where(h => seatIds.Contains(h.SeatId) && h.Status == "ACTIVE" && h.ExpiresAt > DateTime.UtcNow)
                    .ToListAsync(cancellationToken);
                if (seats.Any(s => s.Status == "SOLD") || activeSeatHolds.Any(h => h.UserId != order.UserId))
                {
                    order.Status = OrderStatus.NeedsAttention;
                    if (successTx != null)
                    {
                        successTx.Status = "NEEDS_ATTENTION";
                        successTx.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                    await _context.SaveChangesAsync(cancellationToken);
                    if (dbTransaction != null) await dbTransaction.CommitAsync(cancellationToken);
                    return PaymentExecutionResult.CreateFailure("Ghế không còn khả dụng. Giao dịch cần kiểm tra hoàn tiền.", order.Id, result.OrderCode);
                }

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

                // E. Sinh vé điện tử cho từng ghế trong đơn hàng (Story S-25)
                var existingTicketItemIds = await _context.Tickets
                    .Where(t => order.OrderItems.Select(oi => oi.Id).Contains(t.OrderItemId))
                    .Select(t => t.OrderItemId)
                    .ToListAsync(cancellationToken);

                foreach (var item in order.OrderItems)
                {
                    if (!existingTicketItemIds.Contains(item.Id) && item.Ticket == null)
                    {
                        var ticket = new Ticket
                        {
                            Id = Guid.NewGuid(),
                            OrderItemId = item.Id,
                            TicketCode = _ticketService.GenerateTicketCode(),
                            CreatedAt = DateTimeOffset.UtcNow,
                            OrderItem = item
                        };
                        _context.Tickets.Add(ticket);
                        item.Ticket = ticket;
                    }
                }

                await _context.SaveChangesAsync(cancellationToken);

                // Broadcast real-time update
                foreach (var seat in seats)
                {
                    await _hubContext.Clients.Group($"Showtime_{seat.ShowtimeId}")
                        .SendAsync("SeatStatusChanged", new { SeatId = seat.Id, Status = "Booked" }, cancellationToken);
                }

                if (dbTransaction != null)
                {
                    await dbTransaction.CommitAsync(cancellationToken);
                }

                // Gửi email vé cho khách hàng (không làm chặn luồng thanh toán)
                try
                {
                    await _ticketEmailQueue.EnqueueAsync(order.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi đẩy job gửi vé email vào queue cho đơn hàng {OrderId}.", order.Id);
                }

                _logger.LogInformation("Giao dịch thanh toán thành công cho đơn hàng {OrderId}. Số ghế đã bán: {SeatsCount}, Giữ chỗ đã chuyển đổi: {HoldsCount}.",
                    order.Id, seats.Count, holds.Count);

                return PaymentExecutionResult.CreateSuccess(
                    order.Id,
                    successTx?.OrderCode ?? result.OrderCode,
                    "Thanh toán thành công và đã xuất vé."
                );
            }
            catch (Exception ex)
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.RollbackAsync(cancellationToken);
                }
                _logger.LogError(ex, "Lỗi xảy ra trong giao dịch cập nhật thanh toán đơn hàng {OrderId}. Toàn bộ giao dịch đã được rollback.", targetOrderId);
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
