using System;
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
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests;

/// <summary>
/// Bộ kiểm thử Story S-45: Khách huỷ vé trước hạn và được hoàn tiền theo chính sách (bản demo).
/// Chính sách demo: huỷ trước 24 giờ so với giờ chiếu, hoàn 90% số tiền đã thanh toán (chờ Product duyệt).
/// </summary>
public sealed class RefundCancelS45Tests
{
    private const string MockGatewaySucceeded = "Succeeded";
    private const string MockGatewayFailed = "Failed";

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static DevelopmentMockPaymentGateway CreateMockGateway(string outcome = MockGatewaySucceeded) =>
        new(Options.Create(new MockPaymentOptions { RefundOutcome = outcome }));

    private static OrdersController CreateOrdersController(
        AppDbContext context,
        Guid userId,
        DevelopmentMockPaymentGateway? gateway = null,
        RefundPolicyOptions? policy = null)
    {
        return new OrdersController(
            context,
            NullLogger<OrdersController>.Instance,
            paymentGateway: gateway ?? CreateMockGateway(),
            refundPolicy: Options.Create(policy ?? new RefundPolicyOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                    ], "Test"))
                }
            }
        };
    }

    private static async Task<(Guid UserId, Guid ShowtimeId, Guid OrderId, long OrderCode, string TicketCode)> SeedPaidOrderAsync(
        AppDbContext context,
        DateTime showtimeStartUtc,
        int seatPrice,
        bool checkedIn = false,
        OrderStatus status = OrderStatus.Paid,
        int seatCount = 1)
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showtimeId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderCode = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ticketCode = $"TK-{Guid.NewGuid():N}";

        var user = new User
        {
            Id = userId,
            Username = $"user_{Guid.NewGuid():N}",
            Email = $"user_{Guid.NewGuid():N}@example.com",
            PasswordHash = "hashed"
        };

        var ev = new Event
        {
            Id = eventId,
            Title = "Sự kiện S-45",
            Location = "Địa điểm test",
            TotalSeats = 100
        };

        var showtime = new Showtime
        {
            Id = showtimeId,
            EventId = eventId,
            StartTime = showtimeStartUtc,
            EndTime = showtimeStartUtc.AddHours(2),
            AvailableSeats = 100
        };

        var category = new SeatCategory
        {
            Id = categoryId,
            ShowtimeId = showtimeId,
            Name = "Standard",
            Price = seatPrice
        };

        var orderItems = new System.Collections.Generic.List<OrderItem>();
        for (int i = 1; i <= seatCount; i++)
        {
            var seat = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                SeatCategoryId = categoryId,
                Row = "A",
                SeatNumber = i,
                Status = "SOLD"
            };
            context.Seats.Add(seat);

            var item = new OrderItem
            {
                SeatId = seat.Id,
                Price = seatPrice,
                IsCheckedIn = checkedIn
            };
            orderItems.Add(item);
        }

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = showtimeId,
            Status = status,
            TotalAmount = seatPrice * seatCount,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            OrderItems = orderItems
        };
        order.CalculateTotal();

        var ticket = new Ticket
        {
            OrderItemId = orderItems[0].Id,
            TicketCode = ticketCode
        };

        context.Users.Add(user);
        context.Events.Add(ev);
        context.Showtimes.Add(showtime);
        context.SeatCategories.Add(category);
        context.Orders.Add(order);
        context.Tickets.Add(ticket);
        context.PaymentTransactions.Add(new PaymentTransaction
        {
            OrderId = orderId,
            OrderCode = orderCode,
            Amount = order.TotalAmount,
            Status = "PAID",
            TransactionId = $"TX-{Guid.NewGuid():N}"
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return (userId, showtimeId, orderId, orderCode, ticketCode);
    }

    [Fact]
    public async Task Cancel_PaidOrderBeforeCutoff_CreatesRefundAndCancelsOrder()
    {
        // Arrange: suất chiếu sau 3 ngày (> 24h) → hợp lệ; Mock gateway mô phỏng hoàn thành công
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000, seatCount: 2);
        var controller = CreateOrdersController(context, userId);

        // Act
        var result = await controller.CancelOrderAndRefund(orderId);

        // Assert: phản hồi 200
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.True(response.Success);
        Assert.Contains("Đơn hàng đã được huỷ", response.Message);

        // Assert: đơn chuyển Cancelled kèm lý do khách huỷ sau thanh toán
        var order = await context.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("CUSTOMER_REFUND", order.CancelReason);

        // Assert: đúng một bản ghi hoàn tiền, Succeeded, số tiền = 90% (200.000 → 180.000)
        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
        Assert.Equal("Succeeded", refund.Status);
        Assert.Equal(180_000, refund.Amount);
        Assert.Null(refund.FailureReason);
    }

    [Fact]
    public async Task Cancel_OrderStartingInLessThan24Hours_IsRejected()
    {
        // Arrange: suất chiếu sau 10 giờ → sau mốc cutoff 24 giờ
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddHours(10), 100_000);
        var controller = CreateOrdersController(context, userId);

        // Act
        var result = await controller.CancelOrderAndRefund(orderId);

        // Assert: 409, đơn vẫn Paid, không có refund
        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var response = Assert.IsType<ApiResponse<object>>(conflict.Value);
        Assert.Contains("quá hạn huỷ", response.Message, StringComparison.OrdinalIgnoreCase);

        var order = await context.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(context.Refunds.AsNoTracking());
    }

    [Fact]
    public async Task Cancel_AnotherUsersOrder_IsForbidden()
    {
        await using var context = CreateContext();
        var (_, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000);
        var intruderId = Guid.NewGuid();
        var controller = CreateOrdersController(context, intruderId);

        var result = await controller.CancelOrderAndRefund(orderId);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var order = await context.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(context.Refunds.AsNoTracking());
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public async Task Cancel_NonPaidOrder_IsRejectedWithoutRefund(OrderStatus status)
    {
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000, status: status);
        var controller = CreateOrdersController(context, userId);

        var result = await controller.CancelOrderAndRefund(orderId);

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        var order = await context.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(status, order.Status);
        Assert.Empty(context.Refunds.AsNoTracking());
    }

    [Fact]
    public async Task Cancel_OrderWithCheckedInTicket_IsRejected()
    {
        // Arrange: đơn Paid hợp lệ nhưng một vé đã check-in
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000, checkedIn: true);
        var controller = CreateOrdersController(context, userId);

        var result = await controller.CancelOrderAndRefund(orderId);

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var response = Assert.IsType<ApiResponse<object>>(conflict.Value);
        Assert.Contains("đã sử dụng để vào cửa", response.Message);

        var order = await context.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(context.Refunds.AsNoTracking());
    }

    [Fact]
    public async Task RefundAmount_IsRoundedDownToWholeVnd()
    {
        // 99.999 * 90% = 89.999,1 → làm tròn xuống 89.999
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 99_999);
        var controller = CreateOrdersController(context, userId);

        var result = await controller.CancelOrderAndRefund(orderId);

        Assert.IsType<OkObjectResult>(result);
        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
        Assert.Equal(89_999, refund.Amount);
    }

    [Fact]
    public async Task Cancel_SecondRequest_DoesNotCreateDuplicateRefund()
    {
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000);
        var controller = CreateOrdersController(context, userId);

        var first = await controller.CancelOrderAndRefund(orderId);
        Assert.IsType<OkObjectResult>(first);

        // Bấm lần 2 (kể cả khi frontend gửi lặp)
        var second = await controller.CancelOrderAndRefund(orderId);
        var conflict = Assert.IsType<ObjectResult>(second);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        Assert.Single(context.Refunds.AsNoTracking().Where(r => r.OrderId == orderId));
    }

    [Fact]
    public async Task MockGatewayFailure_RefundMarkedFailedWithReason_NotSucceeded()
    {
        // Arrange: Mock gateway mô phỏng hoàn tiền thất bại
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000);
        var controller = CreateOrdersController(context, userId, CreateMockGateway(MockGatewayFailed));

        // Act
        var result = await controller.CancelOrderAndRefund(orderId);

        // Assert: đơn vẫn bị huỷ nhưng refund = Failed kèm lý do; không bao giờ ghi Succeeded
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Contains("THẤT BẠI", response.Message);

        var order = await context.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Cancelled, order.Status);

        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
        Assert.Equal("Failed", refund.Status);
        Assert.NotNull(refund.FailureReason);
        Assert.NotEqual("Succeeded", refund.Status);
    }

    [Fact]
    public async Task PayOSStyleGateway_NoRefundSupport_RefundMarkedManualPending()
    {
        // Arrange: gateway không hỗ trợ hoàn tự động (giống PayOS thật theo tài liệu công bố)
        await using var context = CreateContext();
        var (userId, _, orderId, _, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000);

        var controller = new OrdersController(
            context,
            NullLogger<OrdersController>.Instance,
            paymentGateway: new NoRefundGatewayStub(),
            refundPolicy: Options.Create(new RefundPolicyOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                    ], "Test"))
                }
            }
        };

        var result = await controller.CancelOrderAndRefund(orderId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Contains("chờ xử lý thủ công", response.Message);

        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
        Assert.Equal("ManualPending", refund.Status);
    }

    [Fact]
    public async Task CancelledOrderTicket_IsNotIncludedInCheckInManifest()
    {
        // Arrange: vé thuộc đơn Paid rồi bị huỷ hợp lệ
        await using var context = CreateContext();
        var (userId, showtimeId, orderId, _, ticketCode) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000);

        var controller = CreateOrdersController(context, userId);
        var cancelResult = await controller.CancelOrderAndRefund(orderId);
        Assert.IsType<OkObjectResult>(cancelResult);

        // Assert: Manifest soát vé chỉ lấy vé có OrderStatus.Paid -> vé đơn huỷ bị loại trừ tự động
        var checkInController = new TicketCheckInController(
            context,
            new EventTicketBooking.Api.Services.Implementations.QrSignatureService());

        var manifestResult = await checkInController.GetOfflineTickets(showtimeId);
        var okResult = Assert.IsType<OkObjectResult>(manifestResult);
        var response = Assert.IsType<OfflineTicketSyncResponseDto>(okResult.Value);
        Assert.DoesNotContain(response.Tickets, t => t.TicketCode == ticketCode);
    }

    [Fact]
    public async Task LatePaymentResult_AfterCustomerCancel_KeepsOrderCancelled()
    {
        // Kiểm tra cạnh tranh cơ bản: webhook/kết quả thanh toán muộn đến sau khi khách đã huỷ hợp lệ
        await using var context = CreateContext();
        var (userId, _, orderId, orderCode, _) = await SeedPaidOrderAsync(context, DateTime.UtcNow.AddDays(3), 100_000);

        var controller = CreateOrdersController(context, userId);
        var cancelResult = await controller.CancelOrderAndRefund(orderId);
        Assert.IsType<OkObjectResult>(cancelResult);

        var paymentService = new PaymentService(
            context,
            CreateMockGateway(),
            NullLogger<PaymentService>.Instance,
            Mock.Of<ITicketEmailQueue>());

        // Act: kết quả thanh toán Success muộn (webhook replay) không được chuyển đơn đã huỷ sang Paid/NeedsAttention
        var execution = await paymentService.HandlePaymentResultAsync(
            PaymentResultDto.CreateSuccess(orderCode, 100_000, $"TX-{Guid.NewGuid():N}", DateTimeOffset.UtcNow));

        Assert.False(execution.Success);

        context.ChangeTracker.Clear();
        var order = await context.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("CUSTOMER_REFUND", order.CancelReason);

        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.OrderId == orderId);
        Assert.Equal("Succeeded", refund.Status);
    }

    /// <summary>
    /// Gateway giả lập kiểu payOS: không hỗ trợ hoàn tiền tự động (SupportsRefund = false).
    /// </summary>
    private sealed class NoRefundGatewayStub : EventTicketBooking.Api.Services.Interfaces.IPaymentGateway
    {
        public string ProviderName => "NoRefundStub";
        public bool SupportsRefund => false;

        public Task<PaymentCreationResult> CreatePaymentAsync(PaymentCreationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PaymentCreationResult.CreateSuccess("https://example.invalid/pay", null, null, request.OrderCode));

        public bool VerifyWebhookSignature(string webhookPayload, string signature, string? webhookSecret = null) => false;

        public WebhookParseResult ParseWebhookData(string webhookPayload) =>
            WebhookParseResult.CreateFailure("stub");

        public Task<PaymentResultDto?> QueryPaymentStatusAsync(long orderCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<PaymentResultDto?>(null);

        public Task<RefundCreationResult> CreateRefundAsync(RefundCreationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(RefundCreationResult.CreateManualRequired("Stub không hỗ trợ hoàn tiền tự động."));
    }
}

