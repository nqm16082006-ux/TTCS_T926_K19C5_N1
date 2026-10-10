
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using EventTicketBooking.Api.Services.Interfaces;
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
    /// Bộ kiểm thử cho Task T-48 — Kiểm tra chữ ký webhook
    /// trước khi thực hiện business processing.
    /// </summary>
    public class WebhookSignatureVerificationTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly string _testChecksumKey = "test_secret_checksum_key_889900";

        public WebhookSignatureVerificationTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x =>
                    x.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId
                            .TransactionIgnoredWarning))
                .Options;

            _context = new AppDbContext(options);
            _context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private async Task<(Order order, PaymentTransaction tx)> SeedPendingOrderAsync(
            long orderCode = 123456,
            int amount = 100000)
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
                CreatedAt = DateTimeOffset.UtcNow,
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

        private (
            PaymentsController Controller,
            FakeLogger<PaymentsController> Logger
        ) CreateController(
            PaymentService paymentService,
            IPaymentGateway paymentGateway,
            string payload,
            string? signatureHeaderName = null,
            string? signatureHeaderValue = null)
        {
            var httpContext = new DefaultHttpContext();

            var stream = new MemoryStream(
                Encoding.UTF8.GetBytes(payload));

            httpContext.Request.Body = stream;

            // Mock remote IP để kiểm tra structured rejection log.
            httpContext.Connection.RemoteIpAddress =
                System.Net.IPAddress.Parse("203.0.113.10");

            if (!string.IsNullOrEmpty(signatureHeaderName) &&
                !string.IsNullOrEmpty(signatureHeaderValue))
            {
                httpContext.Request.Headers[signatureHeaderName] =
                    signatureHeaderValue;
            }

            var logger = new FakeLogger<PaymentsController>();

            var controller = new PaymentsController(
                paymentService,
                logger,
                _context,
                paymentGateway)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };

            return (controller, logger);
        }

        #region T-48 Signature Verification Tests

        /// <summary>
        /// Signature hợp lệ phải cho phép webhook tiếp tục xử lý.
        /// </summary>
        [Fact]
        public async Task Test1_ValidSignature_ProcessesWebhook_ReturnsHttp200Ok()
        {
            // Arrange
            var (order, _) =
                await SeedPendingOrderAsync(123456, 100000);

            string dataSignString =
                "amount=100000&orderCode=123456";

            string validSignature =
                ComputeHmacSha256(
                    dataSignString,
                    _testChecksumKey);

            string payload =
                $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":100000}},\"signature\":\"{validSignature}\"}}";

            var payosOptions =
                Options.Create(
                    new PayOSOptions
                    {
                        ChecksumKey = _testChecksumKey
                    });

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    payosOptions,
                    NullLogger<PayOSPaymentGateway>.Instance);

            var paymentService =
                new PaymentService(
                    _context,
                    gateway,
                    NullLogger<PaymentService>.Instance, new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var (controller, _) =
                CreateController(
                    paymentService,
                    gateway,
                    payload,
                    "x-signature",
                    validSignature);

            // Act
            var response =
                await controller.HandlePaymentWebhook();

            // Assert
            var okResult =
                Assert.IsType<OkObjectResult>(response);

            Assert.Equal(
                StatusCodes.Status200OK,
                okResult.StatusCode);

            var updatedOrder =
                await _context.Orders.FindAsync(order.Id);

            Assert.NotNull(updatedOrder);
            Assert.Equal(
                OrderStatus.Paid,
                updatedOrder.Status);
        }

        /// <summary>
        /// Chỉ sửa một ký tự payload phải làm signature không hợp lệ
        /// và business logic không được thực thi.
        /// </summary>
        [Fact]
        public async Task Test2_TamperedPayloadByOneChar_ReturnsHttp401Unauthorized_AndDoesNotExecuteBusinessLogic()
        {
            // Arrange
            var (order, tx) =
                await SeedPendingOrderAsync(123456, 100000);

            string originalDataString =
                "amount=100000&orderCode=123456";

            string signatureOfPayloadA =
                ComputeHmacSha256(
                    originalDataString,
                    _testChecksumKey);

            string tamperedPayload =
                $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":100001}},\"signature\":\"{signatureOfPayloadA}\"}}";

            var payosOptions =
                Options.Create(
                    new PayOSOptions
                    {
                        ChecksumKey = _testChecksumKey
                    });

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    payosOptions,
                    NullLogger<PayOSPaymentGateway>.Instance);

            var paymentService =
                new PaymentService(
                    _context,
                    gateway,
                    NullLogger<PaymentService>.Instance, new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var (controller, _) =
                CreateController(
                    paymentService,
                    gateway,
                    tamperedPayload,
                    "x-signature",
                    signatureOfPayloadA);

            // Act
            var response =
                await controller.HandlePaymentWebhook();

            // Assert
            var unauthResult =
                Assert.IsType<UnauthorizedObjectResult>(response);

            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                unauthResult.StatusCode);

            var eventCount =
                await _context.PaymentEvents.CountAsync();

            Assert.Equal(0, eventCount);

            var currentOrder =
                await _context.Orders.FindAsync(order.Id);

            Assert.NotNull(currentOrder);

            Assert.Equal(
                OrderStatus.Pending,
                currentOrder.Status);

            var currentTx =
                await _context.PaymentTransactions.FindAsync(tx.Id);

            Assert.NotNull(currentTx);

            Assert.Equal(
                "PENDING",
                currentTx.Status);
        }

        /// <summary>
        /// Signature sai phải trả về HTTP 401.
        /// </summary>
        [Fact]
        public async Task Test3_InvalidSignature_ReturnsHttp401Unauthorized()
        {
            // Arrange
            var (order, _) =
                await SeedPendingOrderAsync(123456, 100000);

            string payload =
                "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":123456,\"amount\":100000}}";

            string invalidSignature =
                "invalid_signature_hex_1234567890";

            var payosOptions =
                Options.Create(
                    new PayOSOptions
                    {
                        ChecksumKey = _testChecksumKey
                    });

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    payosOptions,
                    NullLogger<PayOSPaymentGateway>.Instance);

            var paymentService =
                new PaymentService(
                    _context,
                    gateway,
                    NullLogger<PaymentService>.Instance, new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var (controller, _) =
                CreateController(
                    paymentService,
                    gateway,
                    payload,
                    "x-signature",
                    invalidSignature);

            // Act
            var response =
                await controller.HandlePaymentWebhook();

            // Assert
            var unauthResult =
                Assert.IsType<UnauthorizedObjectResult>(response);

            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                unauthResult.StatusCode);

            var currentOrder =
                await _context.Orders.FindAsync(order.Id);

            Assert.NotNull(currentOrder);

            Assert.Equal(
                OrderStatus.Pending,
                currentOrder.Status);
        }

        /// <summary>
        /// Không có signature phải trả về HTTP 401.
        /// </summary>
        [Fact]
        public async Task Test4_MissingSignature_ReturnsHttp401Unauthorized()
        {
            // Arrange
            var (order, _) =
                await SeedPendingOrderAsync(123456, 100000);

            string payload =
                "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":123456,\"amount\":100000}}";

            var payosOptions =
                Options.Create(
                    new PayOSOptions
                    {
                        ChecksumKey = _testChecksumKey
                    });

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    payosOptions,
                    NullLogger<PayOSPaymentGateway>.Instance);

            var paymentService =
                new PaymentService(
                    _context,
                    gateway,
                    NullLogger<PaymentService>.Instance, new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var (controller, _) =
                CreateController(
                    paymentService,
                    gateway,
                    payload);

            // Act
            var response =
                await controller.HandlePaymentWebhook();

            // Assert
            var unauthResult =
                Assert.IsType<UnauthorizedObjectResult>(response);

            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                unauthResult.StatusCode);

            var currentOrder =
                await _context.Orders.FindAsync(order.Id);

            Assert.NotNull(currentOrder);

            Assert.Equal(
                OrderStatus.Pending,
                currentOrder.Status);
        }

        /// <summary>
        /// VerifyWebhookSignature phải xử lý signature theo constant-time comparison.
        /// </summary>
        [Fact]
        public void Test5_ConstantTimeVerification_ValidAndTamperedSignature()
        {
            // Arrange
            var payosOptions =
                Options.Create(
                    new PayOSOptions
                    {
                        ChecksumKey = _testChecksumKey
                    });

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    payosOptions,
                    NullLogger<PayOSPaymentGateway>.Instance);

            string dataString =
                "amount=500000&orderCode=998877";

            string validSignature =
                ComputeHmacSha256(
                    dataString,
                    _testChecksumKey);

            string validPayload =
                $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":998877,\"amount\":500000}},\"signature\":\"{validSignature}\"}}";

            string tamperedPayload =
                $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":998877,\"amount\":500001}},\"signature\":\"{validSignature}\"}}";

            // Act
            bool isValidResult =
                gateway.VerifyWebhookSignature(
                    validPayload,
                    validSignature);

            bool isTamperedResult =
                gateway.VerifyWebhookSignature(
                    tamperedPayload,
                    validSignature);

            bool isFakeSignatureResult =
                gateway.VerifyWebhookSignature(
                    validPayload,
                    "bad_signature");

            // Assert
            Assert.True(
                isValidResult,
                "Chữ ký hợp lệ phải trả về true");

            Assert.False(
                isTamperedResult,
                "Payload bị sửa 1 ký tự phải trả về false");

            Assert.False(
                isFakeSignatureResult,
                "Chữ ký giả phải trả về false");
        }

        #endregion

        #region Structured Rejection Logging Tests

        /// <summary>
        /// Missing signature phải được ghi structured rejection log.
        /// </summary>
        [Fact]
        public async Task Test6_MissingSignature_Returns401_AndLogsRejection()
        {
            // Arrange
            var (_, _) =
                await SeedPendingOrderAsync(123456, 100000);

            string payload =
                "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":123456,\"amount\":100000}}";

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    Options.Create(
                        new PayOSOptions
                        {
                            ChecksumKey = _testChecksumKey
                        }),
                    new FakeLogger<PayOSPaymentGateway>());

            var paymentService =
                new PaymentService(
                    _context,
                    gateway,
                    new FakeLogger<PaymentService>(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var (controller, logger) =
                CreateController(
                    paymentService,
                    gateway,
                    payload);

            // Act
            var response =
                await controller.HandlePaymentWebhook();

            // Assert
            var unauthResult =
                Assert.IsType<UnauthorizedObjectResult>(response);

            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                unauthResult.StatusCode);

            // Controller phải ghi rejection log.
            var log =
                Assert.Single(
                    logger.Logs,
                    l => l.Message.Contains("Webhook rejected"));

            Assert.Contains(
                "StatusCode=401",
                log.Message);

            Assert.Contains(
                "SourceAddress=203.0.113.10",
                log.Message);

            Assert.Contains(
                "Reason=MissingSignature",
                log.Message);

            // Không log payload nhạy cảm.
            Assert.DoesNotContain(
                payload,
                log.Message);
        }

        /// <summary>
        /// Invalid signature phải được ghi structured rejection log.
        /// </summary>
        [Fact]
        public async Task Test7_InvalidSignature_Returns401_AndLogsRejection()
        {
            // Arrange
            var (_, _) =
                await SeedPendingOrderAsync(123456, 100000);

            string payload =
                "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":123456,\"amount\":100000}}";

            string invalidSignature =
                "invalid_signature_hex";

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    Options.Create(
                        new PayOSOptions
                        {
                            ChecksumKey = _testChecksumKey
                        }),
                    new FakeLogger<PayOSPaymentGateway>());

            var paymentService =
                new PaymentService(
                    _context,
                    gateway,
                    new FakeLogger<PaymentService>(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var (controller, logger) =
                CreateController(
                    paymentService,
                    gateway,
                    payload,
                    "x-signature",
                    invalidSignature);

            // Act
            var response =
                await controller.HandlePaymentWebhook();

            // Assert
            var unauthResult =
                Assert.IsType<UnauthorizedObjectResult>(response);

            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                unauthResult.StatusCode);

            var log =
                Assert.Single(
                    logger.Logs,
                    l => l.Message.Contains("Webhook rejected"));

            Assert.Contains(
                "StatusCode=401",
                log.Message);

            Assert.Contains(
                "SourceAddress=203.0.113.10",
                log.Message);

            Assert.Contains(
                "Reason=InvalidSignature",
                log.Message);

            // Không log payload hoặc signature nhạy cảm.
            Assert.DoesNotContain(
                payload,
                log.Message);

            Assert.DoesNotContain(
                invalidSignature,
                log.Message);
        }

        /// <summary>
        /// Payload bị thay đổi phải trả 401 và ghi rejection log.
        /// </summary>
        [Fact]
        public async Task Test8_TamperedPayload_Returns401_AndLogsRejection()
        {
            // Arrange
            var (_, _) =
                await SeedPendingOrderAsync(123456, 100000);

            string dataSignString =
                "amount=100000&orderCode=123456";

            string validSignature =
                ComputeHmacSha256(
                    dataSignString,
                    _testChecksumKey);

            string tamperedPayload =
                $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":999999}},\"signature\":\"{validSignature}\"}}";

            var gateway =
                new PayOSPaymentGateway(
                    new System.Net.Http.HttpClient(),
                    Options.Create(
                        new PayOSOptions
                        {
                            ChecksumKey = _testChecksumKey
                        }),
                    new FakeLogger<PayOSPaymentGateway>());

            var paymentService =
                new PaymentService(
                    _context,
                    gateway,
                    new FakeLogger<PaymentService>(), new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var (controller, logger) =
                CreateController(
                    paymentService,
                    gateway,
                    tamperedPayload,
                    "x-signature",
                    validSignature);

            // Act
            var response =
                await controller.HandlePaymentWebhook();

            // Assert
            var unauthResult =
                Assert.IsType<UnauthorizedObjectResult>(response);

            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                unauthResult.StatusCode);

            var log =
                Assert.Single(
                    logger.Logs,
                    l => l.Message.Contains("Webhook rejected"));

            Assert.Contains(
                "StatusCode=401",
                log.Message);

            Assert.Contains(
                "SourceAddress=203.0.113.10",
                log.Message);

            Assert.Contains(
                "Reason=InvalidSignature",
                log.Message);

            // Không log dữ liệu nhạy cảm.
            Assert.DoesNotContain(
                tamperedPayload,
                log.Message);

            Assert.DoesNotContain(
                _testChecksumKey,
                log.Message);

            Assert.DoesNotContain(
                validSignature,
                log.Message);
        }
        #endregion
    }
}
