using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử toàn diện cho Task T-42:
    /// "Nhận kết quả trả về, đối chiếu trạng thái đơn và ghế trong một giao dịch"
    /// </summary>
    public class PaymentResultTests : IDisposable
    {
        private readonly AppDbContext _context;

        public PaymentResultTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new AppDbContext(options);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        #region Helper Setup

        private async Task<(User user, Showtime showtime, List<Seat> seats, Order order, PaymentTransaction transaction, List<SeatHolds> holds)> SeedFullOrderGraphAsync(int totalAmount = 500000, OrderStatus initialStatus = OrderStatus.Pending)
        {
            var userId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            var seatCategoryId = Guid.NewGuid();

            var user = new User
            {
                Id = userId,
                FullName = "Nguyen Van A",
                Email = "user_test@example.com",
                PasswordHash = "hashed"
            };

            var showtime = new Showtime
            {
                Id = showtimeId,
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(3)
            };

            var seat1 = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                SeatCategoryId = seatCategoryId,
                Row = "A",
                SeatNumber = 1,
                Status = "AVAILABLE"
            };

            var seat2 = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                SeatCategoryId = seatCategoryId,
                Row = "A",
                SeatNumber = 2,
                Status = "AVAILABLE"
            };

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ShowtimeId = showtime.Id,
                Status = initialStatus,
                TotalAmount = totalAmount,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var orderItem1 = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = seat1.Id,
                Price = totalAmount / 2
            };

            var orderItem2 = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = seat2.Id,
                Price = totalAmount / 2
            };

            long orderCode = 1000000001L;
            var paymentTx = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = orderCode,
                Amount = totalAmount,
                PaymentUrl = "https://pay.example.com/checkout/1000000001",
                TransactionId = "TXN_INIT_1001",
                Status = initialStatus == OrderStatus.Paid ? "PAID" : "PENDING"
            };

            var hold1 = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seat1.Id,
                UserId = user.Id,
                Status = initialStatus == OrderStatus.Paid ? "CONVERTED" : "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15)
            };

            var hold2 = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seat2.Id,
                UserId = user.Id,
                Status = initialStatus == OrderStatus.Paid ? "CONVERTED" : "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15)
            };

            _context.Users.Add(user);
            _context.Seats.AddRange(seat1, seat2);
            _context.Orders.Add(order);
            _context.OrderItems.AddRange(orderItem1, orderItem2);
            _context.PaymentTransactions.Add(paymentTx);
            _context.SeatHold.AddRange(hold1, hold2);
            await _context.SaveChangesAsync();

            return (user, showtime, new List<Seat> { seat1, seat2 }, order, paymentTx, new List<SeatHolds> { hold1, hold2 });
        }

        #endregion

        #region 1. Single DB Transaction: Order Paid, Seats SOLD, SeatHolds CONVERTED

        [Fact]
        public async Task HandlePaymentResult_SuccessfulPayment_UpdatesOrderPaid_MarksSeatsSold_ConvertsHolds_InOneTransaction()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(500000);
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var resultDto = PaymentResultDto.CreateSuccess(
                orderCode: paymentTx.OrderCode,
                amount: 500000,
                transactionId: "TXN_GATEWAY_SUCCESS_01",
                paidAt: DateTimeOffset.UtcNow,
                orderId: order.Id.ToString()
            );

            // Act
            var executionResult = await paymentService.HandlePaymentResultAsync(resultDto);

            // Assert
            Assert.True(executionResult.Success);
            Assert.Equal("Paid", executionResult.OrderStatus);
            Assert.False(executionResult.IsAlreadyProcessed);

            // 1. Order Status -> Paid
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Paid, updatedOrder.Status);

            // 2. PaymentTransaction Status -> PAID
            var updatedTx = await _context.PaymentTransactions.FirstOrDefaultAsync(pt => pt.OrderId == order.Id);
            Assert.NotNull(updatedTx);
            Assert.Equal("PAID", updatedTx.Status);
            Assert.Equal("TXN_GATEWAY_SUCCESS_01", updatedTx.TransactionId);

            // 3. Seats -> SOLD
            var updatedSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(updatedSeats, s => Assert.Equal("SOLD", s.Status));

            // 4. SeatHolds -> CONVERTED
            var updatedHolds = await _context.SeatHold.Where(h => holds.Select(x => x.Id).Contains(h.Id)).ToListAsync();
            Assert.All(updatedHolds, h => Assert.Equal("CONVERTED", h.Status));
        }

        #endregion

        #region 2. Amount Reconciliation (Đối chiếu số tiền) & Rollback

        [Fact]
        public async Task HandlePaymentResult_AmountMismatch_Rejects_DoesNotUpdateOrder_SeatsOrHolds()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(500000);
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            // Gateway gửi về số tiền 400.000 thay vì 500.000
            var resultDto = PaymentResultDto.CreateSuccess(
                orderCode: paymentTx.OrderCode,
                amount: 400000,
                transactionId: "TXN_TAMPERED_AMOUNT",
                paidAt: DateTimeOffset.UtcNow,
                orderId: order.Id.ToString()
            );

            // Act
            var executionResult = await paymentService.HandlePaymentResultAsync(resultDto);

            // Assert
            Assert.False(executionResult.Success);
            Assert.Contains("không khớp", executionResult.Message);

            // Order KHÔNG chuyển sang Paid
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Pending, updatedOrder.Status);

            // PaymentTransaction KHÔNG chuyển sang PAID
            var updatedTx = await _context.PaymentTransactions.FirstOrDefaultAsync(pt => pt.OrderId == order.Id);
            Assert.NotNull(updatedTx);
            Assert.Equal("PENDING", updatedTx.Status);

            // Ghế KHÔNG chuyển sang SOLD
            var updatedSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(updatedSeats, s => Assert.Equal("AVAILABLE", s.Status));

            // Giữ chỗ KHÔNG chuyển sang CONVERTED
            var updatedHolds = await _context.SeatHold.Where(h => holds.Select(x => x.Id).Contains(h.Id)).ToListAsync();
            Assert.All(updatedHolds, h => Assert.Equal("ACTIVE", h.Status));
        }

        #endregion

        #region 3. Idempotency (Trùng lặp / gọi lại không sinh lỗi, không nhân đôi side effects)

        [Fact]
        public async Task HandlePaymentResult_WhenOrderAlreadyPaid_ReturnsSuccessIdempotentlyWithoutSideEffects()
        {
            // Arrange: Đơn hàng đã ở trạng thái Paid từ trước
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(500000, OrderStatus.Paid);

            // Đặt sẵn trạng thái ghế là SOLD và hold là CONVERTED
            foreach (var seat in seats) seat.Status = "SOLD";
            foreach (var hold in holds) hold.Status = "CONVERTED";
            await _context.SaveChangesAsync();

            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var duplicateResult = PaymentResultDto.CreateSuccess(
                orderCode: paymentTx.OrderCode,
                amount: 500000,
                transactionId: "TXN_DUPLICATE",
                paidAt: DateTimeOffset.UtcNow,
                orderId: order.Id.ToString()
            );

            // Act
            var executionResult = await paymentService.HandlePaymentResultAsync(duplicateResult);

            // Assert
            Assert.True(executionResult.Success);
            Assert.True(executionResult.IsAlreadyProcessed);
            Assert.Contains("trước đó", executionResult.Message);

            var dbOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(dbOrder);
            Assert.Equal(OrderStatus.Paid, dbOrder.Status);
        }

        #endregion

        #region 4. Failed / Cancelled Result Handling

        [Fact]
        public async Task HandlePaymentResult_CancelledResult_UpdatesOrderToCancelled_DoesNotMarkSeatsSold()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(500000);
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var cancelResult = new PaymentResultDto
            {
                Success = false,
                OrderId = order.Id.ToString(),
                OrderCode = paymentTx.OrderCode,
                Amount = 500000,
                Status = PaymentStatus.Cancelled,
                ErrorMessage = "Người dùng hủy thanh toán."
            };

            // Act
            var executionResult = await paymentService.HandlePaymentResultAsync(cancelResult);

            // Assert
            Assert.False(executionResult.Success);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Cancelled, updatedOrder.Status);

            var updatedTx = await _context.PaymentTransactions.FirstOrDefaultAsync(pt => pt.OrderId == order.Id);
            Assert.NotNull(updatedTx);
            Assert.Equal("CANCELLED", updatedTx.Status);

            // Ghế KHÔNG bị đổi thành SOLD
            var updatedSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(updatedSeats, s => Assert.Equal("AVAILABLE", s.Status));
        }

        #endregion

        #region 5. Webhook Flow Integration with Handler

        [Fact]
        public async Task ProcessPaymentWebhook_ValidWebhook_ExecutesHandlerAndReturnsTrue()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(350000);
            var fakeGateway = new FakePaymentGateway
            {
                ShouldVerifySignature = true,
                CustomWebhookParseResult = WebhookParseResult.CreateSuccess(
                    orderCode: paymentTx.OrderCode,
                    amount: 350000,
                    transactionId: "TXN_WEBHOOK_01",
                    paidAt: DateTimeOffset.UtcNow,
                    orderId: order.Id.ToString()
                )
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            // Act
            bool processed = await paymentService.ProcessPaymentWebhookAsync("valid_payload", "valid_sig");

            // Assert
            Assert.True(processed);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Paid, updatedOrder.Status);

            var updatedSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(updatedSeats, s => Assert.Equal("SOLD", s.Status));

            var updatedHolds = await _context.SeatHold.Where(h => holds.Select(x => x.Id).Contains(h.Id)).ToListAsync();
            Assert.All(updatedHolds, h => Assert.Equal("CONVERTED", h.Status));
        }

        [Fact]
        public async Task ProcessPaymentWebhook_InvalidSignature_ReturnsFalse_DoesNotTouchDatabase()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(350000);
            var fakeGateway = new FakePaymentGateway
            {
                ShouldVerifySignature = false
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            // Act
            bool processed = await paymentService.ProcessPaymentWebhookAsync("fake_payload", "invalid_sig");

            // Assert
            Assert.False(processed);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Pending, updatedOrder.Status);

            var updatedSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(updatedSeats, s => Assert.Equal("AVAILABLE", s.Status));
        }

        #endregion

        #region 6. Return / Query Flow: Server-side Verification (Không tin query client)

        [Fact]
        public async Task VerifyAndProcessPaymentReturn_UntrustedClientStatus_GatewayConfirmsPaid_UpdatesOrderAndSeats()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(600000);
            var fakeGateway = new FakePaymentGateway
            {
                // Giả lập gateway xác nhận đơn này đã thanh toán thành công
                SimulatedQueryResult = PaymentResultDto.CreateSuccess(
                    orderCode: paymentTx.OrderCode,
                    amount: 600000,
                    transactionId: "TXN_GATEWAY_QUERY_OK",
                    paidAt: DateTimeOffset.UtcNow
                )
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            // Client gửi query param có kèm status
            var query = new PaymentReturnQueryDto
            {
                OrderId = order.Id,
                OrderCode = paymentTx.OrderCode,
                Status = "PAID"
            };

            // Act
            var result = await paymentService.VerifyAndProcessPaymentReturnAsync(query);

            // Assert
            Assert.True(result.Success);
            Assert.Equal("Paid", result.OrderStatus);

            // Server-side đã cập nhật Order và Ghế
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Paid, updatedOrder.Status);

            var updatedSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(updatedSeats, s => Assert.Equal("SOLD", s.Status));

            var updatedHolds = await _context.SeatHold.Where(h => holds.Select(x => x.Id).Contains(h.Id)).ToListAsync();
            Assert.All(updatedHolds, h => Assert.Equal("CONVERTED", h.Status));
        }

        [Fact]
        public async Task VerifyAndProcessPaymentReturn_ClientClaimsPaid_GatewayVerificationFails_RejectsAndKeepsPending()
        {
            // Arrange: Kẻ gian hoặc client tự gọi return với status=PAID nhưng gateway không xác nhận
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(600000);
            var fakeGateway = new FakePaymentGateway
            {
                // Gateway trả về null (chưa thanh toán / không tìm thấy giao dịch)
                SimulatedQueryResult = null
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var fakeClientQuery = new PaymentReturnQueryDto
            {
                OrderId = order.Id,
                OrderCode = paymentTx.OrderCode,
                Status = "PAID" // Giả mạo hoặc client tự biên
            };

            // Act
            var result = await paymentService.VerifyAndProcessPaymentReturnAsync(fakeClientQuery);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Không thể xác minh", result.Message);

            // Database TUYỆT ĐỐI không bị thay đổi trạng thái sang Paid
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Pending, updatedOrder.Status);

            var updatedSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(updatedSeats, s => Assert.Equal("AVAILABLE", s.Status));

            var updatedHolds = await _context.SeatHold.Where(h => holds.Select(x => x.Id).Contains(h.Id)).ToListAsync();
            Assert.All(updatedHolds, h => Assert.Equal("ACTIVE", h.Status));
        }

        [Fact]
        public async Task VerifyAndProcessPaymentReturn_GatewayReturnsAmountMismatch_RejectsAndRollbacks()
        {
            // Arrange: Gateway xác nhận thành công nhưng số tiền bị lệch
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(600000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedQueryResult = PaymentResultDto.CreateSuccess(
                    orderCode: paymentTx.OrderCode,
                    amount: 550000, // Lệch số tiền
                    transactionId: "TXN_MISMATCH",
                    paidAt: DateTimeOffset.UtcNow
                )
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var query = new PaymentReturnQueryDto
            {
                OrderId = order.Id,
                OrderCode = paymentTx.OrderCode
            };

            // Act
            var result = await paymentService.VerifyAndProcessPaymentReturnAsync(query);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("không khớp", result.Message);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Pending, updatedOrder.Status);
        }

        [Fact]
        public async Task VerifyAndProcessPaymentReturn_OrderAlreadyPaidInDb_ReturnsSuccessImmediately()
        {
            // Arrange: Đơn hàng đã được webhook xử lý trước đó sang Paid
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(600000, OrderStatus.Paid);
            var fakeGateway = new FakePaymentGateway(); // Không cần query gateway

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var query = new PaymentReturnQueryDto
            {
                OrderId = order.Id,
                OrderCode = paymentTx.OrderCode
            };

            // Act
            var result = await paymentService.VerifyAndProcessPaymentReturnAsync(query);

            // Assert
            Assert.True(result.Success);
            Assert.True(result.IsAlreadyProcessed);
        }

        #endregion

        #region 7. Controller Endpoints Integration

        [Fact]
        public async Task PaymentsController_HandlePaymentReturn_ReturnsOkWhenVerified()
        {
            // Arrange
            var (_, _, _, order, paymentTx, _) = await SeedFullOrderGraphAsync(400000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedQueryResult = PaymentResultDto.CreateSuccess(
                    orderCode: paymentTx.OrderCode,
                    amount: 400000,
                    transactionId: "TXN_RETURN_OK",
                    paidAt: DateTimeOffset.UtcNow
                )
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);
            var controller = new PaymentsController(paymentService, NullLogger<PaymentsController>.Instance, _context);

            var query = new PaymentReturnQueryDto
            {
                OrderId = order.Id,
                OrderCode = paymentTx.OrderCode
            };

            // Act
            var response = await controller.HandlePaymentReturn(query);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(response);
            var apiResponse = Assert.IsType<ApiResponse<PaymentExecutionResult>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.NotNull(apiResponse.Data);
            Assert.True(apiResponse.Data.Success);
        }

        [Fact]
        public async Task PaymentsController_HandlePaymentReturn_ReturnsBadRequestWhenUnverified()
        {
            // Arrange
            var (_, _, _, order, paymentTx, _) = await SeedFullOrderGraphAsync(400000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedQueryResult = null // Không xác nhận được
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);
            var controller = new PaymentsController(paymentService, NullLogger<PaymentsController>.Instance, _context);

            var query = new PaymentReturnQueryDto
            {
                OrderId = order.Id,
                OrderCode = paymentTx.OrderCode
            };

            // Act
            var response = await controller.HandlePaymentReturn(query);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(response);
            var apiResponse = Assert.IsType<ApiResponse<PaymentExecutionResult>>(badRequestResult.Value);
            Assert.False(apiResponse.Success);
        }

        [Fact]
        public async Task PaymentsController_GetAndVerifyPaymentStatus_ReturnsVerifiedStatus()
        {
            // Arrange
            var (user, _, _, order, paymentTx, _) = await SeedFullOrderGraphAsync(400000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedQueryResult = PaymentResultDto.CreateSuccess(
                    orderCode: paymentTx.OrderCode,
                    amount: 400000,
                    transactionId: "TXN_STATUS_OK",
                    paidAt: DateTimeOffset.UtcNow
                )
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);
            var controller = new PaymentsController(paymentService, NullLogger<PaymentsController>.Instance, _context);

            var userPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
            }, "TestAuth"));

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = userPrincipal }
            };

            // Act
            var response = await controller.GetAndVerifyPaymentStatus(order.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(response);
            var apiResponse = Assert.IsType<ApiResponse<PaymentExecutionResult>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.NotNull(apiResponse.Data);
            Assert.True(apiResponse.Data.Success);
            Assert.Equal("Paid", apiResponse.Data.OrderStatus);
        }

        #endregion

        #region 8. Edge Cases & Isolation

        [Fact]
        public async Task HandlePaymentResult_WrongPaymentReference_DoesNotTouchExistingOrders()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(300000);
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var foreignResult = PaymentResultDto.CreateSuccess(
                orderCode: 9999999999L, // Không tồn tại trong hệ thống
                amount: 300000,
                transactionId: "TXN_FOREIGN",
                paidAt: DateTimeOffset.UtcNow
            );

            // Act
            var result = await paymentService.HandlePaymentResultAsync(foreignResult);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Không tìm thấy đơn hàng", result.Message);

            // Đơn hàng hiện có trong hệ thống không bị ảnh hưởng
            var existingOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(existingOrder);
            Assert.Equal(OrderStatus.Pending, existingOrder.Status);

            var existingSeats = await _context.Seats.Where(s => seats.Select(x => x.Id).Contains(s.Id)).ToListAsync();
            Assert.All(existingSeats, s => Assert.Equal("AVAILABLE", s.Status));
        }

        [Fact]
        public async Task HandlePaymentResult_WhenCancelledCancellationToken_ThrowsAndRollsBack()
        {
            // Arrange
            var (_, _, seats, order, paymentTx, holds) = await SeedFullOrderGraphAsync(300000);
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance);

            var resultDto = PaymentResultDto.CreateSuccess(
                orderCode: paymentTx.OrderCode,
                amount: 300000,
                transactionId: "TXN_CANCELLED_TOKEN",
                paidAt: DateTimeOffset.UtcNow,
                orderId: order.Id.ToString()
            );

            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel(); // Token bị hủy

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await paymentService.HandlePaymentResultAsync(resultDto, cts.Token);
            });

            // Sau khi rollback, trạng thái trong DB không bị thay đổi sang Paid
            var refreshedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(refreshedOrder);
            Assert.Equal(OrderStatus.Pending, refreshedOrder.Status);
        }

        #endregion
    }
}
