using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class PaymentGatewayTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly string _testChecksumKey = "test_checksum_key_1234567890_secret";

        public PaymentGatewayTests()
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

        #region 1. Create Payment Success & Failure

        [Fact]
        public async Task CreatePayment_Success_ReturnsValidPaymentUrl()
        {
            // Arrange: Fake gateway
            var fakeGateway = new FakePaymentGateway
            {
                ShouldSucceed = true,
                SimulatedPaymentUrl = "https://checkout.example.com/pay/123",
                SimulatedTransactionId = "TXN_SUCCESS_1"
            };

            var request = new PaymentCreationRequest
            {
                OrderId = Guid.NewGuid().ToString(),
                OrderCode = 12345678,
                Amount = 300000,
                Description = "Thanh toan ve test"
            };

            // Act
            var result = await fakeGateway.CreatePaymentAsync(request);

            // Assert
            Assert.True(result.Success);
            Assert.Equal("https://checkout.example.com/pay/123", result.PaymentUrl);
            Assert.Equal("TXN_SUCCESS_1", result.TransactionId);
            Assert.Null(result.ErrorMessage);
        }

        [Fact]
        public async Task CreatePayment_Failure_ReturnsFailureWithErrorMessage()
        {
            // Arrange
            var fakeGateway = new FakePaymentGateway
            {
                ShouldSucceed = false,
                SimulatedErrorMessage = "Cổng thanh toán đang bảo trì."
            };

            var request = new PaymentCreationRequest
            {
                OrderId = Guid.NewGuid().ToString(),
                OrderCode = 87654321,
                Amount = 250000
            };

            // Act
            var result = await fakeGateway.CreatePaymentAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Null(result.PaymentUrl);
            Assert.Equal("Cổng thanh toán đang bảo trì.", result.ErrorMessage);
        }

        [Fact]
        public async Task PayOSAdapter_ZeroAmount_ReturnsFailureDirectly()
        {
            // Arrange
            var payosOptions = Microsoft.Extensions.Options.Options.Create(new PayOSOptions
            {
                ChecksumKey = _testChecksumKey
            });
            var adapter = new PayOSPaymentGateway(new HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);

            var request = new PaymentCreationRequest
            {
                OrderId = Guid.NewGuid().ToString(),
                Amount = 0
            };

            // Act
            var result = await adapter.CreatePaymentAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Số tiền", result.ErrorMessage);
        }

        #endregion

        #region 2. Webhook Signature Verification

        [Fact]
        public void PayOSAdapter_VerifyWebhookSignature_ValidSignature_ReturnsTrue()
        {
            // Arrange
            var payosOptions = Microsoft.Extensions.Options.Options.Create(new PayOSOptions
            {
                ChecksumKey = _testChecksumKey
            });
            var adapter = new PayOSPaymentGateway(new HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);

            // Chuẩn bị payload data của PayOS
            // data string sắp xếp a-z: amount=250000&orderCode=1001
            string expectedDataString = "amount=250000&orderCode=1001";
            string validSignature = ComputeHmac(expectedDataString, _testChecksumKey);

            string webhookJson = $"{{\"code\":\"00\",\"desc\":\"success\",\"data\":{{\"orderCode\":1001,\"amount\":250000}},\"signature\":\"{validSignature}\"}}";

            // Act
            bool isValid = adapter.VerifyWebhookSignature(webhookJson, validSignature);

            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void PayOSAdapter_VerifyWebhookSignature_InvalidSignature_ReturnsFalse()
        {
            // Arrange
            var payosOptions = Microsoft.Extensions.Options.Options.Create(new PayOSOptions
            {
                ChecksumKey = _testChecksumKey
            });
            var adapter = new PayOSPaymentGateway(new HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);

            string webhookJson = "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":1001,\"amount\":250000}}";
            string tamperedSignature = "invalid_or_tampered_signature_hex";

            // Act
            bool isValid = adapter.VerifyWebhookSignature(webhookJson, tamperedSignature);

            // Assert
            Assert.False(isValid);
        }

        #endregion

        #region 3. Parse Webhook Data

        [Fact]
        public void PayOSAdapter_ParseWebhookData_ValidPayload_ReturnsCorrectParsedResult()
        {
            // Arrange
            var payosOptions = Microsoft.Extensions.Options.Options.Create(new PayOSOptions());
            var adapter = new PayOSPaymentGateway(new HttpClient(), payosOptions, NullLogger<PayOSPaymentGateway>.Instance);

            string webhookJson = "{\"code\":\"00\",\"desc\":\"success\",\"data\":{\"orderCode\":998877,\"amount\":450000,\"reference\":\"PAYOS_REF_01\",\"transactionDateTime\":\"2026-09-29T10:00:00+07:00\"}}";

            // Act
            var parseResult = adapter.ParseWebhookData(webhookJson);

            // Assert
            Assert.True(parseResult.IsValid);
            Assert.Equal(998877, parseResult.OrderCode);
            Assert.Equal(450000, parseResult.Amount);
            Assert.Equal(PaymentStatus.Success, parseResult.Status);
            Assert.Equal("PAYOS_REF_01", parseResult.TransactionId);
        }

        #endregion

        #region 4. Business Service runs with FakePaymentGateway

        [Fact]
        public async Task PaymentService_RunsWithFakePaymentGateway_CreatesPaymentSuccessfully()
        {
            // Arrange: Tạo Order trong DB
            var userId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = showtimeId,
                Status = OrderStatus.Pending,
                TotalAmount = 500000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var fakeGateway = new FakePaymentGateway
            {
                ShouldSucceed = true,
                SimulatedPaymentUrl = "https://fake-checkout.local/order/500k"
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create());

            // Act
            var result = await paymentService.CreatePaymentForOrderAsync(order.Id, userId);

            // Assert
            Assert.True(result.Success);
            Assert.Equal("https://fake-checkout.local/order/500k", result.PaymentUrl);
            Assert.Single(fakeGateway.RecordedCreationRequests);
            Assert.Equal(order.Id.ToString(), fakeGateway.RecordedCreationRequests[0].OrderId);
            Assert.Equal(500000, fakeGateway.RecordedCreationRequests[0].Amount);
        }

        #endregion

        #region 5. Swapping implementation without modifying Business Logic

        [Fact]
        public async Task PaymentService_SwappingGatewayImplementation_WorksWithoutModifyingBusinessLogic()
        {
            // Arrange: Tạo 2 implementations khác nhau của IPaymentGateway
            var fakeGateway1 = new FakePaymentGateway
            {
                SimulatedPaymentUrl = "https://gateway-one.vn/pay"
            };

            var fakeGateway2 = new FakePaymentGateway
            {
                SimulatedPaymentUrl = "https://gateway-two.vn/pay"
            };

            var userId = Guid.NewGuid();
            var order1 = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = Guid.NewGuid(),
                Status = OrderStatus.Pending,
                TotalAmount = 200000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            };
            var order2 = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = Guid.NewGuid(),
                Status = OrderStatus.Pending,
                TotalAmount = 300000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            };
            _context.Orders.AddRange(order1, order2);
            await _context.SaveChangesAsync();

            // Act 1: Dùng Gateway 1 cho Order 1
            var serviceWithGateway1 = new PaymentService(_context, fakeGateway1, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create());
            var res1 = await serviceWithGateway1.CreatePaymentForOrderAsync(order1.Id, userId);

            // Act 2: Đổi sang Gateway 2 cho Order 2 mà KHÔNG đổi một dòng code nào trong PaymentService
            var serviceWithGateway2 = new PaymentService(_context, fakeGateway2, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create());
            var res2 = await serviceWithGateway2.CreatePaymentForOrderAsync(order2.Id, userId);

            // Assert
            Assert.Equal("https://gateway-one.vn/pay", res1.PaymentUrl);
            Assert.Equal("https://gateway-two.vn/pay", res2.PaymentUrl);
        }

        #endregion

        #region 6. Webhook updates Order to Paid

        [Fact]
        public async Task ProcessPaymentWebhook_ValidSignatureAndSuccess_UpdatesOrderStatusToPaid()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = Guid.NewGuid(),
                Status = OrderStatus.Pending,
                TotalAmount = 400000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var fakeGateway = new FakePaymentGateway
            {
                ShouldVerifySignature = true,
                CustomWebhookParseResult = WebhookParseResult.CreateSuccess(
                    orderCode: null,
                    amount: 400000,
                    transactionId: "TXN_PAID_123",
                    paidAt: DateTimeOffset.UtcNow,
                    orderId: order.Id.ToString()
                )
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create());

            // Act
            bool isProcessed = await paymentService.ProcessPaymentWebhookAsync("valid_webhook_payload", "valid_signature");

            // Assert
            Assert.True(isProcessed);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        }

        [Fact]
        public async Task ProcessPaymentWebhook_InvalidSignature_DoesNotUpdateOrder()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = Guid.NewGuid(),
                Status = OrderStatus.Pending,
                TotalAmount = 400000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var fakeGateway = new FakePaymentGateway
            {
                ShouldVerifySignature = false // Chữ ký không hợp lệ
            };

            var paymentService = new PaymentService(_context, fakeGateway, NullLogger<PaymentService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create());

            // Act
            bool isProcessed = await paymentService.ProcessPaymentWebhookAsync("tampered_payload", "fake_signature");

            // Assert
            Assert.False(isProcessed);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder);
            Assert.Equal(OrderStatus.Pending, updatedOrder.Status); // Vẫn là Pending, không bị cập nhật
        }

        #endregion

        #region 7. Architecture Isolation: No SDK leakage in Business Layer

        [Fact]
        public void Architecture_BusinessLayer_DoesNotLeakProviderSdkOrSpecificTypes()
        {
            // Kiểm tra kiểu trong PaymentService
            var paymentServiceType = typeof(PaymentService);

            // Constructor của PaymentService chỉ được nhận IPaymentGateway, không được nhận PayOSPaymentGateway
            var constructors = paymentServiceType.GetConstructors();
            foreach (var ctor in constructors)
            {
                var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
                Assert.DoesNotContain(typeof(PayOSPaymentGateway), paramTypes);
                Assert.Contains(typeof(IPaymentGateway), paramTypes);
            }

            // OrdersController không được tham chiếu trực tiếp PayOS
            var ordersControllerType = typeof(OrdersController);
            var fields = ordersControllerType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                Assert.NotEqual(typeof(PayOSPaymentGateway), field.FieldType);
            }
        }

        #endregion

        private static string ComputeHmac(string data, string key)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);

            using var hmac = new HMACSHA256(keyBytes);
            byte[] hash = hmac.ComputeHash(dataBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
