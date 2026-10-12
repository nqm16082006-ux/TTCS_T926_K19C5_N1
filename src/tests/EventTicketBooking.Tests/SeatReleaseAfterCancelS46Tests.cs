using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.BackgroundServices;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Order = EventTicketBooking.Api.Models.Order;

namespace EventTicketBooking.Tests;

/// <summary>
/// Bộ kiểm thử Story S-46: Ghế của vé đã huỷ quay lại kho bán.
/// Mô tả nghiệp vụ: Vé huỷ xong thì ghế trở lại trạng thái trống trên sơ đồ và giữ được như ghế thường.
/// </summary>
public sealed class SeatReleaseAfterCancelS46Tests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static DevelopmentMockPaymentGateway CreateMockGateway() =>
        new(Options.Create(new MockPaymentOptions { RefundOutcome = "Succeeded" }));

    private static OrdersController CreateOrdersController(
        AppDbContext context,
        Guid userId,
        IHubContext<SeatStatusHub>? hubContext = null)
    {
        return new OrdersController(
            context,
            NullLogger<OrdersController>.Instance,
            paymentGateway: CreateMockGateway(),
            refundPolicy: Options.Create(new RefundPolicyOptions()),
            hubContext: hubContext)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim("id", userId.ToString()),
                        new Claim(ClaimTypes.Name, "TestBuyer")
                    ], "Test"))
                }
            }
        };
    }

    private static async Task<(Guid UserId, Guid ShowtimeId, Guid OrderId, List<Guid> SeatIds)> SeedPaidOrderWithSeatsAsync(
        AppDbContext context,
        int seatCount = 2,
        DateTime? showtimeStartUtc = null)
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showtimeId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var startTime = showtimeStartUtc ?? DateTime.UtcNow.AddDays(3);

        var user = new User
        {
            Id = userId,
            Username = $"buyer_{Guid.NewGuid():N}",
            Email = $"buyer_{Guid.NewGuid():N}@example.com",
            PasswordHash = "hashed",
            FullName = "Khách Hàng Mua Vé"
        };

        var ev = new Event
        {
            Id = eventId,
            Title = "Đêm Nhạc S-46 Trực Tiếp",
            Location = "Nhà hát TP",
            TotalSeats = 100
        };

        var showtime = new Showtime
        {
            Id = showtimeId,
            EventId = eventId,
            StartTime = startTime,
            EndTime = startTime.AddHours(3),
            AvailableSeats = 100
        };
        showtime.ChangeStatus(ShowtimeStatus.OnSale);

        var category = new SeatCategory
        {
            Id = categoryId,
            ShowtimeId = showtimeId,
            Name = "VIP",
            Price = 250_000
        };

        var seats = new List<Seat>();
        var orderItems = new List<OrderItem>();
        var holds = new List<SeatHolds>();

        for (int i = 1; i <= seatCount; i++)
        {
            var seat = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                SeatCategoryId = categoryId,
                Row = "A",
                SeatNumber = i,
                Status = "SOLD" // Ghế đã thanh toán
            };
            seats.Add(seat);

            var item = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                SeatId = seat.Id,
                Price = 250_000,
                IsCheckedIn = false,
                Seat = seat
            };
            orderItems.Add(item);

            var hold = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seat.Id,
                UserId = userId,
                Status = "CONVERTED", // Đã chuyển thành vé
                HeldAt = DateTime.UtcNow.AddMinutes(-30),
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            };
            holds.Add(hold);
        }

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = showtimeId,
            Status = OrderStatus.Paid,
            TotalAmount = 250_000 * seatCount,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            OrderItems = orderItems
        };
        order.CalculateTotal();

        context.Users.Add(user);
        context.Events.Add(ev);
        context.Showtimes.Add(showtime);
        context.SeatCategories.Add(category);
        context.Seats.AddRange(seats);
        context.SeatHold.AddRange(holds);
        context.Orders.Add(order);

        foreach (var item in orderItems)
        {
            context.Tickets.Add(new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = item.Id,
                TicketCode = $"TK-S46-{Guid.NewGuid():N}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        context.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            OrderCode = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Amount = order.TotalAmount,
            Status = "PAID",
            TransactionId = $"TX-{Guid.NewGuid():N}"
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return (userId, showtimeId, orderId, seats.Select(s => s.Id).ToList());
    }

    [Fact]
    public async Task CancelOrderAndRefund_ReleasesAllSeats_StatusBecomesAvailable()
    {
        // Arrange
        await using var context = CreateContext();
        var (userId, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 2);
        var controller = CreateOrdersController(context, userId);

        // Verify pre-condition: ghế đang SOLD
        var seatsBefore = await context.Seats.Where(s => seatIds.Contains(s.Id)).ToListAsync();
        Assert.All(seatsBefore, s => Assert.Equal("SOLD", s.Status));

        // Act: Khách hàng huỷ vé & hoàn tiền
        var result = await controller.CancelOrderAndRefund(orderId);

        // Assert: Thành công
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.True(response.Success);

        // S-46: Ghế của vé đã huỷ quay lại kho bán (chuyển sang AVAILABLE)
        var seatsAfter = await context.Seats.Where(s => seatIds.Contains(s.Id)).ToListAsync();
        Assert.Equal(2, seatsAfter.Count);
        Assert.All(seatsAfter, s => Assert.Equal("AVAILABLE", s.Status));

        // S-46: Giữ chỗ chuyển sang RELEASED
        var holdsAfter = await context.SeatHold.Where(sh => seatIds.Contains(sh.SeatId)).ToListAsync();
        Assert.All(holdsAfter, h => Assert.Equal("RELEASED", h.Status));
    }

    [Fact]
    public async Task CancelOrderAndRefund_RecordsAuditLogs_ForSeatRelease()
    {
        // Arrange
        await using var context = CreateContext();
        var (userId, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 2);
        var controller = CreateOrdersController(context, userId);

        // Act
        await controller.CancelOrderAndRefund(orderId);

        // Assert: Audit trail ghi nhận SEAT_RELEASED cho từng ghế
        var seatLogs = await context.OrderAuditLogs
            .Where(l => l.OrderId == orderId && l.EntityType == "SEAT")
            .ToListAsync();

        Assert.Equal(2, seatLogs.Count);
        foreach (var log in seatLogs)
        {
            Assert.Equal("SEAT_RELEASED", log.Action);
            Assert.Equal("SOLD", log.OldStatus);
            Assert.Equal("AVAILABLE", log.NewStatus);
            Assert.Equal("USER", log.ActorType);
            Assert.Contains(log.EntityId, seatIds.Select(id => id.ToString()));
            Assert.Contains("S-46", log.Note);
        }

        // Audit trail ghi nhận SEAT_HOLD_RELEASED cho các bản ghi giữ chỗ
        var holdLogs = await context.OrderAuditLogs
            .Where(l => l.OrderId == orderId && l.EntityType == "SEAT_HOLD")
            .ToListAsync();

        Assert.Equal(2, holdLogs.Count);
        Assert.All(holdLogs, l =>
        {
            Assert.Equal("SEAT_HOLD_RELEASED", l.Action);
            Assert.Equal("RELEASED", l.NewStatus);
        });
    }

    [Fact]
    public async Task CancelOrderAndRefund_BroadcastsSignalRSeatReleased()
    {
        // Arrange: Mock SignalR Hub Context
        await using var context = CreateContext();
        var (userId, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 2);

        var mockClients = new Mock<IHubClients>();
        var mockGroupClient = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(showtimeId.ToString())).Returns(mockGroupClient.Object);

        var mockHubContext = new Mock<IHubContext<SeatStatusHub>>();
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = CreateOrdersController(context, userId, mockHubContext.Object);

        // Act
        var result = await controller.CancelOrderAndRefund(orderId);
        Assert.IsType<OkObjectResult>(result);

        // Assert: SignalR "SeatReleased" được phát tới nhóm showtime với danh sách seatIds
        mockGroupClient.Verify(
            c => c.SendCoreAsync(
                "SeatReleased",
                It.Is<object[]>(args => args.Length == 1),
                default),
            Times.Once);
    }

    [Fact]
    public async Task CancelledSeat_ReturnsAvailable_InPublicShowtimeGetSeats()
    {
        // Arrange
        await using var context = CreateContext();
        var (userId, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 1);
        var controller = CreateOrdersController(context, userId);

        var publicController = new PublicShowtimeController(
            context,
            new Mock<IDistributedCache>().Object,
            NullLogger<PublicShowtimeController>.Instance);

        // Trước khi huỷ: Sơ đồ ghế báo SOLD
        var preResult = await publicController.GetShowtimeSeats(showtimeId);
        var preOk = Assert.IsType<OkObjectResult>(preResult);
        var preResponse = Assert.IsType<ApiResponse<List<SeatStatusDto>>>(preOk.Value);
        var targetPreSeat = preResponse.Data!.First(s => s.Id == seatIds[0]);
        Assert.Equal("SOLD", targetPreSeat.Status);

        // Act: Huỷ vé
        await controller.CancelOrderAndRefund(orderId);

        // Assert: Sau khi huỷ, ghế trở lại trạng thái AVAILABLE trên sơ đồ
        var postResult = await publicController.GetShowtimeSeats(showtimeId);
        var postOk = Assert.IsType<OkObjectResult>(postResult);
        var postResponse = Assert.IsType<ApiResponse<List<SeatStatusDto>>>(postOk.Value);
        var targetPostSeat = postResponse.Data!.First(s => s.Id == seatIds[0]);
        Assert.Equal("AVAILABLE", targetPostSeat.Status);
    }

    [Fact]
    public async Task CancelledSeat_CanBeHeldAgain_ByAnotherUser()
    {
        // Arrange: S-46 - "và giữ được như ghế thường"
        await using var context = CreateContext();
        var (firstBuyerId, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 1);
        var controller = CreateOrdersController(context, firstBuyerId);

        var seatHoldService = new SeatHoldService(
            context,
            NullLogger<SeatHoldService>.Instance,
            new Mock<IHubContext<SeatStatusHub>>().Object,
            redis: null);

        var newBuyerId = Guid.NewGuid();
        context.Users.Add(new User
        {
            Id = newBuyerId,
            Username = "second_buyer",
            Email = "second_buyer@example.com",
            PasswordHash = "hash"
        });
        await context.SaveChangesAsync();

        // Thử giữ ghế trước khi huỷ -> Thất bại vì ghế đang SOLD
        var holdBeforeCancel = await seatHoldService.HoldSeatsAsync(showtimeId, seatIds, newBuyerId);
        Assert.Equal(HoldSeatsResultStatus.Conflict, holdBeforeCancel.Status);
        Assert.Contains("Ghế đã được bán", holdBeforeCancel.Message);

        // Act: Người mua đầu tiên huỷ vé
        await controller.CancelOrderAndRefund(orderId);

        // Assert: Người mua thứ hai giữ lại ghế vừa huỷ thành công như ghế thường!
        var holdAfterCancel = await seatHoldService.HoldSeatsAsync(showtimeId, seatIds, newBuyerId);
        Assert.Equal(HoldSeatsResultStatus.Success, holdAfterCancel.Status);

        // Ghế có bản ghi giữ chỗ ACTIVE bởi user mới
        var dbHold = await context.SeatHold.FirstOrDefaultAsync(h => h.SeatId == seatIds[0] && h.UserId == newBuyerId && h.Status == "ACTIVE");
        Assert.NotNull(dbHold);

        // Truy vấn sơ đồ ghế lúc này hiển thị trạng thái HELD
        var publicController = new PublicShowtimeController(context, new Mock<IDistributedCache>().Object, NullLogger<PublicShowtimeController>.Instance);
        var postHoldResult = await publicController.GetShowtimeSeats(showtimeId);
        var postHoldOk = Assert.IsType<OkObjectResult>(postHoldResult);
        var postHoldSeats = Assert.IsType<ApiResponse<List<SeatStatusDto>>>(postHoldOk.Value).Data!;
        Assert.Equal("HELD", postHoldSeats.First(s => s.Id == seatIds[0]).Status);
    }

    [Fact]
    public async Task CancelledSeat_CanBeHeldAgain_BySameUser_TicketLimitNotBlocked()
    {
        // Arrange: Người dùng cũ huỷ vé, số vé đã mua giảm về 0, có thể giữ lại ghế
        await using var context = CreateContext();
        var (buyerId, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 1);
        var controller = CreateOrdersController(context, buyerId);

        var seatHoldService = new SeatHoldService(
            context,
            NullLogger<SeatHoldService>.Instance,
            new Mock<IHubContext<SeatStatusHub>>().Object,
            redis: null);

        // Act: Huỷ vé
        await controller.CancelOrderAndRefund(orderId);

        // Assert: Người dùng cũ giữ lại chính chiếc ghế đó thành công
        var holdResult = await seatHoldService.HoldSeatsAsync(showtimeId, seatIds, buyerId);
        Assert.Equal(HoldSeatsResultStatus.Success, holdResult.Status);
    }

    [Fact]
    public async Task CancelledSeat_CanCompleteFullRebookingCycle()
    {
        // Arrange: Toàn bộ vòng đời: Vé đã mua -> Huỷ vé (S-46 nhả ghế) -> Giữ ghế lại -> Tạo đơn mới -> Thanh toán thành công
        await using var context = CreateContext();
        var (buyer1Id, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 1);
        var ordersController1 = CreateOrdersController(context, buyer1Id);

        // Bước 1: Huỷ đơn
        var cancelResult = await ordersController1.CancelOrderAndRefund(orderId);
        Assert.IsType<OkObjectResult>(cancelResult);

        // Bước 2: Khách hàng mới giữ ghế
        var buyer2Id = Guid.NewGuid();
        context.Users.Add(new User { Id = buyer2Id, Username = "buyer_two", Email = "two@test.com", PasswordHash = "pw" });
        await context.SaveChangesAsync();

        var seatHoldService = new SeatHoldService(
            context,
            NullLogger<SeatHoldService>.Instance,
            new Mock<IHubContext<SeatStatusHub>>().Object,
            redis: null);

        var holdResult = await seatHoldService.HoldSeatsAsync(showtimeId, seatIds, buyer2Id);
        Assert.Equal(HoldSeatsResultStatus.Success, holdResult.Status);

        // Bước 3: Tạo đơn hàng mới từ ghế vừa giữ
        var ordersController2 = CreateOrdersController(context, buyer2Id);
        var createOrderResult = await ordersController2.CreateOrderFromHolds(showtimeId);
        var orderOk = Assert.IsType<OkObjectResult>(createOrderResult);
        var newOrderDto = Assert.IsType<ApiResponse<OrderDto>>(orderOk.Value).Data!;

        // Bước 4: Thanh toán đơn mới
        var paymentTxCode = 998877L;
        context.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            OrderId = newOrderDto.Id,
            OrderCode = paymentTxCode,
            Amount = newOrderDto.TotalAmount,
            Status = "PENDING"
        });
        await context.SaveChangesAsync();

        var paymentService = new PaymentService(
            context,
            CreateMockGateway(),
            NullLogger<PaymentService>.Instance,
            Mock.Of<ITicketEmailQueue>());

        var txId = $"TX-NEW-{Guid.NewGuid():N}";
        var paymentResult = await paymentService.HandlePaymentResultAsync(
            PaymentResultDto.CreateSuccess(
                paymentTxCode,
                newOrderDto.TotalAmount,
                txId,
                DateTimeOffset.UtcNow,
                orderId: newOrderDto.Id.ToString()));

        Assert.True(paymentResult.Success);

        // Ghế trở lại trạng thái SOLD gắn với đơn hàng mới
        var finalSeat = await context.Seats.FindAsync(seatIds[0]);
        Assert.Equal("SOLD", finalSeat?.Status);

        var finalOrder = await context.Orders.FindAsync(newOrderDto.Id);
        Assert.Equal(OrderStatus.Paid, finalOrder?.Status);
    }

    [Fact]
    public async Task RefundService_ProcessOrderRefundAsync_AlsoReleasesSeats_ForCustomerRefund()
    {
        // Arrange: Kiểm thử RefundService trực tiếp
        await using var context = CreateContext();
        var (userId, showtimeId, orderId, seatIds) = await SeedPaidOrderWithSeatsAsync(context, seatCount: 1);

        var mockHubContext = new Mock<IHubContext<SeatStatusHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockGroup = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(showtimeId.ToString())).Returns(mockGroup.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var refundService = new RefundService(
            context,
            new OrderAuditService(context, NullLogger<OrderAuditService>.Instance),
            CreateMockGateway(),
            NullLogger<RefundService>.Instance,
            mockHubContext.Object);

        // Act: Gọi ProcessOrderRefundAsync với lý do CUSTOMER_REFUND
        var result = await refundService.ProcessOrderRefundAsync(
            orderId,
            225_000,
            OrderCancelReasons.CustomerRefund,
            "USER",
            "TestUser",
            userId);

        Assert.True(result.Success);

        // Assert: Ghế đổi sang AVAILABLE
        var seat = await context.Seats.FindAsync(seatIds[0]);
        Assert.Equal("AVAILABLE", seat?.Status);

        // Assert: Giữ chỗ đổi sang RELEASED
        var hold = await context.SeatHold.FirstOrDefaultAsync(h => h.SeatId == seatIds[0]);
        Assert.Equal("RELEASED", hold?.Status);

        // Assert: SignalR SeatReleased được gọi
        mockGroup.Verify(
            c => c.SendCoreAsync("SeatReleased", It.IsAny<object[]>(), default),
            Times.Once);
    }
}
