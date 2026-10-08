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
using EventTicketBooking.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Test Suite toàn diện cho Task T-41: Tạo yêu cầu thanh toán, kiểm tra đơn hàng, idempotency và chuyển hướng Sandbox.
    /// </summary>
    public class PaymentCreationTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Guid _defaultUserId = Guid.NewGuid();
        private readonly Guid _defaultShowtimeId = Guid.NewGuid();

        public PaymentCreationTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private Order CreateTestOrder(int totalAmount = 350000, OrderStatus status = OrderStatus.Pending, int expiryMinutes = 15)
        {
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = _defaultUserId,
                ShowtimeId = _defaultShowtimeId,
                Status = status,
                TotalAmount = totalAmount,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes),
                OrderItems = new List<OrderItem>
                {
                    new OrderItem { Id = Guid.NewGuid(), SeatId = Guid.NewGuid(), Price = totalAmount }
                }
            };
            _context.Orders.Add(order);
            _context.SaveChanges();
            return order;
        }

        #region TEST 1: Create payment với Order hợp lệ
        [Fact]
        public async Task Test1_CreatePayment_ValidOrder_ReturnsSuccessAndPaymentUrl()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 400000);
            var fakeGateway = new FakePaymentGateway
            {
                ShouldSucceed = true,
                SimulatedPaymentUrl = "https://pay.payos.vn/web/test-checkout-url-123",
                SimulatedTransactionId = "TXN_T41_01"
            };
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.True(result.Success);
            Assert.Equal("https://pay.payos.vn/web/test-checkout-url-123", result.PaymentUrl);
            Assert.Equal("TXN_T41_01", result.TransactionId);
            Assert.NotNull(result.OrderCode);
            Assert.Single(fakeGateway.RecordedCreationRequests);
            Assert.Equal(400000, fakeGateway.RecordedCreationRequests[0].Amount);
        }
        #endregion

        #region TEST 2: Create payment với Order không tồn tại
        [Fact]
        public async Task Test2_CreatePayment_NonExistentOrder_Rejected_GatewayNotCalled()
        {
            // Arrange
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(Guid.NewGuid(), _defaultUserId);

            // Assert
            Assert.False(result.Success);
            Assert.Equal("Không tìm thấy đơn hàng.", result.ErrorMessage);
            Assert.Empty(fakeGateway.RecordedCreationRequests); // Gateway tuyệt đối không được gọi
        }
        #endregion

        #region TEST 3: Order đã hết hạn
        [Fact]
        public async Task Test3_CreatePayment_ExpiredOrder_Rejected_GatewayNotCalled()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 250000, expiryMinutes: -5); // Hết hạn 5 phút trước
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.False(result.Success);
            Assert.Equal("Đơn hàng đã hết hạn thanh toán.", result.ErrorMessage);
            Assert.Empty(fakeGateway.RecordedCreationRequests);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Expired, updatedOrder.Status);
        }
        #endregion

        #region TEST 4: Order đã Paid
        [Fact]
        public async Task Test4_CreatePayment_AlreadyPaidOrder_Rejected_NoPaymentCreated()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 300000, status: OrderStatus.Paid);
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("đã được thanh toán", result.ErrorMessage);
            Assert.Empty(fakeGateway.RecordedCreationRequests);
        }
        #endregion

        #region TEST 5: Create payment lần 1 lưu PaymentTransaction
        [Fact]
        public async Task Test5_CreatePayment_FirstCall_CreatesPaymentTransactionRecord()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 500000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedPaymentUrl = "https://pay.sandbox.vn/checkout-500k",
                SimulatedTransactionId = "TXN_FIRST_CALL"
            };
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.True(result.Success);

            var transaction = await _context.PaymentTransactions.FirstOrDefaultAsync(pt => pt.OrderId == order.Id);
            Assert.NotNull(transaction);
            Assert.Equal(500000, transaction.Amount);
            Assert.Equal("https://pay.sandbox.vn/checkout-500k", transaction.PaymentUrl);
            Assert.Equal("TXN_FIRST_CALL", transaction.TransactionId);
            Assert.Equal("PENDING", transaction.Status);
            Assert.Equal(result.OrderCode, transaction.OrderCode);
        }
        #endregion

        #region TEST 6: Create payment lần 2 cho cùng Order (Reuse reference, không tạo transaction mới)
        [Fact]
        public async Task Test6_CreatePayment_SecondCall_ReusesExistingTransactionAndDoesNotCallGatewayAgain()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 500000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedPaymentUrl = "https://pay.sandbox.vn/reused-checkout",
                SimulatedTransactionId = "TXN_REUSED"
            };
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act 1: Lần 1
            var res1 = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);
            Assert.True(res1.Success);

            // Act 2: Lần 2 (người dùng bấm lại thanh toán cho cùng đơn hàng)
            var res2 = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.True(res2.Success);
            Assert.Equal(res1.PaymentUrl, res2.PaymentUrl);
            Assert.Equal(res1.OrderCode, res2.OrderCode);
            Assert.Equal(res1.TransactionId, res2.TransactionId);

            // Gateway chỉ được gọi đúng 1 lần duy nhất
            Assert.Single(fakeGateway.RecordedCreationRequests);

            // Bảng PaymentTransactions chỉ có đúng 1 bản ghi
            var transactionCount = await _context.PaymentTransactions.CountAsync(pt => pt.OrderId == order.Id);
            Assert.Equal(1, transactionCount);
        }
        #endregion

        #region TEST 7: Double click / Duplicate concurrent requests (Race Condition)
        [Fact]
        public async Task Test7_CreatePayment_ConcurrentRequests_OnlyOneTransactionCreatedAndBothGetSameReference()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 600000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedPaymentUrl = "https://pay.sandbox.vn/concurrent-checkout"
            };
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act: Giả lập 2 request gần như đồng thời (User double-click)
            var task1 = paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);
            var task2 = paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            var results = await Task.WhenAll(task1, task2);

            // Assert
            Assert.True(results[0].Success);
            Assert.True(results[1].Success);
            Assert.Equal(results[0].PaymentUrl, results[1].PaymentUrl);
            Assert.Equal(results[0].OrderCode, results[1].OrderCode);

            // Database chỉ lưu duy nhất 1 PaymentTransaction cho Order này
            var transactionCount = await _context.PaymentTransactions.CountAsync(pt => pt.OrderId == order.Id);
            Assert.Equal(1, transactionCount);
        }
        #endregion

        #region TEST 8: Gateway failure
        [Fact]
        public async Task Test8_CreatePayment_GatewayFailure_ReturnsFailure_OrderRemainsPending()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 150000);
            var fakeGateway = new FakePaymentGateway
            {
                ShouldSucceed = false,
                SimulatedErrorMessage = "Cổng thanh toán Sandbox phản hồi lỗi 503."
            };
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.False(result.Success);
            Assert.Equal("Cổng thanh toán Sandbox phản hồi lỗi 503.", result.ErrorMessage);

            // Đơn hàng vẫn giữ nguyên trạng thái Pending
            var currentOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(currentOrder);
            Assert.Equal(OrderStatus.Pending, currentOrder.Status);

            // Không lưu PaymentTransaction rác khi gateway lỗi
            var tx = await _context.PaymentTransactions.FirstOrDefaultAsync(pt => pt.OrderId == order.Id);
            Assert.Null(tx);
        }
        #endregion

        #region TEST 9: Amount mapping đúng từ Backend
        [Fact]
        public async Task Test9_CreatePayment_AmountTakenFromBackendOrderTotalAmount()
        {
            // Arrange
            int expectedAmount = 750000;
            var order = CreateTestOrder(totalAmount: expectedAmount);
            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(expectedAmount, fakeGateway.RecordedCreationRequests[0].Amount);
        }
        #endregion

        #region TEST 10: FakePaymentGateway chạy 100% offline
        [Fact]
        public async Task Test10_CreatePayment_RunsCompletelyOfflineWithFakeGateway()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 200000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedPaymentUrl = "https://offline-sandbox.local/pay"
            };
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, _defaultUserId);

            // Assert
            Assert.True(result.Success);
            Assert.Equal("FakeGateway", fakeGateway.ProviderName);
            Assert.Equal("https://offline-sandbox.local/pay", result.PaymentUrl);
        }
        #endregion

        #region TEST 11: Security - Unauthorized User cannot pay for someone else's order
        [Fact]
        public async Task Test11_CreatePayment_UnauthorizedUser_RejectedWithForbiddenMessage()
        {
            // Arrange: Order của User A
            var order = CreateTestOrder(totalAmount: 300000);
            var maliciousUserId = Guid.NewGuid(); // User B

            var fakeGateway = new FakePaymentGateway();
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, maliciousUserId);

            // Assert
            Assert.False(result.Success);
            Assert.Equal("Bạn không có quyền thanh toán cho đơn hàng này.", result.ErrorMessage);
            Assert.Empty(fakeGateway.RecordedCreationRequests);
        }
        #endregion

        #region TEST 12: Controller Endpoint HTTP Status Codes
        [Fact]
        public async Task Test12_PaymentsController_CreatePayment_ReturnsExpectedStatusCodes()
        {
            // Arrange
            var order = CreateTestOrder(totalAmount: 100000);
            var fakeGateway = new FakePaymentGateway
            {
                SimulatedPaymentUrl = "https://pay.sandbox.vn/checkout-order"
            };
            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);
            var controller = new PaymentsController(paymentService, fakeGateway, NullLogger<PaymentsController>.Instance, _context);

            // Mock User Claims
            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, _defaultUserId.ToString()) };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };

            // 1. Success -> 200 OK
            var okResult = await controller.CreatePayment(order.Id) as OkObjectResult;
            Assert.NotNull(okResult);
            Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

            // 2. Order not found -> 404 NotFound
            var notFoundResult = await controller.CreatePayment(Guid.NewGuid()) as NotFoundObjectResult;
            Assert.NotNull(notFoundResult);
            Assert.Equal(StatusCodes.Status404NotFound, notFoundResult.StatusCode);

            // 3. User khác -> 403 Forbidden
            var otherUserClaims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(otherUserClaims, "TestAuth"));
            var forbiddenResult = await controller.CreatePayment(order.Id) as ObjectResult;
            Assert.NotNull(forbiddenResult);
            Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
        }
        #endregion
    }
}
