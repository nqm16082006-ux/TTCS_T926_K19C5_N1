using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
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
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class OrderAuditLogS49Tests
    {
        private AppDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: "AuditLogTestDb_" + Guid.NewGuid())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new AppDbContext(options);
        }

        private ClaimsPrincipal CreateClaimsPrincipal(Guid userId, string username, string role = "Customer")
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("id", userId.ToString()),
                new Claim(ClaimTypes.Name, username),
                new Claim("name", username),
                new Claim(ClaimTypes.Role, role),
                new Claim("role", role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            return new ClaimsPrincipal(identity);
        }

        [Fact]
        public async Task CreateOrder_RecordsAuditLogs_WithUserActor_And_SeatHoldsAttached()
        {
            using var context = CreateInMemoryDbContext();
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var userId = Guid.NewGuid();
            var user = new User { Id = userId, Username = "alice", Email = "alice@example.com", FullName = "Alice Nguyen" };
            context.Users.Add(user);

            var eventItem = new Event { Id = Guid.NewGuid(), Title = "Concert 2026", Location = "Hà Nội", TotalSeats = 100, OwnerId = userId };
            context.Events.Add(eventItem);

            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = eventItem.Id, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2), AvailableSeats = 100 };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);
            context.Showtimes.Add(showtime);

            var category = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Name = "VIP", Price = 150000 };
            context.SeatCategories.Add(category);

            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "A", SeatNumber = 1, Status = "HELD" };
            context.Seats.Add(seat);

            var hold = new SeatHolds { Id = Guid.NewGuid(), SeatId = seat.Id, UserId = userId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
            context.SeatHold.Add(hold);

            await context.SaveChangesAsync();

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance, null, null, auditService);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateClaimsPrincipal(userId, "Alice Nguyen") }
            };

            // Act
            var result = await controller.CreateOrderFromHolds(showtime.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<OrderDto>>(okResult.Value);
            var orderId = apiResponse.Data!.Id;

            var logs = await context.OrderAuditLogs.Where(l => l.OrderId == orderId).ToListAsync();
            Assert.NotEmpty(logs);

            var orderCreatedLog = logs.FirstOrDefault(l => l.Action == "ORDER_CREATED");
            Assert.NotNull(orderCreatedLog);
            Assert.Equal("ORDER", orderCreatedLog.EntityType);
            Assert.Null(orderCreatedLog.OldStatus);
            Assert.Equal("Pending", orderCreatedLog.NewStatus);
            Assert.Equal("USER", orderCreatedLog.ActorType);
            Assert.Equal("Alice Nguyen", orderCreatedLog.Actor);
            Assert.Equal(userId, orderCreatedLog.ActorUserId);

            var holdLog = logs.FirstOrDefault(l => l.Action == "SEAT_HOLD_ATTACHED");
            Assert.NotNull(holdLog);
            Assert.Equal("SEAT_HOLD", holdLog.EntityType);
            Assert.Equal("ACTIVE", holdLog.NewStatus);
            Assert.Equal("USER", holdLog.ActorType);
        }

        [Fact]
        public async Task PaymentSuccess_RecordsOrderPaid_HoldConverted_And_TicketIssuedLogs()
        {
            using var context = CreateInMemoryDbContext();
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var userId = Guid.NewGuid();
            var user = new User { Id = userId, Username = "bob", Email = "bob@example.com" };
            context.Users.Add(user);

            var eventItem = new Event { Id = Guid.NewGuid(), Title = "Sự kiện Âm Nhạc", Location = "TP.HCM", TotalSeats = 50, OwnerId = userId };
            context.Events.Add(eventItem);

            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = eventItem.Id, StartTime = DateTime.UtcNow.AddDays(2), EndTime = DateTime.UtcNow.AddDays(2).AddHours(2), AvailableSeats = 50 };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);
            context.Showtimes.Add(showtime);

            var category = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Name = "Standard", Price = 100000 };
            context.SeatCategories.Add(category);

            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "B", SeatNumber = 5, Status = "HELD" };
            context.Seats.Add(seat);

            var hold = new SeatHolds { Id = Guid.NewGuid(), SeatId = seat.Id, UserId = userId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(15) };
            context.SeatHold.Add(hold);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Pending,
                TotalAmount = 100000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                OrderItems = new List<OrderItem>
                {
                    new OrderItem { Id = Guid.NewGuid(), SeatId = seat.Id, Price = 100000 }
                }
            };
            context.Orders.Add(order);

            var paymentTx = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = 123456789,
                Amount = 100000,
                Status = "PENDING"
            };
            context.PaymentTransactions.Add(paymentTx);
            await context.SaveChangesAsync();

            var fakeGateway = new FakePaymentGateway();
            var emailQueueMock = new Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>();
            var paymentService = new PaymentService(context, fakeGateway, NullLogger<PaymentService>.Instance, emailQueueMock.Object, null, null, null, auditService);

            var paymentResult = PaymentResultDto.CreateSuccess(
                orderCode: 123456789,
                amount: 100000,
                transactionId: "TX-PAYOS-999",
                paidAt: DateTimeOffset.UtcNow,
                orderId: order.Id.ToString()
            );

            // Act
            var result = await paymentService.HandlePaymentResultAsync(paymentResult);

            // Assert
            Assert.True(result.Success);

            var logs = await context.OrderAuditLogs.Where(l => l.OrderId == order.Id).ToListAsync();
            Assert.NotEmpty(logs);

            var orderPaidLog = logs.FirstOrDefault(l => l.Action == "ORDER_PAID");
            Assert.NotNull(orderPaidLog);
            Assert.Equal("Pending", orderPaidLog.OldStatus);
            Assert.Equal("Paid", orderPaidLog.NewStatus);
            Assert.Equal("SYSTEM", orderPaidLog.ActorType);
            Assert.Equal("system:PaymentWebhook", orderPaidLog.Actor);

            var holdLog = logs.FirstOrDefault(l => l.Action == "SEAT_HOLD_CONVERTED");
            Assert.NotNull(holdLog);
            Assert.Equal("SEAT_HOLD", holdLog.EntityType);
            Assert.Equal("ACTIVE", holdLog.OldStatus);
            Assert.Equal("CONVERTED", holdLog.NewStatus);

            var ticketLog = logs.FirstOrDefault(l => l.Action == "TICKET_ISSUED");
            Assert.NotNull(ticketLog);
            Assert.Equal("TICKET", ticketLog.EntityType);
            Assert.Equal("UNISSUED", ticketLog.OldStatus);
            Assert.Equal("ISSUED", ticketLog.NewStatus);
        }

        [Fact]
        public async Task ExpiredOrderCleanup_RecordsAuditLogs_WithBackgroundJobActor()
        {
            using var context = CreateInMemoryDbContext();
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var userId = Guid.NewGuid();

            var seat = new Seat { Id = Guid.NewGuid(), Row = "C", SeatNumber = 10, Status = "HELD" };
            context.Seats.Add(seat);

            var hold = new SeatHolds { Id = Guid.NewGuid(), SeatId = seat.Id, UserId = userId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(-5) };
            context.SeatHold.Add(hold);

            var expiredOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Status = OrderStatus.Pending,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                OrderItems = new List<OrderItem>
                {
                    new OrderItem { Id = Guid.NewGuid(), SeatId = seat.Id, Price = 50000 }
                }
            };
            context.Orders.Add(expiredOrder);
            await context.SaveChangesAsync();

            var cleanupService = new ExpiredOrderCleanupService(context, NullLogger<ExpiredOrderCleanupService>.Instance, null, auditService);

            // Act
            int count = await cleanupService.CleanupExpiredOrdersAsync(DateTimeOffset.UtcNow);

            // Assert
            Assert.Equal(1, count);

            var logs = await context.OrderAuditLogs.Where(l => l.OrderId == expiredOrder.Id).ToListAsync();
            Assert.NotEmpty(logs);

            var orderExpiredLog = logs.FirstOrDefault(l => l.Action == "ORDER_EXPIRED");
            Assert.NotNull(orderExpiredLog);
            Assert.Equal("ORDER", orderExpiredLog.EntityType);
            Assert.Equal("Pending", orderExpiredLog.OldStatus);
            Assert.Equal("Expired", orderExpiredLog.NewStatus);
            Assert.Equal("BACKGROUND_JOB", orderExpiredLog.ActorType);
            Assert.Equal("job:ExpiredOrderCleanupWorker", orderExpiredLog.Actor);

            var holdExpiredLog = logs.FirstOrDefault(l => l.Action == "SEAT_HOLD_EXPIRED");
            Assert.NotNull(holdExpiredLog);
            Assert.Equal("SEAT_HOLD", holdExpiredLog.EntityType);
            Assert.Equal("ACTIVE", holdExpiredLog.OldStatus);
            Assert.Equal("EXPIRED", holdExpiredLog.NewStatus);
            Assert.Equal("BACKGROUND_JOB", holdExpiredLog.ActorType);
        }

        [Fact]
        public async Task TicketCheckIn_RecordsAuditLogs_WithStaffActor()
        {
            using var context = CreateInMemoryDbContext();
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var staffUserId = Guid.NewGuid();
            var staffUser = new User { Id = staffUserId, Username = "staff01", FullName = "Nguyễn Soát Vé", IsActive = true };
            context.Users.Add(staffUser);

            var showtimeId = Guid.NewGuid();
            var showtime = new Showtime { Id = showtimeId, StartTime = DateTime.UtcNow.AddHours(1), EndTime = DateTime.UtcNow.AddHours(3), AvailableSeats = 10 };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);
            context.Showtimes.Add(showtime);

            var category = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtimeId, Name = "VIP" };
            context.SeatCategories.Add(category);

            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtimeId, SeatCategoryId = category.Id, Row = "D", SeatNumber = 2, Status = "SOLD" };
            context.Seats.Add(seat);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                Status = OrderStatus.Paid,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
            };
            context.Orders.Add(order);

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = seat.Id,
                Price = 120000,
                IsCheckedIn = false,
                Order = order,
                Seat = seat
            };
            context.OrderItems.Add(orderItem);

            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = orderItem.Id,
                TicketCode = "TCK-1234-ABCD",
                OrderItem = orderItem
            };
            context.Tickets.Add(ticket);
            orderItem.Ticket = ticket;
            await context.SaveChangesAsync();

            var checkInController = new TicketCheckInController(context, NullLogger<TicketCheckInController>.Instance, null, auditService);
            checkInController.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateClaimsPrincipal(staffUserId, "Nguyễn Soát Vé", "Staff") }
            };

            var scanRequest = new TicketScanRequestDto
            {
                TicketId = orderItem.Id,
                TicketCode = "TCK-1234-ABCD",
                SelectedShowtimeId = showtimeId,
                GateName = "Cổng A1"
            };

            // Act
            var scanResult = await checkInController.ScanTicket(scanRequest);

            // Assert
            if (scanResult is BadRequestObjectResult br)
            {
                Assert.Fail("ScanTicket failed with BadRequest: " + System.Text.Json.JsonSerializer.Serialize(br.Value));
            }
            Assert.IsType<OkObjectResult>(scanResult);

            var logs = await context.OrderAuditLogs.Where(l => l.OrderId == order.Id).ToListAsync();
            var checkInLog = logs.FirstOrDefault(l => l.Action == "TICKET_CHECKED_IN");
            Assert.NotNull(checkInLog);
            Assert.Equal("TICKET", checkInLog.EntityType);
            Assert.Equal("TCK-1234-ABCD", checkInLog.EntityId);
            Assert.Equal("NOT_CHECKED_IN", checkInLog.OldStatus);
            Assert.Equal("CHECKED_IN", checkInLog.NewStatus);
            Assert.Equal("STAFF", checkInLog.ActorType);
            Assert.Equal("Nguyễn Soát Vé", checkInLog.Actor);
            Assert.Equal(staffUserId, checkInLog.ActorUserId);
        }

        [Fact]
        public async Task GetOrderAuditLogs_ReturnsLogsForOrder_AndProtectsUnauthorizedAccess()
        {
            using var context = CreateInMemoryDbContext();
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var ownerId = Guid.NewGuid();
            var strangerId = Guid.NewGuid();

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = ownerId,
                Status = OrderStatus.Paid
            };
            context.Orders.Add(order);

            // Thêm 2 bản ghi nhật ký
            auditService.Record(order.Id, "ORDER", order.Id.ToString(), "ORDER_CREATED", null, "Pending", "USER", "Owner", ownerId, "Tạo đơn", DateTimeOffset.UtcNow.AddMinutes(-10));
            auditService.Record(order.Id, "ORDER", order.Id.ToString(), "ORDER_PAID", "Pending", "Paid", "SYSTEM", "system:PaymentWebhook", null, "Đã thanh toán", DateTimeOffset.UtcNow.AddMinutes(-5));
            await context.SaveChangesAsync();

            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance, null, null, auditService);

            // 1. Người lạ truy cập -> 403 Forbidden
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateClaimsPrincipal(strangerId, "Stranger", "Customer") }
            };
            var forbiddenResult = await controller.GetOrderAuditLogs(order.Id);
            var objResult = Assert.IsType<ObjectResult>(forbiddenResult);
            Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);

            // 2. Chủ đơn truy cập -> 200 OK với danh sách log
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateClaimsPrincipal(ownerId, "Owner", "Customer") }
            };
            var ownerResult = await controller.GetOrderAuditLogs(order.Id);
            var okResult = Assert.IsType<OkObjectResult>(ownerResult);
            var apiResp = Assert.IsType<ApiResponse<List<OrderAuditLogDto>>>(okResult.Value);
            Assert.Equal(2, apiResp.Data!.Count);
            Assert.Equal("ORDER_CREATED", apiResp.Data[0].Action);
            Assert.Equal("ORDER_PAID", apiResp.Data[1].Action);

            // 3. Auditor/Admin truy cập -> 200 OK
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateClaimsPrincipal(Guid.NewGuid(), "AdminUser", "Auditor") }
            };
            var auditorResult = await controller.GetOrderAuditLogs(order.Id);
            var auditorOk = Assert.IsType<OkObjectResult>(auditorResult);
            var auditorResp = Assert.IsType<ApiResponse<List<OrderAuditLogDto>>>(auditorOk.Value);
            Assert.Equal(2, auditorResp.Data!.Count);
        }
    }
}
