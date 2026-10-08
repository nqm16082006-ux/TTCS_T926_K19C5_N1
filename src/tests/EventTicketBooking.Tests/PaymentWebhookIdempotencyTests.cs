using System;
using System.Collections.Generic;
using System.IO;
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
    /// Bộ kiểm thử tích hợp cho Task T-45, T-46, T-47 & S-20:
    /// - T-45: Bảng payment_events lưu mã giao dịch với ràng buộc unique.
    /// - T-46: Xử lý webhook: bỏ qua mã đã có, khóa theo mã giao dịch khi xử lý song song.
    /// - T-47: Kịch bản gửi lại cùng một webhook 5 lần (tuần tự & song song) và đếm số vé sinh ra.
    /// </summary>
    public class PaymentWebhookIdempotencyTests
    {
        private AppDbContext CreateSqliteDbContext(string dbPath)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var context = new AppDbContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private async Task<(Guid userId, Guid showtimeId, Guid orderId, long orderCode, List<Guid> seatIds)> SeedDatabaseAsync(AppDbContext context, OrderStatus status = OrderStatus.Pending)
        {
            var userId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            var seatCategoryId = Guid.NewGuid();

            var user = new User
            {
                Id = userId,
                Username = $"user_{Guid.NewGuid():N}",
                Email = $"user_{Guid.NewGuid():N}@example.com",
                PasswordHash = "hashed_pass"
            };

            var eventObj = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Test Event",
                Location = "Test Hall",
                TotalSeats = 100,
                OwnerId = userId
            };

            var seatCategory = new SeatCategory
            {
                Id = seatCategoryId,
                ShowtimeId = showtimeId,
                Name = "VIP",
                Price = 150000
            };

            var showtime = new Showtime
            {
                Id = showtimeId,
                EventId = eventObj.Id,
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(2)
            };

            var seat1 = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                SeatCategoryId = seatCategoryId,
                Row = "B",
                SeatNumber = 1,
                Status = "AVAILABLE"
            };

            var seat2 = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                SeatCategoryId = seatCategoryId,
                Row = "B",
                SeatNumber = 2,
                Status = "AVAILABLE"
            };

            long orderCode = 9988776655L;
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = showtimeId,
                Status = status,
                TotalAmount = 300000,
                ExpiresAt = status == OrderStatus.Expired ? DateTimeOffset.UtcNow.AddMinutes(-10) : DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var item1 = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, SeatId = seat1.Id, Price = 150000 };
            var item2 = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, SeatId = seat2.Id, Price = 150000 };

            var paymentTx = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = orderCode,
                Amount = 300000,
                Status = status == OrderStatus.Paid ? "PAID" : "PENDING",
                TransactionId = "TXN_REPLAY_TEST_100"
            };

            var hold1 = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seat1.Id,
                UserId = userId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15)
            };

            var hold2 = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seat2.Id,
                UserId = userId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15)
            };

            context.Users.Add(user);
            context.Events.Add(eventObj);
            context.Showtimes.Add(showtime);
            context.SeatCategories.Add(seatCategory);
            context.Seats.AddRange(seat1, seat2);
            context.Orders.Add(order);
            context.OrderItems.AddRange(item1, item2);
            context.PaymentTransactions.Add(paymentTx);
            context.SeatHold.AddRange(hold1, hold2);
            await context.SaveChangesAsync();

            return (userId, showtimeId, order.Id, orderCode, new List<Guid> { seat1.Id, seat2.Id });
        }

        #region Task T-45 Tests

        [Fact]
        public async Task T45_PaymentEvent_UniqueTransactionId_RejectsDuplicateInsertion()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"t45_unique_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var txnId = "TXN_UNIQUE_001";

                var event1 = new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = txnId,
                    RawPayload = "{\"data\": {\"orderCode\": 123}}",
                    CreatedAt = DateTimeOffset.UtcNow
                };

                var event2 = new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = txnId,
                    RawPayload = "{\"data\": {\"orderCode\": 123}}",
                    CreatedAt = DateTimeOffset.UtcNow
                };

                // Act
                context.PaymentEvents.Add(event1);
                await context.SaveChangesAsync();

                context.PaymentEvents.Add(event2);

                // Assert: Duplicate insert throws DbUpdateException
                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch { }
                }
            }
        }

        #endregion

        #region Task T-46 & T-47 Tests: Sequential & Concurrent 5x Webhook Replay

        [Fact]
        public async Task T47_ProcessPaymentWebhook_SequentialReplay5Times_OnlyOneEvent_OrderPaidOnce_OneSetOfTickets()
        {
            // Arrange: Gửi lại cùng một webhook 5 lần tuần tự (Task T-47)
            var dbPath = Path.Combine(Path.GetTempPath(), $"t47_seq_{Guid.NewGuid():N}.db");
            try
            {
                using (var setupContext = CreateSqliteDbContext(dbPath))
                {
                    await SeedDatabaseAsync(setupContext);
                }

                var fakeGateway = new FakePaymentGateway
                {
                    SimulatedTransactionId = "TXN_REPLAY_TEST_100",
                    CustomWebhookParseResult = WebhookParseResult.CreateSuccess(
                        orderCode: 9988776655L,
                        amount: 300000,
                        transactionId: "TXN_REPLAY_TEST_100",
                        paidAt: DateTimeOffset.UtcNow
                    )
                };

                string webhookPayload = "{\"code\":\"00\",\"data\":{\"orderCode\":9988776655,\"amount\":300000,\"reference\":\"TXN_REPLAY_TEST_100\"}}";
                string signature = "VALID_SIGNATURE_123";

                // Act: Gửi 5 lần tuần tự
                for (int i = 0; i < 5; i++)
                {
                    using var loopContext = CreateSqliteDbContext(dbPath);
                    var paymentService = new PaymentService(loopContext, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);
                    bool result = await paymentService.ProcessPaymentWebhookAsync(webhookPayload, signature);
                    Assert.True(result, $"Lần gửi {i + 1} phải trả về true (200 OK)");
                }

                // Assert: Kiểm tra số dòng payment_events, số vé và trạng thái đơn
                using var verifyContext = CreateSqliteDbContext(dbPath);
                var paymentEventCount = await verifyContext.PaymentEvents.CountAsync();
                var order = await verifyContext.Orders.FirstOrDefaultAsync();
                var soldSeatsCount = await verifyContext.Seats.CountAsync(s => s.Status == "SOLD");
                var convertedHoldsCount = await verifyContext.SeatHold.CountAsync(sh => sh.Status == "CONVERTED");

                Assert.Equal(1, paymentEventCount); // Đúng 1 bản ghi payment_events
                Assert.NotNull(order);
                Assert.Equal(OrderStatus.Paid, order.Status); // Đơn đổi trạng thái thành Paid
                Assert.Equal(2, soldSeatsCount); // Vé chỉ sinh đúng 1 bộ (2 ghế SOLD)
                Assert.Equal(2, convertedHoldsCount);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch { }
                }
            }
        }

        [Fact]
        public async Task T47_ProcessPaymentWebhook_ConcurrentReplay5Times_No500_OnlyOneEvent_OneSetOfTickets()
        {
            // Arrange: Hai/nhiều bản sao đồng thời của cùng 1 webhook (5 lần song song)
            var dbPath = Path.Combine(Path.GetTempPath(), $"t47_conc_{Guid.NewGuid():N}.db");
            try
            {
                using (var setupContext = CreateSqliteDbContext(dbPath))
                {
                    await SeedDatabaseAsync(setupContext);
                }

                var fakeGateway = new FakePaymentGateway
                {
                    SimulatedTransactionId = "TXN_CONCURRENT_999",
                    CustomWebhookParseResult = WebhookParseResult.CreateSuccess(
                        orderCode: 9988776655L,
                        amount: 300000,
                        transactionId: "TXN_CONCURRENT_999",
                        paidAt: DateTimeOffset.UtcNow
                    )
                };

                string webhookPayload = "{\"code\":\"00\",\"data\":{\"orderCode\":9988776655,\"amount\":300000,\"reference\":\"TXN_CONCURRENT_999\"}}";
                string signature = "VALID_CONCURRENT_SIG";

                // Act: Gửi 5 webhook đồng thời song song
                var tasks = Enumerable.Range(0, 5).Select(async _ =>
                {
                    using var threadContext = CreateSqliteDbContext(dbPath);
                    var paymentService = new PaymentService(threadContext, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);
                    return await paymentService.ProcessPaymentWebhookAsync(webhookPayload, signature);
                }).ToArray();

                bool[] results = await Task.WhenAll(tasks);

                // Assert: Cả 5 request đều thành công (không có ngoại lệ 500 nảy sinh)
                Assert.All(results, res => Assert.True(res));

                using var verifyContext = CreateSqliteDbContext(dbPath);
                var paymentEvents = await verifyContext.PaymentEvents.ToListAsync();
                var order = await verifyContext.Orders.FirstOrDefaultAsync();
                var soldSeatsCount = await verifyContext.Seats.CountAsync(s => s.Status == "SOLD");

                Assert.Single(paymentEvents); // Đúng 1 bản ghi payment_events được chèn
                Assert.Equal("TXN_CONCURRENT_999", paymentEvents[0].TransactionId);
                Assert.NotNull(order);
                Assert.Equal(OrderStatus.Paid, order.Status);
                Assert.Equal(2, soldSeatsCount); // Vé chỉ sinh 1 bộ
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch { }
                }
            }
        }

        #endregion

        #region Task T-46 & S-20 Expired Order Webhook Test

        [Fact]
        public async Task T46_ProcessPaymentWebhook_ExpiredOrder_DoesNotMarkPaid_FlagsAsNeedsAttention()
        {
            // Arrange: Webhook tới cho đơn đã bị hủy vì hết hạn
            var dbPath = Path.Combine(Path.GetTempPath(), $"t46_exp_{Guid.NewGuid():N}.db");
            try
            {
                using (var setupContext = CreateSqliteDbContext(dbPath))
                {
                    await SeedDatabaseAsync(setupContext, status: OrderStatus.Expired);
                }

                var fakeGateway = new FakePaymentGateway
                {
                    SimulatedTransactionId = "TXN_EXPIRED_ORDER_777",
                    CustomWebhookParseResult = WebhookParseResult.CreateSuccess(
                        orderCode: 9988776655L,
                        amount: 300000,
                        transactionId: "TXN_EXPIRED_ORDER_777",
                        paidAt: DateTimeOffset.UtcNow
                    )
                };

                string webhookPayload = "{\"code\":\"00\",\"data\":{\"orderCode\":9988776655,\"amount\":300000,\"reference\":\"TXN_EXPIRED_ORDER_777\"}}";
                string signature = "VALID_SIG_EXPIRED";

                // Act
                using var context = CreateSqliteDbContext(dbPath);
                var paymentService = new PaymentService(context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);
                bool processed = await paymentService.ProcessPaymentWebhookAsync(webhookPayload, signature);

                // Assert
                Assert.True(processed); // Webhook trả 200 cho cổng

                using var verifyContext = CreateSqliteDbContext(dbPath);
                var order = await verifyContext.Orders.FirstOrDefaultAsync();
                var soldSeatsCount = await verifyContext.Seats.CountAsync(s => s.Status == "SOLD");
                var paymentTx = await verifyContext.PaymentTransactions.FirstOrDefaultAsync();

                Assert.NotNull(order);
                Assert.NotEqual(OrderStatus.Paid, order.Status); // KHÔNG đổi đơn thành đã trả
                Assert.Equal(OrderStatus.NeedsAttention, order.Status); // Tiền về sau hạn: cần đối soát, không bán lại ghế
                Assert.NotNull(paymentTx);
                Assert.Equal("NEEDS_ATTENTION", paymentTx.Status);
                Assert.Equal(0, soldSeatsCount); // Không xuất/sinh vé mới
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    try { File.Delete(dbPath); } catch { }
                }
            }
        }

        #endregion
    }
}
