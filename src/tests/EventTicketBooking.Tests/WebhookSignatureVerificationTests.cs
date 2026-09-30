using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using EventTicketBooking.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class WebhookSignatureVerificationTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly string _testChecksumKey = "test_checksum_key_123456";

        public WebhookSignatureVerificationTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private async Task<(Order order, PaymentTransaction tx)> SeedPendingOrderAsync(int orderCode, int amount)
        {
            var order = new Order
            {
                Id = Guid.NewGuid(),
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

        private (PaymentsController Controller, FakeLogger<PaymentsController> Logger) CreateController(
            PaymentService paymentService,
            PayOSPaymentGateway gateway,
            string payload,
            string? signatureHeaderName = null,
            string? signatureHeaderValue = null)
        {
            var httpContext = new DefaultHttpContext();
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            httpContext.Request.Body = stream;

            // Mock remote IP
            httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");

            if (!string.IsNullOrEmpty(signatureHeaderName) && !string.IsNullOrEmpty(signatureHeaderValue))
            {
                httpContext.Request.Headers[signatureHeaderName] = signatureHeaderValue;
            }

            var logger = new FakeLogger<PaymentsController>();
            var controller = new PaymentsController(paymentService, gateway, logger)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };

            return (controller, logger);
        }

        [Fact]
        public async Task Test1_ValidSignature_ProcessesWebhook_ReturnsHttp200Ok()
        {
            var (order, tx) = await SeedPendingOrderAsync(123456, 100000);
            string dataSignString = "amount=100000&orderCode=123456";
            string validSignature = ComputeHmacSha256(dataSignString, _testChecksumKey);
            string payload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":100000}},\"signature\":\"{validSignature}\"}}";

            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey }), new FakeLogger<PayOSPaymentGateway>());
            var paymentService = new PaymentService(_context, gateway, new FakeLogger<PaymentService>());
            var (controller, logger) = CreateController(paymentService, gateway, payload, "x-signature", validSignature);

            var response = await controller.HandlePaymentWebhook();

            var okResult = Assert.IsType<OkObjectResult>(response);
            Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
            
            // Check order status changed
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.Equal(OrderStatus.Paid, updatedOrder!.Status);
            
            // Should NOT have rejection logs
            Assert.DoesNotContain(logger.Logs, l => l.Message.Contains("Webhook rejected"));
        }

        [Fact]
        public async Task Test2_MissingSignature_ReturnsHttp401_AndLogsRejection()
        {
            var (order, tx) = await SeedPendingOrderAsync(123456, 100000);
            string payload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":100000}}}}";

            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey }), new FakeLogger<PayOSPaymentGateway>());
            var paymentService = new PaymentService(_context, gateway, new FakeLogger<PaymentService>());
            var (controller, logger) = CreateController(paymentService, gateway, payload);

            var response = await controller.HandlePaymentWebhook();

            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(response);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);

            // Assert structured logging
            var log = Assert.Single(logger.Logs, l => l.Message.Contains("Webhook rejected"));
            Assert.Contains("StatusCode=401", log.Message);
            Assert.Contains("SourceAddress=203.0.113.10", log.Message);
            Assert.Contains("Reason=MissingSignature", log.Message);
            Assert.DoesNotContain(payload, log.Message); // Sensitive data not logged
        }

        [Fact]
        public async Task Test3_InvalidSignature_ReturnsHttp401_AndLogsRejection()
        {
            var (order, tx) = await SeedPendingOrderAsync(123456, 100000);
            string payload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":100000}}}}";
            string invalidSignature = "invalid_signature_hex";

            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey }), new FakeLogger<PayOSPaymentGateway>());
            var paymentService = new PaymentService(_context, gateway, new FakeLogger<PaymentService>());
            var (controller, logger) = CreateController(paymentService, gateway, payload, "x-signature", invalidSignature);

            var response = await controller.HandlePaymentWebhook();

            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(response);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);

            // Assert structured logging
            var log = Assert.Single(logger.Logs, l => l.Message.Contains("Webhook rejected"));
            Assert.Contains("StatusCode=401", log.Message);
            Assert.Contains("SourceAddress=203.0.113.10", log.Message);
            Assert.Contains("Reason=InvalidSignature", log.Message);
            Assert.DoesNotContain(payload, log.Message);
            Assert.DoesNotContain(invalidSignature, log.Message);
        }

        [Fact]
        public async Task Test4_TamperedPayload_ReturnsHttp401_AndLogsRejection()
        {
            var (order, tx) = await SeedPendingOrderAsync(123456, 100000);
            string dataSignString = "amount=100000&orderCode=123456";
            string validSignature = ComputeHmacSha256(dataSignString, _testChecksumKey);
            
            // Tampered payload amount to 999999
            string tamperedPayload = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":123456,\"amount\":999999}},\"signature\":\"{validSignature}\"}}";

            var gateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), Options.Create(new PayOSOptions { ChecksumKey = _testChecksumKey }), new FakeLogger<PayOSPaymentGateway>());
            var paymentService = new PaymentService(_context, gateway, new FakeLogger<PaymentService>());
            var (controller, logger) = CreateController(paymentService, gateway, tamperedPayload, "x-signature", validSignature);

            var response = await controller.HandlePaymentWebhook();

            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(response);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);

            // Assert structured logging
            var log = Assert.Single(logger.Logs, l => l.Message.Contains("Webhook rejected"));
            Assert.Contains("StatusCode=401", log.Message);
            Assert.Contains("SourceAddress=203.0.113.10", log.Message);
            Assert.Contains("Reason=InvalidSignature", log.Message);
            
            // Ensure no sensitive info logged
            Assert.DoesNotContain(tamperedPayload, log.Message);
            Assert.DoesNotContain(_testChecksumKey, log.Message);
            Assert.DoesNotContain(validSignature, log.Message);
        }
    }
}
