using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử tích hợp cho Task T-52 và T-53 (Story S-23):
    /// - Chứng minh đơn đã thanh toán (Paid) và đơn chưa hết hạn (Pending active) KHÔNG bị job đụng tới.
    /// - Chứng minh chỉ duy nhất đơn quá hạn (Pending expired) mới bị huỷ và nhả ghế về AVAILABLE trong một giao dịch.
    /// - Kiểm thử tính Idempotent (chạy lặp lại hai lần không ném lỗi và không tác động sai).
    /// - Kiểm thử ca đua (Race condition) giữa webhook thanh toán và job huỷ đơn quá hạn.
    /// </summary>
    public class ExpiredOrderCleanupTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly ExpiredOrderCleanupService _cleanupService;

        public ExpiredOrderCleanupTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new AppDbContext(options);
            _cleanupService = new ExpiredOrderCleanupService(_context, NullLogger<ExpiredOrderCleanupService>.Instance);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        #region Helper Methods

        private async Task<(Order order, Seat seat, SeatHolds hold)> CreateOrderGraphAsync(
            Guid userId, Guid showtimeId, Guid seatCategoryId, string row, int seatNumber,
            OrderStatus status, DateTimeOffset expiresAt, int price = 150000)
        {
            var seat = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                SeatCategoryId = seatCategoryId,
                Row = row,
                SeatNumber = seatNumber,
                Status = status == OrderStatus.Paid ? "SOLD" : "HELD"
            };

            var hold = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seat.Id,
                UserId = userId,
                Status = status == OrderStatus.Paid ? "CONVERTED" : "ACTIVE",
                HeldAt = DateTime.UtcNow.AddMinutes(-20),
                ExpiresAt = expiresAt.UtcDateTime
            };

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = showtimeId,
                Status = status,
                TotalAmount = price,
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20)
            };

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = seat.Id,
                Price = price
            };

            var paymentTx = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = Math.Abs(BitConverter.ToInt64(order.Id.ToByteArray(), 0) % 9000000000L) + 1000000000L,
                Amount = price,
                Status = status == OrderStatus.Paid ? "PAID" : "PENDING",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20)
            };

            _context.Seats.Add(seat);
            _context.SeatHold.Add(hold);
            _context.Orders.Add(order);
            _context.OrderItems.Add(orderItem);
            _context.PaymentTransactions.Add(paymentTx);
            await _context.SaveChangesAsync();

            return (order, seat, hold);
        }

        #endregion

        /// <summary>
        /// Task T-53 / S-23 AC1 & AC2:
        /// Tạo ba đơn: Đã trả (Paid), Chờ còn hạn (Pending active), Chờ quá hạn (Pending expired).
        /// Kiểm tra khi Job chạy: Chỉ đơn quá hạn bị chuyển sang Expired và ghế trả về AVAILABLE.
        /// Hai đơn còn lại giữ nguyên trạng thái không bị đụng tới.
        /// </summary>
        [Fact]
        public async Task CleanupExpiredOrders_ThreeOrdersScenario_OnlyExpiredPendingOrderIsCancelled()
        {
            // Arrange: Thiết lập thời điểm giả định (fakeNow) - NFR requirement: không chờ thật
            var fakeNow = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            var seatCategoryId = Guid.NewGuid();

            var user = new User
            {
                Id = userId,
                FullName = "Test User",
                Email = "testuser@example.com",
                PasswordHash = "hash"
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Đơn 1: Đã thanh toán (Paid) - Dù hết hạn theo mốc thời gian nhưng đã trả tiền -> Đơn & Ghế KHÔNG bị đụng tới
            var (paidOrder, paidSeat, paidHold) = await CreateOrderGraphAsync(
                userId, showtimeId, seatCategoryId, "A", 1,
                OrderStatus.Paid, fakeNow.AddMinutes(-10));

            // Đơn 2: Chờ thanh toán CÒN HẠN (Pending active) -> Đơn & Ghế KHÔNG bị đụng tới
            var (activeOrder, activeSeat, activeHold) = await CreateOrderGraphAsync(
                userId, showtimeId, seatCategoryId, "A", 2,
                OrderStatus.Pending, fakeNow.AddMinutes(10));

            // Đơn 3: Chờ thanh toán QUÁ HẠN (Pending expired) -> Phải bị huỷ và nhả ghế về AVAILABLE
            var (expiredOrder, expiredSeat, expiredHold) = await CreateOrderGraphAsync(
                userId, showtimeId, seatCategoryId, "A", 3,
                OrderStatus.Pending, fakeNow.AddMinutes(-5));

            // Act: Chạy Job huỷ đơn quá hạn tại thời điểm fakeNow
            int cancelledCount = await _cleanupService.CleanupExpiredOrdersAsync(fakeNow);

            // Assert:
            // 1. Chỉ duy nhất 1 đơn hàng bị huỷ
            Assert.Equal(1, cancelledCount);

            // 2. Kiểm tra Đơn 1 (Paid): Giữ nguyên Paid, ghế giữ nguyên SOLD, hold giữ nguyên CONVERTED
            var dbPaidOrder = await _context.Orders.FindAsync(paidOrder.Id);
            var dbPaidSeat = await _context.Seats.FindAsync(paidSeat.Id);
            var dbPaidHold = await _context.SeatHold.FindAsync(paidHold.Id);

            Assert.NotNull(dbPaidOrder);
            Assert.Equal(OrderStatus.Paid, dbPaidOrder.Status);
            Assert.NotNull(dbPaidSeat);
            Assert.Equal("SOLD", dbPaidSeat.Status);
            Assert.NotNull(dbPaidHold);
            Assert.Equal("CONVERTED", dbPaidHold.Status);

            // 3. Kiểm tra Đơn 2 (Pending active): Giữ nguyên Pending, ghế giữ nguyên HELD, hold giữ nguyên ACTIVE
            var dbActiveOrder = await _context.Orders.FindAsync(activeOrder.Id);
            var dbActiveSeat = await _context.Seats.FindAsync(activeSeat.Id);
            var dbActiveHold = await _context.SeatHold.FindAsync(activeHold.Id);

            Assert.NotNull(dbActiveOrder);
            Assert.Equal(OrderStatus.Pending, dbActiveOrder.Status);
            Assert.NotNull(dbActiveSeat);
            Assert.Equal("HELD", dbActiveSeat.Status);
            Assert.NotNull(dbActiveHold);
            Assert.Equal("ACTIVE", dbActiveHold.Status);

            // 4. Kiểm tra Đơn 3 (Pending expired): Phải đổi sang Expired, ghế sang AVAILABLE, hold sang EXPIRED
            var dbExpiredOrder = await _context.Orders.FindAsync(expiredOrder.Id);
            var dbExpiredSeat = await _context.Seats.FindAsync(expiredSeat.Id);
            var dbExpiredHold = await _context.SeatHold.FindAsync(expiredHold.Id);

            Assert.NotNull(dbExpiredOrder);
            Assert.Equal(OrderStatus.Expired, dbExpiredOrder.Status);
            Assert.NotNull(dbExpiredSeat);
            Assert.Equal("AVAILABLE", dbExpiredSeat.Status);
            Assert.NotNull(dbExpiredHold);
            Assert.Equal("EXPIRED", dbExpiredHold.Status);
        }

        /// <summary>
        /// Task T-52 AC: Chạy hai lần không lỗi (Idempotency check).
        /// </summary>
        [Fact]
        public async Task CleanupExpiredOrders_RunTwice_IsIdempotentAndThrowsNoError()
        {
            // Arrange
            var fakeNow = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            var seatCategoryId = Guid.NewGuid();

            await CreateOrderGraphAsync(
                userId, showtimeId, seatCategoryId, "B", 1,
                OrderStatus.Pending, fakeNow.AddMinutes(-5));

            // Act 1: Chạy lần thứ 1
            int countFirstRun = await _cleanupService.CleanupExpiredOrdersAsync(fakeNow);
            Assert.Equal(1, countFirstRun);

            // Act 2: Chạy lần thứ 2 ngay lập tức
            int countSecondRun = await _cleanupService.CleanupExpiredOrdersAsync(fakeNow);

            // Assert 2: Lần 2 trả về 0, không ném ngoại lệ
            Assert.Equal(0, countSecondRun);
        }

        /// <summary>
        /// Task T-53 / S-23 AC3: Ca đua (Race condition).
        /// Gửi webhook thanh toán thành công và chạy job huỷ đơn cùng lúc.
        /// Kiểm tra kết quả luôn kết thúc ở 1 trong 2 trạng thái hợp lệ và không có trạng thái lửng.
        /// </summary>
        [Fact]
        public async Task RaceCondition_WebhookAndCleanupJobRunConcurrently_EndsInValidStateWithoutInconsistency()
        {
            // Arrange
            var fakeNow = DateTimeOffset.UtcNow;
            var userId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            var seatCategoryId = Guid.NewGuid();

            var user = new User
            {
                Id = userId,
                FullName = "Concurrent User",
                Email = "concurrent@example.com",
                PasswordHash = "hash"
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            var (order, seat, hold) = await CreateOrderGraphAsync(
                userId, showtimeId, seatCategoryId, "C", 1,
                OrderStatus.Pending, fakeNow.AddMinutes(-2), price: 200000);

            var paymentTx = await _context.PaymentTransactions.FirstAsync(pt => pt.OrderId == order.Id);

            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var webhookResult = new PaymentResultDto
            {
                Success = true,
                OrderId = order.Id.ToString(),
                OrderCode = paymentTx.OrderCode,
                Amount = 200000,
                Status = PaymentStatus.Success,
                TransactionId = "TX_CONCURRENT_RACE_123",
                PaidAt = fakeNow.DateTime
            };

            // Act: Chạy song song cả 2 task (Job quét quá hạn & Webhook thanh toán)
            var jobTask = _cleanupService.CleanupExpiredOrdersAsync(fakeNow);
            var webhookTask = paymentService.HandlePaymentResultAsync(webhookResult);

            await Task.WhenAll(jobTask, webhookTask);

            // Assert: Kiểm tra không bị trạng thái lửng (không có sự mâu thuẫn giữa Order - Seat - PaymentTransaction)
            var finalOrder = await _context.Orders.FindAsync(order.Id);
            var finalSeat = await _context.Seats.FindAsync(seat.Id);
            var finalTx = await _context.PaymentTransactions.FirstOrDefaultAsync(pt => pt.OrderId == order.Id);

            Assert.NotNull(finalOrder);
            Assert.NotNull(finalSeat);
            Assert.NotNull(finalTx);

            bool isStateA = finalOrder.Status == OrderStatus.Paid &&
                            finalSeat.Status == "SOLD" &&
                            finalTx.Status == "PAID";

            // A verified payment received after expiry requires reconciliation; it must never sell the released seat.
            bool isStateB = (finalOrder.Status == OrderStatus.NeedsAttention || finalOrder.Status == OrderStatus.Expired) &&
                            finalSeat.Status == "AVAILABLE" &&
                            (finalTx.Status == "NEEDS_ATTENTION" || finalTx.Status == "REFUND_REQUIRED");

            // Bắt buộc phải khớp chính xác 1 trong 2 trạng thái hợp lệ, tuyệt đối không có trạng thái lai/lửng
            Assert.True(isStateA || isStateB, $"Phát hiện trạng thái lửng không hợp lệ! OrderStatus: {finalOrder.Status}, SeatStatus: {finalSeat.Status}, TxStatus: {finalTx.Status}");
        }
    }
}
