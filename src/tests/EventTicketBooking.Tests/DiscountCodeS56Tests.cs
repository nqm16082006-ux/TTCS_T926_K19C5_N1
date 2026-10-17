using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.DiscountCodes;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử Story S-56 "Mã giảm giá theo sự kiện":
    /// Bao gồm đầy đủ 15 ca kiểm thử nghiệp vụ bắt buộc + Concurrency integration test.
    /// </summary>
    public class DiscountCodeS56Tests
    {
        private AppDbContext CreateInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            return new AppDbContext(options);
        }

        private async Task<(User user, Event ev, Showtime showtime, Order order)> SeedOrderScenarioAsync(
            AppDbContext context, int price = 500000, OrderStatus status = OrderStatus.Pending)
        {
            var organizer = new User
            {
                Id = Guid.NewGuid(),
                Username = $"organizer_{Guid.NewGuid():N}",
                Email = $"org_{Guid.NewGuid():N}@example.com",
                PasswordHash = "hash"
            };

            var customer = new User
            {
                Id = Guid.NewGuid(),
                Username = $"customer_{Guid.NewGuid():N}",
                Email = $"cust_{Guid.NewGuid():N}@example.com",
                PasswordHash = "hash"
            };

            var ev = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Live Concert S-56",
                Location = "Hanoi Opera House",
                TotalSeats = 100,
                OwnerId = organizer.Id
            };

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(3),
                AvailableSeats = 100
            };

            var category = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Name = "VIP",
                Price = price
            };

            var seat = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                SeatCategoryId = category.Id,
                Row = "A",
                SeatNumber = 1,
                Status = "HELD"
            };

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = customer.Id,
                ShowtimeId = showtime.Id,
                Status = status,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                OriginalAmount = price,
                DiscountAmount = 0,
                TotalAmount = price,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = seat.Id,
                Price = price
            };

            context.Users.AddRange(organizer, customer);
            context.Events.Add(ev);
            context.Showtimes.Add(showtime);
            context.SeatCategories.Add(category);
            context.Seats.Add(seat);
            context.Orders.Add(order);
            context.OrderItems.Add(orderItem);

            await context.SaveChangesAsync();

            return (customer, ev, showtime, order);
        }

        #region 15 Required Business Tests

        // 1. Percent discount đúng
        [Fact]
        public async Task Test01_PercentDiscount_CalculatesCorrectly()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "SALE10",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 100,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            var (createSuccess, _, createdCode, _) = await service.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);
            Assert.True(createSuccess);

            var (applySuccess, _, updatedOrder, _) = await service.ApplyDiscountCodeAsync(order.Id, "sale10", customer.Id);
            Assert.True(applySuccess);
            Assert.NotNull(updatedOrder);
            Assert.Equal(500000, updatedOrder.OriginalAmount);
            Assert.Equal(50000, updatedOrder.DiscountAmount);
            Assert.Equal(450000, updatedOrder.TotalAmount);
            Assert.Equal("SALE10", updatedOrder.DiscountCode);
        }

        // 2. Fixed discount đúng
        [Fact]
        public async Task Test02_FixedDiscount_CalculatesCorrectly()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "FIXED50K",
                DiscountType = DiscountType.FixedAmount,
                DiscountValue = 50000,
                UsageLimit = 50,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            var (createSuccess, _, _, _) = await service.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);
            Assert.True(createSuccess);

            var (applySuccess, _, updatedOrder, _) = await service.ApplyDiscountCodeAsync(order.Id, "FIXED50K", customer.Id);
            Assert.True(applySuccess);
            Assert.Equal(500000, updatedOrder!.OriginalAmount);
            Assert.Equal(50000, updatedOrder.DiscountAmount);
            Assert.Equal(450000, updatedOrder.TotalAmount);
        }

        // 3. Fixed > order total => total = 0
        [Fact]
        public async Task Test03_FixedDiscount_ExceedsTotal_TotalEqualsZero()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "BIGDISCOUNT",
                DiscountType = DiscountType.FixedAmount,
                DiscountValue = 600000,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await service.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);

            var (applySuccess, _, updatedOrder, _) = await service.ApplyDiscountCodeAsync(order.Id, "BIGDISCOUNT", customer.Id);
            Assert.True(applySuccess);
            Assert.Equal(500000, updatedOrder!.OriginalAmount);
            Assert.Equal(500000, updatedOrder.DiscountAmount); // capped at originalAmount
            Assert.Equal(0, updatedOrder.TotalAmount); // Không bao giờ âm
        }

        // 4. Expired => reject
        [Fact]
        public async Task Test04_ExpiredCode_Rejected()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var expiredCode = new DiscountCode
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Code = "EXPIREDCODE",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                UsedCount = 0,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                IsActive = true,
                CreatedByUserId = ev.OwnerId
            };
            context.DiscountCodes.Add(expiredCode);
            await context.SaveChangesAsync();

            var (applySuccess, message, _, statusCode) = await service.ApplyDiscountCodeAsync(order.Id, "EXPIREDCODE", customer.Id);
            Assert.False(applySuccess);
            Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
            Assert.Contains("hết hạn", message);
        }

        // 5. UsageLimit exhausted => reject
        [Fact]
        public async Task Test05_UsageLimitExhausted_Rejected()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var exhaustedCode = new DiscountCode
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Code = "LIMITED",
                DiscountType = DiscountType.FixedAmount,
                DiscountValue = 10000,
                UsageLimit = 1,
                UsedCount = 1, // Already exhausted
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                IsActive = true,
                CreatedByUserId = ev.OwnerId
            };
            context.DiscountCodes.Add(exhaustedCode);
            await context.SaveChangesAsync();

            var (applySuccess, message, _, statusCode) = await service.ApplyDiscountCodeAsync(order.Id, "LIMITED", customer.Id);
            Assert.False(applySuccess);
            Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
            Assert.Contains("hết lượt", message);
        }

        // 6. Event A code không dùng Event B
        [Fact]
        public async Task Test06_EventACode_CannotBeUsedOnEventB()
        {
            using var context = CreateInMemoryContext();
            var (customer, evA, _, orderA) = await SeedOrderScenarioAsync(context, price: 500000);

            // Create Event B
            var evB = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Event B",
                Location = "Da Nang",
                TotalSeats = 50,
                OwnerId = evA.OwnerId
            };
            context.Events.Add(evB);
            await context.SaveChangesAsync();

            var service = new DiscountCodeService(context);
            // Create code for Event B
            var createReq = new CreateDiscountCodeRequest
            {
                Code = "EVENTB10",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await service.CreateDiscountCodeAsync(evB.Id, createReq, evB.OwnerId);

            // Apply Event B's code to order of Event A
            var (applySuccess, message, _, statusCode) = await service.ApplyDiscountCodeAsync(orderA.Id, "EVENTB10", customer.Id);
            Assert.False(applySuccess);
            Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
            Assert.Contains("không thuộc sự kiện này", message);
        }

        // 7. Customer không apply order người khác
        [Fact]
        public async Task Test07_CustomerCannotApplyToAnotherUsersOrder()
        {
            using var context = CreateInMemoryContext();
            var (_, ev, _, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var otherUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "other_user",
                Email = "other@example.com",
                PasswordHash = "hash"
            };
            context.Users.Add(otherUser);
            await context.SaveChangesAsync();

            var service = new DiscountCodeService(context);
            var createReq = new CreateDiscountCodeRequest
            {
                Code = "SALE10",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await service.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);

            var (applySuccess, _, _, statusCode) = await service.ApplyDiscountCodeAsync(order.Id, "SALE10", otherUser.Id);
            Assert.False(applySuccess);
            Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
        }

        // 8. Organizer khác không quản lý code của Event
        [Fact]
        public async Task Test08_OtherOrganizerCannotManageEventCodes()
        {
            using var context = CreateInMemoryContext();
            var (_, ev, _, _) = await SeedOrderScenarioAsync(context);
            var anotherOrganizer = new User
            {
                Id = Guid.NewGuid(),
                Username = "another_org",
                Email = "another_org@example.com",
                PasswordHash = "hash"
            };
            context.Users.Add(anotherOrganizer);
            await context.SaveChangesAsync();

            var service = new DiscountCodeService(context);
            var createReq = new CreateDiscountCodeRequest
            {
                Code = "ATTEMPT",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };

            // Non-owner organizer without admin -> 403
            var (createSuccess, _, _, createStatus) = await service.CreateDiscountCodeAsync(ev.Id, createReq, anotherOrganizer.Id, isAdmin: false);
            Assert.False(createSuccess);
            Assert.Equal(StatusCodes.Status403Forbidden, createStatus);

            var (getSuccess, _, _, getStatus) = await service.GetDiscountCodesByEventAsync(ev.Id, anotherOrganizer.Id, isAdmin: false);
            Assert.False(getSuccess);
            Assert.Equal(StatusCodes.Status403Forbidden, getStatus);

            // Admin can manage
            var (adminSuccess, _, _, adminStatus) = await service.CreateDiscountCodeAsync(ev.Id, createReq, anotherOrganizer.Id, isAdmin: true);
            Assert.True(adminSuccess);
            Assert.Equal(StatusCodes.Status201Created, adminStatus);
        }

        // 9. Apply cùng code lại => UsedCount không tăng lần 2 (Idempotency)
        [Fact]
        public async Task Test09_ReapplySameCode_Idempotent_DoesNotIncrementTwice()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, _, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "IDEMPOTENT10",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await service.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);

            // Apply 1st time
            var (firstSuccess, _, firstDto, _) = await service.ApplyDiscountCodeAsync(order.Id, "IDEMPOTENT10", customer.Id);
            Assert.True(firstSuccess);
            Assert.Equal(450000, firstDto!.TotalAmount);

            var codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "IDEMPOTENT10");
            Assert.Equal(1, codeEntity.UsedCount);

            // Apply 2nd time with exact same code
            var (secondSuccess, _, secondDto, _) = await service.ApplyDiscountCodeAsync(order.Id, "IDEMPOTENT10", customer.Id);
            Assert.True(secondSuccess);
            Assert.Equal(450000, secondDto!.TotalAmount);

            // UsedCount must NOT increment again!
            codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "IDEMPOTENT10");
            Assert.Equal(1, codeEntity.UsedCount);
        }

        // 10. Order có code khác => reject stacking
        [Fact]
        public async Task Test10_ApplyDifferentCode_RejectStacking()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, _, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var req1 = new CreateDiscountCodeRequest
            {
                Code = "FIRSTCODE",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            var req2 = new CreateDiscountCodeRequest
            {
                Code = "SECONDCODE",
                DiscountType = DiscountType.Percent,
                DiscountValue = 20,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await service.CreateDiscountCodeAsync(ev.Id, req1, ev.OwnerId);
            await service.CreateDiscountCodeAsync(ev.Id, req2, ev.OwnerId);

            // Apply FIRSTCODE
            var (success1, _, _, _) = await service.ApplyDiscountCodeAsync(order.Id, "FIRSTCODE", customer.Id);
            Assert.True(success1);

            // Attempt to apply SECONDCODE on same order -> Rejected
            var (success2, message2, _, status2) = await service.ApplyDiscountCodeAsync(order.Id, "SECONDCODE", customer.Id);
            Assert.False(success2);
            Assert.Equal(StatusCodes.Status400BadRequest, status2);
            Assert.Contains("đã có mã giảm giá khác", message2);
        }

        // 11. Expired Order release usage đúng 1
        [Fact]
        public async Task Test11_ExpiredOrder_ReleasesUsageExactlyOnce()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var discountService = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "RELEASEME",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await discountService.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);
            await discountService.ApplyDiscountCodeAsync(order.Id, "RELEASEME", customer.Id);

            var codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "RELEASEME");
            Assert.Equal(1, codeEntity.UsedCount);

            // Expire order
            order.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();

            var cleanupService = new ExpiredOrderCleanupService(context, NullLogger<ExpiredOrderCleanupService>.Instance, null, null, discountService);
            int cleaned = await cleanupService.CleanupExpiredOrdersAsync();

            Assert.Equal(1, cleaned);
            codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "RELEASEME");
            Assert.Equal(0, codeEntity.UsedCount); // Released 1 usage

            var reloadedOrder = await context.Orders.FindAsync(order.Id);
            Assert.Null(reloadedOrder!.DiscountCodeId);
            Assert.Equal(0, reloadedOrder.DiscountAmount);
            Assert.Equal(500000, reloadedOrder.TotalAmount); // restored to original
        }

        // 12. Worker chạy lại không double release
        [Fact]
        public async Task Test12_ExpiredWorker_SecondRun_DoesNotDoubleRelease()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var discountService = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "NODECLINE",
                DiscountType = DiscountType.FixedAmount,
                DiscountValue = 50000,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await discountService.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);
            await discountService.ApplyDiscountCodeAsync(order.Id, "NODECLINE", customer.Id);

            order.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await context.SaveChangesAsync();

            var cleanupService = new ExpiredOrderCleanupService(context, NullLogger<ExpiredOrderCleanupService>.Instance, null, null, discountService);

            // 1st run
            await cleanupService.CleanupExpiredOrdersAsync();
            var codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "NODECLINE");
            Assert.Equal(0, codeEntity.UsedCount);

            // 2nd run
            await cleanupService.CleanupExpiredOrdersAsync();
            codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "NODECLINE");
            Assert.Equal(0, codeEntity.UsedCount); // Still 0, not below 0!
        }

        // 13. Paid Order không release usage
        [Fact]
        public async Task Test13_PaidOrder_NeverReleasesUsage()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, _, order) = await SeedOrderScenarioAsync(context, price: 500000, status: OrderStatus.Pending);
            var service = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "PAIDTEST",
                DiscountType = DiscountType.Percent,
                DiscountValue = 10,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await service.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);
            await service.ApplyDiscountCodeAsync(order.Id, "PAIDTEST", customer.Id);

            // Order is now PAID
            order.Status = OrderStatus.Paid;
            await context.SaveChangesAsync();

            // Calling release on Paid order must return false and not decrement UsedCount
            bool released = await service.ReleaseDiscountReservationAsync(order);
            Assert.False(released);

            var codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "PAIDTEST");
            Assert.Equal(1, codeEntity.UsedCount); // Still reserved
        }

        // 14. Snapshot vẫn giữ giá cũ nếu DiscountCode sau đó thay đổi
        [Fact]
        public async Task Test14_SnapshotPreservesOrderPrice_WhenDiscountCodeChangesLater()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, _, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var service = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "ORIGINAL20",
                DiscountType = DiscountType.Percent,
                DiscountValue = 20,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await service.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);
            await service.ApplyDiscountCodeAsync(order.Id, "ORIGINAL20", customer.Id);

            // Verify initial discounted price
            var reloadedOrder = await context.Orders.FindAsync(order.Id);
            Assert.Equal(100000, reloadedOrder!.DiscountAmount);
            Assert.Equal(400000, reloadedOrder.TotalAmount);

            // Organizer later edits the discount code entity to 50% or deactivates it
            var codeEntity = await context.DiscountCodes.FirstAsync(d => d.Code == "ORIGINAL20");
            codeEntity.DiscountValue = 50;
            codeEntity.IsActive = false;
            await context.SaveChangesAsync();

            // Recalculating order or re-reading order MUST retain the original snapshot price!
            reloadedOrder.CalculateTotal();
            Assert.Equal(100000, reloadedOrder.DiscountAmount);
            Assert.Equal(400000, reloadedOrder.TotalAmount);
        }

        // 15. Payment amount dùng discounted TotalAmount
        [Fact]
        public async Task Test15_PaymentService_UsesDiscountedTotalAmount()
        {
            using var context = CreateInMemoryContext();
            var (customer, ev, showtime, order) = await SeedOrderScenarioAsync(context, price: 500000);
            var discountService = new DiscountCodeService(context);

            var createReq = new CreateDiscountCodeRequest
            {
                Code = "PAYTEST",
                DiscountType = DiscountType.FixedAmount,
                DiscountValue = 100000,
                UsageLimit = 10,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };
            await discountService.CreateDiscountCodeAsync(ev.Id, createReq, ev.OwnerId);
            await discountService.ApplyDiscountCodeAsync(order.Id, "PAYTEST", customer.Id);

            // Order.TotalAmount is now 400000
            Assert.Equal(400000, order.TotalAmount);

            PaymentCreationRequest? capturedRequest = null;
            var mockGateway = new Mock<IPaymentGateway>();
            mockGateway.Setup(g => g.ProviderName).Returns("Mock");
            mockGateway.Setup(g => g.CreatePaymentAsync(It.IsAny<PaymentCreationRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PaymentCreationRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(PaymentCreationResult.CreateSuccess("https://payment.example.com", "tx_123", null, 123456789));

            var paymentService = new PaymentService(
                context,
                mockGateway.Object,
                NullLogger<PaymentService>.Instance,
                new Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);

            var payResult = await paymentService.CreatePaymentForOrderAsync(order.Id, customer.Id);

            Assert.True(payResult.Success);
            Assert.NotNull(capturedRequest);
            // Must equal discounted total amount (400,000 VND), NOT original 500,000 VND!
            Assert.Equal(400000, capturedRequest.Amount);
        }

        #endregion

        #region Concurrency Integration Test

        // Concurrency test: 20 concurrent apply requests against UsageLimit = 5
        [Fact]
        public async Task Test16_Concurrency_20Requests_UsageLimit5_Exactly5Succeed()
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            Guid discountCodeId;
            var orderData = new List<(Guid OrderId, Guid UserId)>();

            using (var seedContext = new AppDbContext(options))
            {
                var organizer = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "org_concurrency",
                    Email = "org_conc@example.com",
                    PasswordHash = "hash"
                };
                seedContext.Users.Add(organizer);

                var ev = new Event
                {
                    Id = Guid.NewGuid(),
                    Title = "Concurrent Event",
                    Location = "Hanoi",
                    TotalSeats = 100,
                    OwnerId = organizer.Id
                };
                seedContext.Events.Add(ev);

                var showtime = new Showtime
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    StartTime = DateTime.UtcNow.AddDays(1),
                    EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
                    AvailableSeats = 100
                };
                seedContext.Showtimes.Add(showtime);

                // Create 20 distinct users and 20 distinct pending orders
                for (int i = 0; i < 20; i++)
                {
                    var cust = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = $"cust_{i}",
                        Email = $"cust_{i}@example.com",
                        PasswordHash = "hash"
                    };
                    seedContext.Users.Add(cust);

                    var order = new Order
                    {
                        Id = Guid.NewGuid(),
                        UserId = cust.Id,
                        ShowtimeId = showtime.Id,
                        Status = OrderStatus.Pending,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                        OriginalAmount = 500000,
                        DiscountAmount = 0,
                        TotalAmount = 500000
                    };
                    orderData.Add((order.Id, cust.Id));
                    seedContext.Orders.Add(order);
                }

                // Create Discount Code with limit = 5
                var discountCode = new DiscountCode
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    Code = "RACE5",
                    DiscountType = DiscountType.Percent,
                    DiscountValue = 10,
                    UsageLimit = 5,
                    UsedCount = 0,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                    IsActive = true,
                    CreatedByUserId = organizer.Id
                };
                discountCodeId = discountCode.Id;
                seedContext.DiscountCodes.Add(discountCode);
                await seedContext.SaveChangesAsync();
            }

            // Fire 20 concurrent requests (each simulated request has its own scoped DbContext, exactly as in ASP.NET Core)
            var tasks = orderData.Select(o => Task.Run(async () =>
            {
                using var taskContext = new AppDbContext(options);
                var taskService = new DiscountCodeService(taskContext);
                var (success, _, _, _) = await taskService.ApplyDiscountCodeAsync(o.OrderId, "RACE5", o.UserId);
                return success;
            })).ToList();

            var results = await Task.WhenAll(tasks);

            int successCount = results.Count(r => r);
            int failCount = results.Count(r => !r);

            // Verify:
            // - Exactly 5 succeed
            // - Exactly 15 reject
            // - UsedCount == 5 (không > 5, không âm)
            Assert.Equal(5, successCount);
            Assert.Equal(15, failCount);

            using (var verifyContext = new AppDbContext(options))
            {
                var finalCode = await verifyContext.DiscountCodes.FindAsync(discountCodeId);
                Assert.Equal(5, finalCode!.UsedCount);

                int ordersWithDiscount = await verifyContext.Orders.CountAsync(o => o.DiscountCodeId == discountCodeId);
                Assert.Equal(5, ordersWithDiscount);
            }
        }

        // Real PostgreSQL atomic relational concurrency test against local Docker container
        [Fact]
        public async Task Test17_PostgreSql_AtomicConcurrency_UsageLimit5_Exactly5Succeed()
        {
            var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1";
            var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
            var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
            var pass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres_password_123";
            var connStr = $"Host={host};Port={port};Database=event_ticket_s56_test;Username={user};Password={pass};Timeout=3";

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connStr)
                .Options;

            try
            {
                using var testConn = new AppDbContext(options);
                if (!await testConn.Database.CanConnectAsync())
                {
                    return;
                }
                await testConn.Database.EnsureCreatedAsync();
            }
            catch
            {
                return;
            }

            Guid discountCodeId;
            var orderData = new List<(Guid OrderId, Guid UserId)>();

            using (var seedContext = new AppDbContext(options))
            {
                var organizer = new User
                {
                    Id = Guid.NewGuid(),
                    Username = $"org_pg_{Guid.NewGuid():N}",
                    Email = $"org_pg_{Guid.NewGuid():N}@example.com",
                    PasswordHash = "hash"
                };
                seedContext.Users.Add(organizer);

                var ev = new Event
                {
                    Id = Guid.NewGuid(),
                    Title = "PG Concurrent Event",
                    Location = "Hanoi",
                    TotalSeats = 100,
                    OwnerId = organizer.Id
                };
                seedContext.Events.Add(ev);

                var showtime = new Showtime
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    StartTime = DateTime.UtcNow.AddDays(1),
                    EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
                    AvailableSeats = 100
                };
                seedContext.Showtimes.Add(showtime);

                for (int i = 0; i < 20; i++)
                {
                    var cust = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = $"cust_pg_{i}_{Guid.NewGuid():N}",
                        Email = $"cust_pg_{i}_{Guid.NewGuid():N}@example.com",
                        PasswordHash = "hash"
                    };
                    seedContext.Users.Add(cust);

                    var order = new Order
                    {
                        Id = Guid.NewGuid(),
                        UserId = cust.Id,
                        ShowtimeId = showtime.Id,
                        Status = OrderStatus.Pending,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                        OriginalAmount = 500000,
                        DiscountAmount = 0,
                        TotalAmount = 500000
                    };
                    orderData.Add((order.Id, cust.Id));
                    seedContext.Orders.Add(order);
                }

                var codeStr = $"PGRACE5_{Guid.NewGuid():N}"[..15].ToUpperInvariant();
                var discountCode = new DiscountCode
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    Code = codeStr,
                    DiscountType = DiscountType.Percent,
                    DiscountValue = 10,
                    UsageLimit = 5,
                    UsedCount = 0,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                    IsActive = true,
                    CreatedByUserId = organizer.Id
                };
                discountCodeId = discountCode.Id;
                seedContext.DiscountCodes.Add(discountCode);
                await seedContext.SaveChangesAsync();

                // Fire 20 concurrent requests executing real PostgreSQL raw atomic SQL transaction
                var tasks = orderData.Select(o => Task.Run(async () =>
                {
                    using var taskContext = new AppDbContext(options);
                    var taskService = new DiscountCodeService(taskContext);
                    var (success, _, _, _) = await taskService.ApplyDiscountCodeAsync(o.OrderId, codeStr, o.UserId);
                    return success;
                })).ToList();

                var results = await Task.WhenAll(tasks);

                int successCount = results.Count(r => r);
                int failCount = results.Count(r => !r);

                // Assert exactly 5 successes, 15 failures under real PostgreSQL atomic UPDATE
                Assert.Equal(5, successCount);
                Assert.Equal(15, failCount);

                using var verifyContext = new AppDbContext(options);
                var finalCode = await verifyContext.DiscountCodes.FindAsync(discountCodeId);
                Assert.Equal(5, finalCode!.UsedCount);

                int ordersWithDiscount = await verifyContext.Orders.CountAsync(o => o.DiscountCodeId == discountCodeId);
                Assert.Equal(5, ordersWithDiscount);
            }
        }

        #endregion
    }
}
