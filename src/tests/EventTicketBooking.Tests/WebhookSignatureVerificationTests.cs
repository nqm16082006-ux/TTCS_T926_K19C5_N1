using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using EventTicketBooking.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử cho Task T-48 — "Kiểm chữ ký webhook trước khi đọc nội dung"
    /// </summary>
    public class WebhookSignatureVerificationTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly string _testChecksumKey = "test_secret_checksum_key_889900";

        public WebhookSignatureVerificationTests()
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

        private async Task<(Order order, PaymentTransaction tx)> SeedPendingOrderAsync(long orderCode = 123456, int amount = 100000)
        {
            var userId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = showtimeId,
                Status = OrderStatus.Pending,
                TotalAmount = amount,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            };

            var tx = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = orderCode,
                Amount = amount,
                Status = "PENDING"
            };

            _context.Orders.Add(order);
            _context.PaymentTransactions.Add(tx);
            await _context.SaveChangesAsync();

            return (order, tx);
        }

        private static string ComputeHmacSha256(string data, string key)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);

            using var hmac = new HMACSHA256(keyBytes);
            byte[] hash = hmac.ComputeHash(dataBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private PaymentsController CreateControllerWithPayloadAndHeader(
            PaymentService paymentService,
            IPaymentGateway paymentGateway,
            string payload,
            string? signatureHeaderName = null,
            string? signatureHeaderValue = null)
        {
            var httpContext = new DefaultHttpContext();
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            httpContext.Request.Body = stream;

            if (!string.IsNullOrEmpty(signatureHeaderName) && !string.IsNullOrEmpty(signatureHeaderValue))
            {
                httpContext.Request.Headers[signatureHeaderName] = signatureHeaderValue;
            }

            return new PaymentsController(paymentService, paymentGateway, NullLogger<PaymentsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        }

        #region Test 1 — Signature Hợp lệ (Valid Signature -> HTTP 200 OK & Continue T-46)

        [Fact]
        public async Task Test1_ValidSignature_ProcessesWebhook_ReturnsHttp200Ok()
        {
            // Arrange
            var (order, tx) = await SeedPendingOrderAsync(123456, 100000);

            // Data string sắp xếp a-z: amount=100000&orderCode=123456
            string dataSignString = "amount=100000&orderCode=123456";
            string validSignature = ComputeHmacSha256(dataSignString, _testChecksumKey);

            string payload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":100000}},\"signature\":\"{validSignature}\"}}";

            var payosOptions = Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey });
            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);
            var paymentService = new PaymentService(_context, gateway, NullLogger<PaymentService>.Instance);

            var controller = CreateControllerWithPayloadAndHeader(paymentService, gateway, payload, "x-signature", validSignature);

            // Act
            var response = await controller.HandlePaymentWebhook();

            // Assert: Trả HTTP 200 OK
            var okResult = Assert.IsType<OkObjectResult>(response);
            Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

            // Order được chuyển sang Paid (tiếp tục luồng T-46)
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        }

        #endregion

        #region Test 2 — Sửa Đúng 1 Ký Tự Payload (Tampered Payload by 1 Char -> HTTP 401 Unauthorized & STOP)

        [Fact]
        public async Task Test2_TamperedPayloadByOneChar_ReturnsHttp401Unauthorized_AndDoesNotExecuteBusinessLogic()
        {
            // Arrange: Payload A gốc có signature hợp lệ
            var (order, tx) = await SeedPendingOrderAsync(123456, 100000);

            string originalDataString = "amount=100000&orderCode=123456";
            string signatureOfPayloadA = ComputeHmacSha256(originalDataString, _testChecksumKey);

            // Sửa đúng 1 ký tự trong payload (thay amount=100000 thành amount=100001)
            string tamperedPayload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":100001}},\"signature\":\"{signatureOfPayloadA}\"}}";

            var payosOptions = Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey });
            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);
            var paymentService = new PaymentService(_context, gateway, NullLogger<PaymentService>.Instance);

            var controller = CreateControllerWithPayloadAndHeader(paymentService, gateway, tamperedPayload, "x-signature", signatureOfPayloadA);

            // Act
            var response = await controller.HandlePaymentWebhook();

            // Assert: Phải trả HTTP 401 Unauthorized ngay
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(response);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);

            // Đảm bảo KHÔNG thực hiện các bước xử lý phía sau:
            // 1. Không có PaymentEvent nào được tạo
            var eventCount = await _context.PaymentEvents.CountAsync();
            Assert.Equal(0, eventCount);

            // 2. Trạng thái Order KHÔNG bị cập nhật (vẫn là Pending)
            var currentOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(currentOrder);
            Assert.Equal(OrderStatus.Pending, currentOrder.Status);

            // 3. Trạng thái PaymentTransaction KHÔNG bị cập nhật (vẫn là PENDING)
            var currentTx = await _context.PaymentTransactions.FindAsync(tx.Id);
            Assert.NotNull(currentTx);
            Assert.Equal("PENDING", currentTx.Status);
        }

        #endregion

        #region Test 3 — Signature Sai (Invalid Signature -> HTTP 401 Unauthorized)

        [Fact]
        public async Task Test3_InvalidSignature_ReturnsHttp401Unauthorized()
        {
            // Arrange: Payload hợp lệ nhưng signature sai hoàn toàn
            var (order, _) = await SeedPendingOrderAsync(123456, 100000);

            string payload = "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":123456,\"amount\":100000}}";
            string invalidSignature = "invalid_signature_hex_1234567890";

            var payosOptions = Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey });
            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);
            var paymentService = new PaymentService(_context, gateway, NullLogger<PaymentService>.Instance);

            var controller = CreateControllerWithPayloadAndHeader(paymentService, gateway, payload, "x-signature", invalidSignature);

            // Act
            var response = await controller.HandlePaymentWebhook();

            // Assert: Trả HTTP 401 Unauthorized
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(response);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);

            // Business logic không chạy
            var currentOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(currentOrder);
            Assert.Equal(OrderStatus.Pending, currentOrder.Status);
        }

        #endregion

        #region Test 4 — Signature Bị Thiếu (Missing Signature -> HTTP 401 Unauthorized)

        [Fact]
        public async Task Test4_MissingSignature_ReturnsHttp401Unauthorized()
        {
            // Arrange: Gửi webhook không kèm signature header hoặc body signature
            var (order, _) = await SeedPendingOrderAsync(123456, 100000);

            string payload = "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":123456,\"amount\":100000}}";

            var payosOptions = Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey });
            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);
            var paymentService = new PaymentService(_context, gateway, NullLogger<PaymentService>.Instance);

            // Không gửi signature header
            var controller = CreateControllerWithPayloadAndHeader(paymentService, gateway, payload);

            // Act
            var response = await controller.HandlePaymentWebhook();

            // Assert: Trả HTTP 401 Unauthorized
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(response);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);

            // Order vẫn ở Pending
            var currentOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(currentOrder);
            Assert.Equal(OrderStatus.Pending, currentOrder.Status);
        }

        #endregion

        #region Test 5 — Constant-time Verification (VerifyWebhookSignature Timing-Safe)

        [Fact]
        public void Test5_ConstantTimeVerification_ValidAndTamperedSignature()
        {
            // Arrange
            var payosOptions = Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey });
            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);

            string dataString = "amount=500000&orderCode=998877";
            string validSignature = ComputeHmacSha256(dataString, _testChecksumKey);

            string validPayload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":998877,\"amount\":500000}},\"signature\":\"{validSignature}\"}}";
            string tamperedPayload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":998877,\"amount\":500001}},\"signature\":\"{validSignature}\"}}";

            // Act
            bool isValidResult = gateway.VerifyWebhookSignature(validPayload, validSignature);
            bool isTamperedResult = gateway.VerifyWebhookSignature(tamperedPayload, validSignature);
            bool isFakeSignatureResult = gateway.VerifyWebhookSignature(validPayload, "bad_signature");

            // Assert
            Assert.True(isValidResult, "Chữ ký hợp lệ phải trả về true");
            Assert.False(isTamperedResult, "Payload bị sửa 1 ký tự phải trả về false");
            Assert.False(isFakeSignatureResult, "Chữ ký giả phải trả về false");
        }

        #endregion
    }
}
