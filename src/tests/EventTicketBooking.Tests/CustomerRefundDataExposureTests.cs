using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class CustomerRefundDataExposureTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static AppDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new AppDbContext(options);
        }

        private static ClaimsPrincipal CreateUserPrincipal(Guid userId, string role)
        {
            return new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role),
                new Claim("role", role),
                new Claim(ClaimTypes.Name, $"TestUser_{userId:N}")
            }, "TestAuth"));
        }

        private static async Task<(Order order, RefundRequest refundReq)> SeedOrderWithFailedRefundAsync(
            AppDbContext context, Guid ownerUserId)
        {
            var user = new User
            {
                Id = ownerUserId,
                Username = "customer_owner",
                Email = "customer@test.vn",
                FullName = "Nguyễn Văn Khách",
                PasswordHash = "hash"
            };
            context.Users.Add(user);

            var eventItem = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Live Concert S48",
                Location = "Hà Nội",
                TotalSeats = 100
            };
            context.Events.Add(eventItem);

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = eventItem.Id,
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(3)
            };
            context.Showtimes.Add(showtime);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = ownerUserId,
                ShowtimeId = showtime.Id,
                TotalAmount = 500_000,
                Status = OrderStatus.Cancelled,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            context.Orders.Add(order);

            var refundEntity = new Refund
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Amount = 500_000,
                Status = "Failed",
                FailureReason = "GATEWAY_TIMEOUT_504: Transaction timeout at gateway vnpay_mock secret=sec_key_xyz123",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            context.Refunds.Add(refundEntity);

            var refundReq = new RefundRequest
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = 1234567890L,
                TransactionId = "TXN_INTERNAL_9999",
                RefundTransactionId = "REF_INTERNAL_8888",
                Amount = 500_000,
                Currency = "VND",
                Reason = "CustomerCancelAndRefund",
                Status = RefundStatus.RetryScheduled,
                AttemptCount = 2,
                RetryCount = 1,
                MaxRetries = 3,
                ErrorCode = "GATEWAY_TIMEOUT_504",
                ErrorMessage = "Transaction timeout at gateway vnpay_mock secret=sec_key_xyz123",
                IsRetryable = true,
                LastAttemptedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            context.RefundRequests.Add(refundReq);

            await context.SaveChangesAsync();
            return (order, refundReq);
        }

        // =========================================================================
        // 1. Customer owner raw JSON does NOT contain internal error/gateway fields
        // =========================================================================
        [Fact]
        public async Task GetOrderSummary_CustomerOwner_RawJsonDoesNotExposeInternalGatewayOrRetryFields()
        {
            await using var context = CreateInMemoryDbContext();
            var ownerId = Guid.NewGuid();
            var (order, _) = await SeedOrderWithFailedRefundAsync(context, ownerId);

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreateUserPrincipal(ownerId, "Customer")
                    }
                }
            };

            var result = await controller.GetOrderSummary(order.Id);
            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value, JsonOptions);

            // Xác minh trường khách hàng cần thấy
            Assert.Contains("\"refundStatus\":\"RetryScheduled\"", json);
            Assert.Contains("\"refundAmount\":500000", json);
            Assert.Contains("\"refundRequestId\":", json);

            // Xác minh tuyệt đối KHÔNG chứa internal error, gateway reason hay retry internals
            Assert.DoesNotContain("refundErrorCode", json);
            Assert.DoesNotContain("refundErrorMessage", json);
            Assert.DoesNotContain("refundAttemptCount", json);
            Assert.DoesNotContain("refundRetryCount", json);
            Assert.DoesNotContain("refundNextRetryAt", json);
            Assert.DoesNotContain("refundLastAttemptedAt", json);
            Assert.DoesNotContain("refundFailureReason", json);
            Assert.DoesNotContain("GATEWAY_TIMEOUT_504", json);
            Assert.DoesNotContain("sec_key_xyz123", json);
        }

        [Fact]
        public async Task GetOrderRefund_CustomerOwner_ReturnsCustomerRefundDto_RawJsonHasNoTechnicalFields()
        {
            await using var context = CreateInMemoryDbContext();
            var ownerId = Guid.NewGuid();
            var (order, refundReq) = await SeedOrderWithFailedRefundAsync(context, ownerId);

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreateUserPrincipal(ownerId, "Customer")
                    }
                }
            };

            var result = await controller.GetOrderRefund(order.Id);
            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<CustomerRefundDto>>(ok.Value);

            Assert.True(response.Success);
            Assert.NotNull(response.Data);
            Assert.Equal(refundReq.Id, response.Data.Id);
            Assert.Equal(order.Id, response.Data.OrderId);
            Assert.Equal(500_000, response.Data.Amount);
            Assert.Equal("RetryScheduled", response.Data.Status);
            Assert.Equal("Đang thử lại", response.Data.CustomerStatusText);

            var json = JsonSerializer.Serialize(ok.Value, JsonOptions);

            // Raw JSON không chứa bất kỳ trường nhạy cảm nào
            Assert.DoesNotContain("errorCode", json);
            Assert.DoesNotContain("errorMessage", json);
            Assert.DoesNotContain("attemptCount", json);
            Assert.DoesNotContain("retryCount", json);
            Assert.DoesNotContain("nextRetryAt", json);
            Assert.DoesNotContain("lastAttemptedAt", json);
            Assert.DoesNotContain("idempotencyKey", json);
            Assert.DoesNotContain("transactionId", json);
            Assert.DoesNotContain("accountingNotified", json);
            Assert.DoesNotContain("attemptHistory", json);
            Assert.DoesNotContain("sec_key_xyz123", json);
        }

        // =========================================================================
        // 2. Customer khác -> 403 Forbidden
        // =========================================================================
        [Fact]
        public async Task GetOrderSummary_And_GetOrderRefund_OtherCustomer_Returns403Forbidden()
        {
            await using var context = CreateInMemoryDbContext();
            var ownerId = Guid.NewGuid();
            var otherUserId = Guid.NewGuid();
            var (order, _) = await SeedOrderWithFailedRefundAsync(context, ownerId);

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreateUserPrincipal(otherUserId, "Customer")
                    }
                }
            };

            var summaryResult = await controller.GetOrderSummary(order.Id);
            var summaryForbidden = Assert.IsType<ObjectResult>(summaryResult);
            Assert.Equal(StatusCodes.Status403Forbidden, summaryForbidden.StatusCode);

            var refundResult = await controller.GetOrderRefund(order.Id);
            var refundForbidden = Assert.IsType<ObjectResult>(refundResult);
            Assert.Equal(StatusCodes.Status403Forbidden, refundForbidden.StatusCode);
        }

        // =========================================================================
        // 3. Unauthenticated -> 401 Unauthorized
        // =========================================================================
        [Fact]
        public async Task GetOrderSummary_And_GetOrderRefund_Unauthenticated_Returns401Unauthorized()
        {
            await using var context = CreateInMemoryDbContext();
            var ownerId = Guid.NewGuid();
            var (order, _) = await SeedOrderWithFailedRefundAsync(context, ownerId);

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity()) // không có claims
                    }
                }
            };

            var summaryResult = await controller.GetOrderSummary(order.Id);
            var summaryUnauthorized = Assert.IsType<UnauthorizedObjectResult>(summaryResult);
            Assert.Equal(StatusCodes.Status401Unauthorized, summaryUnauthorized.StatusCode);

            var refundResult = await controller.GetOrderRefund(order.Id);
            var refundUnauthorized = Assert.IsType<UnauthorizedObjectResult>(refundResult);
            Assert.Equal(StatusCodes.Status401Unauthorized, refundUnauthorized.StatusCode);
        }

        // =========================================================================
        // 4. Auditor / Admin có technical fields cần thiết
        // =========================================================================
        [Theory]
        [InlineData("Auditor")]
        [InlineData("Admin")]
        public async Task GetOrderSummary_AuditorOrAdmin_RawJsonContainsAllTechnicalFields(string privilegedRole)
        {
            await using var context = CreateInMemoryDbContext();
            var ownerId = Guid.NewGuid();
            var privilegedUserId = Guid.NewGuid();
            var (order, _) = await SeedOrderWithFailedRefundAsync(context, ownerId);

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreateUserPrincipal(privilegedUserId, privilegedRole)
                    }
                }
            };

            var result = await controller.GetOrderSummary(order.Id);
            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value, JsonOptions);

            // Auditor / Admin PHẢI nhận được các trường kỹ thuật để xử lý đối soát
            Assert.Contains("\"refundErrorCode\":\"GATEWAY_TIMEOUT_504\"", json);
            Assert.Contains("\"refundAttemptCount\":2", json);
            Assert.Contains("\"refundRetryCount\":1", json);
            Assert.Contains("\"refundNextRetryAt\":", json);
            Assert.Contains("\"refundLastAttemptedAt\":", json);
            Assert.Contains("\"refundErrorMessage\":", json);
        }

        [Theory]
        [InlineData("Auditor")]
        [InlineData("Admin")]
        public async Task GetOrderRefund_AuditorOrAdmin_ReturnsFullRefundDetailDto(string privilegedRole)
        {
            await using var context = CreateInMemoryDbContext();
            var ownerId = Guid.NewGuid();
            var privilegedUserId = Guid.NewGuid();
            var (order, refundReq) = await SeedOrderWithFailedRefundAsync(context, ownerId);

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreateUserPrincipal(privilegedUserId, privilegedRole)
                    }
                }
            };

            var result = await controller.GetOrderRefund(order.Id);
            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<RefundDetailDto>>(ok.Value);

            Assert.True(response.Success);
            Assert.NotNull(response.Data);
            Assert.Equal(refundReq.Id, response.Data.Id);
            Assert.Equal("GATEWAY_TIMEOUT_504", response.Data.ErrorCode);
            Assert.Equal(2, response.Data.AttemptCount);
            Assert.Equal(1, response.Data.RetryCount);
            Assert.True(response.Data.IsRetryable);

            var json = JsonSerializer.Serialize(ok.Value, JsonOptions);
            Assert.Contains("\"errorCode\":\"GATEWAY_TIMEOUT_504\"", json);
            Assert.Contains("\"attemptCount\":2", json);
            Assert.Contains("\"retryCount\":1", json);
        }

        // =========================================================================
        // 5. GetMyOrders Customer raw JSON không chứa technical fields
        // =========================================================================
        [Fact]
        public async Task GetMyOrders_Customer_RawJsonDoesNotExposeInternalGatewayOrRetryFields()
        {
            await using var context = CreateInMemoryDbContext();
            var ownerId = Guid.NewGuid();
            await SeedOrderWithFailedRefundAsync(context, ownerId);

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = CreateUserPrincipal(ownerId, "Customer")
                    }
                }
            };

            var result = await controller.GetMyOrders();
            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value, JsonOptions);

            // Kiểm tra trong danh sách đơn của tôi
            Assert.Contains("\"refundStatus\":\"RetryScheduled\"", json);
            Assert.DoesNotContain("refundErrorCode", json);
            Assert.DoesNotContain("refundErrorMessage", json);
            Assert.DoesNotContain("refundAttemptCount", json);
            Assert.DoesNotContain("refundRetryCount", json);
            Assert.DoesNotContain("refundNextRetryAt", json);
            Assert.DoesNotContain("GATEWAY_TIMEOUT_504", json);
        }
    }
}
