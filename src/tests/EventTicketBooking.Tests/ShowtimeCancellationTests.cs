using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
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
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class ShowtimeCancellationTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Guid _organizerId = Guid.NewGuid();
        private readonly Guid _strangerId = Guid.NewGuid();
        private readonly Guid _adminId = Guid.NewGuid();

        public ShowtimeCancellationTests()
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

        private EventController CreateController(
            Guid? userId,
            string role = "Organizer",
            IPaymentGateway? paymentGateway = null,
            IRefundService? refundService = null)
        {
            var controller = new EventController(
                _context,
                new OrderAuditService(_context, NullLogger<OrderAuditService>.Instance),
                null,
                refundService,
                paymentGateway ?? new DevelopmentMockPaymentGateway(Options.Create(new MockPaymentOptions { RefundOutcome = "Succeeded" })));

            var httpContext = new DefaultHttpContext();
            if (userId.HasValue)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                    new Claim(ClaimTypes.Role, role),
                    new Claim("name", "TestUser")
                };
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            return controller;
        }

        private (Event ev, Showtime showtime) CreateEventAndShowtime(
            Guid ownerId,
            ShowtimeStatus initialStatus = ShowtimeStatus.OnSale,
            DateTime? startTime = null,
            DateTime? endTime = null)
        {
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                Title = "Test Event for S-47",
                Location = "Venue Hall",
                StartTime = startTime ?? DateTime.UtcNow.AddDays(1),
                EndTime = endTime ?? DateTime.UtcNow.AddDays(1).AddHours(3)
            };

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = ev.StartTime,
                EndTime = ev.EndTime,
                AvailableSeats = 100
            };

            var cat = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Name = "VIP",
                Price = 500_000
            };
            showtime.SeatCategories.Add(cat);

            if (initialStatus == ShowtimeStatus.OnSale)
            {
                showtime.ChangeStatus(ShowtimeStatus.OnSale);
            }
            else if (initialStatus == ShowtimeStatus.Closed)
            {
                showtime.ChangeStatus(ShowtimeStatus.OnSale);
                showtime.ChangeStatus(ShowtimeStatus.Closed);
            }

            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);
            _context.SaveChanges();

            return (ev, showtime);
        }

        [Fact]
        public async Task CancelShowtime_OrganizerCancelsOnSale_Success()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var controller = CreateController(_organizerId, "Organizer");

            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.Equal(ShowtimeStatus.Cancelled, apiResponse.Data?.Status);

            var dbShowtime = await _context.Showtimes.FindAsync(showtime.Id);
            Assert.NotNull(dbShowtime);
            Assert.Equal(ShowtimeStatus.Cancelled, dbShowtime.Status);
        }

        [Fact]
        public async Task CancelShowtime_OrganizerCancelsClosed_Success()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.Closed);
            var controller = CreateController(_organizerId, "Organizer");

            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.Equal(ShowtimeStatus.Cancelled, apiResponse.Data?.Status);
        }

        [Fact]
        public async Task CancelShowtime_AdminCanCancelAnyEvent_Success()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var controller = CreateController(_adminId, "Admin");

            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.Equal(ShowtimeStatus.Cancelled, apiResponse.Data?.Status);
        }

        [Fact]
        public async Task CancelShowtime_StrangerOrganizer_Returns403()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var controller = CreateController(_strangerId, "Organizer");

            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
        }

        [Fact]
        public async Task CancelShowtime_UnauthenticatedUser_Returns401()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var controller = CreateController(null);

            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status401Unauthorized, statusResult.StatusCode);
        }

        [Fact]
        public async Task CancelShowtime_NonExistentEvent_Returns404()
        {
            var controller = CreateController(_organizerId, "Organizer");

            var result = await controller.CancelShowtime(Guid.NewGuid(), Guid.NewGuid());

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<object>>(notFoundResult.Value);
            Assert.False(apiResponse.Success);
        }

        [Fact]
        public async Task CancelShowtime_MismatchedEventAndShowtime_Returns404()
        {
            var (ev1, showtime1) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var (ev2, showtime2) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var controller = CreateController(_organizerId, "Organizer");

            // Cố tình truyền ID sự kiện 1 với ID suất diễn của sự kiện 2
            var result = await controller.CancelShowtime(ev1.Id, showtime2.Id);

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<object>>(notFoundResult.Value);
            Assert.False(apiResponse.Success);
        }

        [Fact]
        public async Task CancelShowtime_DraftShowtime_Returns400()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.Draft);
            var controller = CreateController(_organizerId, "Organizer");

            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);
            Assert.False(apiResponse.Success);
            Assert.Contains("Nháp", apiResponse.Message);
        }

        [Fact]
        public async Task CancelShowtime_AlreadyCancelledShowtime_Returns400()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var controller = CreateController(_organizerId, "Organizer");

            // Lần 1: huỷ thành công
            var result1 = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result1);

            // Lần 2: huỷ lại lần nữa -> bị từ chối 400
            var result2 = await controller.CancelShowtime(ev.Id, showtime.Id);
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result2);
            var apiResponse = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);
            Assert.False(apiResponse.Success);
            Assert.Contains("đã ở trạng thái huỷ", apiResponse.Message);
        }

        [Fact]
        public async Task CancelShowtime_EndedInPast_Returns400()
        {
            var pastStart = DateTime.UtcNow.AddDays(-2);
            var pastEnd = DateTime.UtcNow.AddDays(-1);
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.Closed, pastStart, pastEnd);
            var controller = CreateController(_organizerId, "Organizer");

            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);
            Assert.False(apiResponse.Success);
            Assert.Contains("đã kết thúc trong quá khứ", apiResponse.Message);
        }

        [Fact]
        public async Task CancelShowtime_OpenSale_CannotReopenCancelledShowtime()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var controller = CreateController(_organizerId, "Organizer");

            await controller.CancelShowtime(ev.Id, showtime.Id);

            // Mở bán lại suất đã huỷ
            var openSaleResult = await controller.OpenSale(ev.Id, showtime.Id);
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(openSaleResult);
            var apiResponse = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);
            Assert.False(apiResponse.Success);
            Assert.Contains("Không thể mở bán suất diễn đã bị huỷ", apiResponse.Message);
        }

        [Fact]
        public async Task CancelShowtime_PendingOrders_CancelledAndHoldsExpiredAndSeatsAvailable()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var category = showtime.SeatCategories.First();

            var seat1 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "A", SeatNumber = 1, Status = "HELD" };
            var seat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "A", SeatNumber = 2, Status = "HELD" };
            _context.Seats.AddRange(seat1, seat2);

            var buyerId = Guid.NewGuid();
            var hold1 = new SeatHolds { Id = Guid.NewGuid(), SeatId = seat1.Id, UserId = buyerId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
            var hold2 = new SeatHolds { Id = Guid.NewGuid(), SeatId = seat2.Id, UserId = buyerId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
            _context.SeatHold.AddRange(hold1, hold2);

            var pendingOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyerId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Pending,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                OrderItems = new List<OrderItem>
                {
                    new OrderItem { Id = Guid.NewGuid(), SeatId = seat1.Id, Price = 500_000 },
                    new OrderItem { Id = Guid.NewGuid(), SeatId = seat2.Id, Price = 500_000 }
                }
            };
            _context.Orders.Add(pendingOrder);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            Assert.IsType<OkObjectResult>(result);

            // Kiểm tra trạng thái đơn Pending -> Cancelled
            var dbOrder = await _context.Orders.FindAsync(pendingOrder.Id);
            Assert.NotNull(dbOrder);
            Assert.Equal(OrderStatus.Cancelled, dbOrder.Status);

            // Kiểm tra giữ chỗ -> EXPIRED
            var dbHold1 = await _context.SeatHold.FindAsync(hold1.Id);
            var dbHold2 = await _context.SeatHold.FindAsync(hold2.Id);
            Assert.Equal("EXPIRED", dbHold1?.Status);
            Assert.Equal("EXPIRED", dbHold2?.Status);

            // Kiểm tra ghế -> AVAILABLE
            var dbSeat1 = await _context.Seats.FindAsync(seat1.Id);
            var dbSeat2 = await _context.Seats.FindAsync(seat2.Id);
            Assert.Equal("AVAILABLE", dbSeat1?.Status);
            Assert.Equal("AVAILABLE", dbSeat2?.Status);
        }

        [Fact]
        public async Task CancelShowtime_PaidOrders_CancelledAndRetainsTicketAndCheckInHistory()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var category = showtime.SeatCategories.First();

            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "B", SeatNumber = 5, Status = "SOLD" };
            _context.Seats.Add(seat);

            var buyer = new User { Id = Guid.NewGuid(), Username = "buyer1", Email = "buyer1@test.com", FullName = "Buyer One", PasswordHash = "pw" };
            _context.Users.Add(buyer);

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                SeatId = seat.Id,
                Price = 500_000,
                IsCheckedIn = true,
                CheckInGate = "GATE_NORTH",
                CheckInTime = DateTimeOffset.UtcNow.AddMinutes(-30)
            };

            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = orderItem.Id,
                TicketCode = "TCK-TEST-S47",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
            };
            orderItem.Ticket = ticket;

            var paidOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyer.Id,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500_000,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                OrderItems = new List<OrderItem> { orderItem }
            };
            _context.Orders.Add(paidOrder);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);

            Assert.IsType<OkObjectResult>(result);

            // 1. Đơn hàng chuyển sang Cancelled
            var dbOrder = await _context.Orders.FindAsync(paidOrder.Id);
            Assert.NotNull(dbOrder);
            Assert.Equal(OrderStatus.Cancelled, dbOrder.Status);

            // 2. Thông tin check-in và ticket vẫn được bảo lưu trọn vẹn
            var dbItem = await _context.OrderItems.Include(oi => oi.Ticket).FirstOrDefaultAsync(oi => oi.Id == orderItem.Id);
            Assert.NotNull(dbItem);
            Assert.True(dbItem.IsCheckedIn);
            Assert.Equal("GATE_NORTH", dbItem.CheckInGate);
            Assert.NotNull(dbItem.CheckInTime);
            Assert.NotNull(dbItem.Ticket);
            Assert.Equal("TCK-TEST-S47", dbItem.Ticket.TicketCode);

            // 3. Ghế vẫn giữ SOLD (không bị nhả về AVAILABLE vì cần đối soát hoàn tiền)
            var dbSeat = await _context.Seats.FindAsync(seat.Id);
            Assert.Equal("SOLD", dbSeat?.Status);
        }

        [Fact]
        public async Task CancelShowtime_CancelledOrderTicket_RejectedByS39_WithTicketCancelledReason()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var category = showtime.SeatCategories.First();

            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "C", SeatNumber = 10, Status = "SOLD" };
            _context.Seats.Add(seat);

            var buyer = new User { Id = Guid.NewGuid(), Username = "buyer2", Email = "buyer2@test.com", PasswordHash = "pw" };
            _context.Users.Add(buyer);

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                SeatId = seat.Id,
                Price = 500_000,
                IsCheckedIn = false
            };
            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = orderItem.Id,
                TicketCode = "TCK-S39-CHECK",
                CreatedAt = DateTimeOffset.UtcNow
            };
            orderItem.Ticket = ticket;

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyer.Id,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500_000,
                OrderItems = new List<OrderItem> { orderItem }
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            // Huỷ suất diễn
            var controller = CreateController(_organizerId, "Organizer");
            await controller.CancelShowtime(ev.Id, showtime.Id);

            // Giả lập nhân viên soát vé quét vé qua TicketCheckInController (S-39)
            var staffId = Guid.NewGuid();
            var checkInController = new TicketCheckInController(_context, NullLogger<TicketCheckInController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, staffId.ToString()),
                            new Claim(ClaimTypes.Role, "Staff")
                        }, "Test"))
                    }
                }
            };

            var scanRequest = new TicketScanRequestDto
            {
                TicketCode = "TCK-S39-CHECK",
                SelectedShowtimeId = showtime.Id,
                GateName = "GATE_1"
            };

            var scanResult = await checkInController.ScanTicket(scanRequest);

            var badRequest = Assert.IsType<BadRequestObjectResult>(scanResult);
            var json = JsonSerializer.SerializeToElement(badRequest.Value);
            Assert.Equal("TICKET_CANCELLED", json.GetProperty("Reason").GetString());
            Assert.Contains("Vé đã huỷ ngày", json.GetProperty("Message").GetString());
        }

        [Fact]
        public async Task CancelShowtime_CancelledOrder_S38GetOrderTickets_ReturnsEmptyList()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var category = showtime.SeatCategories.First();

            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "D", SeatNumber = 12, Status = "SOLD" };
            _context.Seats.Add(seat);

            var buyerId = Guid.NewGuid();
            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                SeatId = seat.Id,
                Price = 500_000
            };
            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = orderItem.Id,
                TicketCode = "TCK-S38-QR",
                CreatedAt = DateTimeOffset.UtcNow
            };
            orderItem.Ticket = ticket;

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyerId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500_000,
                OrderItems = new List<OrderItem> { orderItem }
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            // Huỷ suất diễn
            var controller = CreateController(_organizerId, "Organizer");
            await controller.CancelShowtime(ev.Id, showtime.Id);

            // Buyer truy cập API GetOrderTickets (S-38)
            var ordersController = new OrdersController(_context, NullLogger<OrdersController>.Instance, new Mock<ITicketEmailQueue>().Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, buyerId.ToString()),
                            new Claim(ClaimTypes.Role, "User")
                        }, "Test"))
                    }
                }
            };

            var ticketResult = await ordersController.GetOrderTickets(order.Id);

            var okResult = Assert.IsType<OkObjectResult>(ticketResult);
            var apiResponse = Assert.IsType<ApiResponse<List<TicketDto>>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.Empty(apiResponse.Data!); // Không trả về vé nào vì đơn đã huỷ!
        }

        [Fact]
        public async Task CancelShowtime_AuditLogRecordedWithShowtimeCancelledAwaitingRefund()
        {
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var category = showtime.SeatCategories.First();

            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "E", SeatNumber = 20, Status = "SOLD" };
            _context.Seats.Add(seat);

            var buyerId = Guid.NewGuid();
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyerId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500_000
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");
            await controller.CancelShowtime(ev.Id, showtime.Id);

            var logs = await _context.OrderAuditLogs.Where(l => l.OrderId == order.Id).ToListAsync();
            Assert.NotEmpty(logs);

            var cancelLog = Assert.Single(logs, l => l.Action == "SHOWTIME_CANCELLED");
            Assert.Equal("Paid", cancelLog.OldStatus);
            Assert.Equal("Cancelled", cancelLog.NewStatus);
            Assert.Equal("ORGANIZER", cancelLog.ActorType);
            Assert.Equal("SHOWTIME_CANCELLED_AWAITING_REFUND", cancelLog.Note);
        }

        [Fact]
        public async Task CancelShowtime_OtherShowtimesOrdersNotAffected()
        {
            var (ev1, showtime1) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var (ev2, showtime2) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);

            var otherOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ShowtimeId = showtime2.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 300_000
            };
            _context.Orders.Add(otherOrder);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");
            await controller.CancelShowtime(ev1.Id, showtime1.Id);

            // Suất diễn 2 và đơn hàng của suất diễn 2 hoàn toàn không bị ảnh hưởng
            var dbOrder2 = await _context.Orders.FindAsync(otherOrder.Id);
            Assert.NotNull(dbOrder2);
            Assert.Equal(OrderStatus.Paid, dbOrder2.Status);

            var dbShowtime2 = await _context.Showtimes.FindAsync(showtime2.Id);
            Assert.NotNull(dbShowtime2);
            Assert.Equal(ShowtimeStatus.OnSale, dbShowtime2.Status);
        }

        #region S-47 Task 2 Tests: Hoàn tiền khi huỷ suất diễn

        [Fact]
        public async Task CancelShowtime_PaidOrder_Refunds100PercentTotalAmount_WithMockGatewaySuccess()
        {
            // Arrange: Suất diễn có đơn hàng Paid trị giá 500.000 VND
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var buyerId = Guid.NewGuid();
            var paidOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyerId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500_000,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
            };
            _context.Orders.Add(paidOrder);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");

            // Act
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result);

            // Assert: Đơn hàng chuyển sang Cancelled với lý do SHOWTIME_CANCELLED
            var dbOrder = await _context.Orders.FindAsync(paidOrder.Id);
            Assert.NotNull(dbOrder);
            Assert.Equal(OrderStatus.Cancelled, dbOrder.Status);
            Assert.Equal(OrderCancelReasons.ShowtimeCancelled, dbOrder.CancelReason);

            // Assert: Bản ghi hoàn tiền được tạo và hoàn đúng 100% (500.000 VND, không áp dụng 90% hay cutoff của S-45)
            var dbRefund = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == paidOrder.Id);
            Assert.NotNull(dbRefund);
            Assert.Equal(500_000, dbRefund.Amount);
            Assert.Equal("Succeeded", dbRefund.Status);
            Assert.Null(dbRefund.FailureReason);

            // Assert: Audit log ghi nhận REFUND_REQUESTED và REFUND_SUCCEEDED
            var logs = await _context.OrderAuditLogs.Where(l => l.OrderId == paidOrder.Id).ToListAsync();
            Assert.Contains(logs, l => l.Action == "REFUND_REQUESTED");
            Assert.Contains(logs, l => l.Action == "REFUND_SUCCEEDED");
        }

        [Fact]
        public async Task CancelShowtime_PaidOrder_MockGatewayFailure_RefundMarkedFailed()
        {
            // Arrange: Cổng thanh toán giả lập thất bại
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var paidOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 300_000
            };
            _context.Orders.Add(paidOrder);
            await _context.SaveChangesAsync();

            var failedGateway = new DevelopmentMockPaymentGateway(Options.Create(new MockPaymentOptions { RefundOutcome = "Failed" }));
            var controller = CreateController(_organizerId, "Organizer", paymentGateway: failedGateway);

            // Act
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result);

            // Assert: Đơn vẫn bị huỷ nhưng Refund ở trạng thái Failed kèm FailureReason, không ghi nhận Succeeded
            var dbOrder = await _context.Orders.FindAsync(paidOrder.Id);
            Assert.NotNull(dbOrder);
            Assert.Equal(OrderStatus.Cancelled, dbOrder.Status);
            Assert.Equal(OrderCancelReasons.ShowtimeCancelled, dbOrder.CancelReason);

            var dbRefund = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == paidOrder.Id);
            Assert.NotNull(dbRefund);
            Assert.Equal(300_000, dbRefund.Amount);
            Assert.Equal("Failed", dbRefund.Status);
            Assert.NotNull(dbRefund.FailureReason);

            var logs = await _context.OrderAuditLogs.Where(l => l.OrderId == paidOrder.Id).ToListAsync();
            Assert.Contains(logs, l => l.Action == "REFUND_FAILED");
        }

        [Fact]
        public async Task CancelShowtime_PaidOrder_PayOSGateway_RefundMarkedManualPending()
        {
            // Arrange: PayOS không hỗ trợ API hoàn tiền tự động theo tài liệu -> hoàn tiền vào ManualPending
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var paidOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 450_000
            };
            _context.Orders.Add(paidOrder);
            await _context.SaveChangesAsync();

            var payOsGateway = new PayOSPaymentGateway(new System.Net.Http.HttpClient(), Options.Create(new PayOSOptions()), NullLogger<PayOSPaymentGateway>.Instance);
            var controller = CreateController(_organizerId, "Organizer", paymentGateway: payOsGateway);

            // Act
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result);

            // Assert: Refund ở trạng thái ManualPending chờ nhân viên chuyển khoản thủ công
            var dbRefund = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == paidOrder.Id);
            Assert.NotNull(dbRefund);
            Assert.Equal(450_000, dbRefund.Amount);
            Assert.Equal("ManualPending", dbRefund.Status);

            var logs = await _context.OrderAuditLogs.Where(l => l.OrderId == paidOrder.Id).ToListAsync();
            Assert.Contains(logs, l => l.Action == "REFUND_MANUAL_PENDING");
        }

        [Fact]
        public async Task CancelShowtime_OrderAlreadyRefundedByCustomer_DoesNotCreateDuplicateRefund()
        {
            // Arrange: Đơn hàng đã được khách chủ động huỷ trước đó theo S-45 (có bản ghi Refund tồn tại)
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var existingRefundOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Cancelled,
                CancelReason = OrderCancelReasons.CustomerRefund,
                TotalAmount = 200_000
            };
            var existingRefund = new Refund
            {
                Id = Guid.NewGuid(),
                OrderId = existingRefundOrder.Id,
                Amount = 180_000, // 90% theo S-45
                Status = "Succeeded",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-5)
            };
            _context.Orders.Add(existingRefundOrder);
            _context.Refunds.Add(existingRefund);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");

            // Act: Huỷ suất diễn
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result);

            // Assert: Duy nhất 1 bản ghi Refund tồn tại, không sinh Refund thứ hai (chống vi phạm IX_refunds_OrderId_Unique)
            var refunds = await _context.Refunds.Where(r => r.OrderId == existingRefundOrder.Id).ToListAsync();
            Assert.Single(refunds);
            Assert.Equal(existingRefund.Id, refunds[0].Id);
            Assert.Equal(180_000, refunds[0].Amount); // Giữ nguyên thông tin cũ của S-45
        }

        [Fact]
        public async Task CancelShowtime_CheckedInTicket_RetainsHistoryAndRefunds100Percent()
        {
            // Arrange: Khách đã check-in nhưng suất diễn bị huỷ giữa chừng (thiên tai/sự cố kỹ thuật)
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var category = showtime.SeatCategories.First();
            var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "F", SeatNumber = 8, Status = "SOLD" };
            _context.Seats.Add(seat);

            var checkInTime = DateTimeOffset.UtcNow.AddMinutes(-45);
            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                SeatId = seat.Id,
                Price = 600_000,
                IsCheckedIn = true,
                CheckInGate = "GATE_MAIN",
                CheckInTime = checkInTime
            };
            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = orderItem.Id,
                TicketCode = "TCK-CHECKEDIN-S47",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
            };
            orderItem.Ticket = ticket;

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 600_000,
                OrderItems = new List<OrderItem> { orderItem }
            };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");

            // Act
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result);

            // Assert: Lịch sử vé và check-in vẫn bảo toàn trọn vẹn
            var dbItem = await _context.OrderItems.Include(oi => oi.Ticket).FirstAsync(oi => oi.Id == orderItem.Id);
            Assert.True(dbItem.IsCheckedIn);
            Assert.Equal("GATE_MAIN", dbItem.CheckInGate);
            Assert.Equal(checkInTime, dbItem.CheckInTime);
            Assert.NotNull(dbItem.Ticket);

            // Assert: Đơn vẫn được hoàn 100% vì lỗi thuộc về phía Ban tổ chức huỷ suất (khác với S-45 từ chối huỷ)
            var dbRefund = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == order.Id);
            Assert.NotNull(dbRefund);
            Assert.Equal(600_000, dbRefund.Amount);
            Assert.Equal("Succeeded", dbRefund.Status);
        }

        [Fact]
        public async Task CancelShowtime_MultiplePaidOrders_ProcessedIndependently_OneFailureDoesNotBlockOthers()
        {
            // Arrange: Suất diễn có 3 đơn hàng Paid
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var order1 = new Order { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid, TotalAmount = 100_000 };
            var order2 = new Order { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid, TotalAmount = 200_000 };
            var order3 = new Order { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid, TotalAmount = 300_000 };
            _context.Orders.AddRange(order1, order2, order3);
            await _context.SaveChangesAsync();

            // Cổng mô phỏng: Đơn 2 thất bại, Đơn 1 và 3 thành công
            var selectiveGateway = new CustomMultiOrderPaymentGateway(orderId => orderId != order2.Id);
            var controller = CreateController(_organizerId, "Organizer", paymentGateway: selectiveGateway);

            // Act
            var result = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result);

            // Assert: Cả 3 đơn đều đã chuyển Cancelled
            Assert.Equal(OrderStatus.Cancelled, (await _context.Orders.FindAsync(order1.Id))?.Status);
            Assert.Equal(OrderStatus.Cancelled, (await _context.Orders.FindAsync(order2.Id))?.Status);
            Assert.Equal(OrderStatus.Cancelled, (await _context.Orders.FindAsync(order3.Id))?.Status);

            // Assert: Đơn 1 thành công, Đơn 2 thất bại, Đơn 3 vẫn thành công (lỗi đơn 2 không chặn đơn 3)
            var refund1 = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == order1.Id);
            var refund2 = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == order2.Id);
            var refund3 = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == order3.Id);

            Assert.NotNull(refund1);
            Assert.Equal("Succeeded", refund1.Status);

            Assert.NotNull(refund2);
            Assert.Equal("Failed", refund2.Status);

            Assert.NotNull(refund3);
            Assert.Equal("Succeeded", refund3.Status);
        }

        [Fact]
        public async Task CancelShowtime_CalledRepeatedly_DoesNotDuplicateRefunds()
        {
            // Arrange: Suất diễn có 1 đơn Paid
            var (ev, showtime) = CreateEventAndShowtime(_organizerId, ShowtimeStatus.OnSale);
            var order = new Order { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid, TotalAmount = 150_000 };
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            var controller = CreateController(_organizerId, "Organizer");

            // Act 1: Huỷ suất diễn lần 1 thành công
            var result1 = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<OkObjectResult>(result1);

            // Act 2: Cố huỷ lần 2 -> Controller từ chối 400 vì suất đã Cancelled
            var result2 = await controller.CancelShowtime(ev.Id, showtime.Id);
            Assert.IsType<BadRequestObjectResult>(result2);

            // Act 3: Giả định retry trực tiếp trên RefundService cho cùng order
            var refundService = new RefundService(_context, new OrderAuditService(_context, NullLogger<OrderAuditService>.Instance), new DevelopmentMockPaymentGateway(Options.Create(new MockPaymentOptions { RefundOutcome = "Succeeded" })));
            var retryResults = await refundService.ProcessShowtimeRefundsAsync(showtime.Id, new[] { order.Id }, "ORGANIZER", "TestOrganizer");

            // Assert: Lần gọi retry trả về Skipped, không tạo bản ghi mới
            Assert.Single(retryResults);
            Assert.True(retryResults[0].Skipped);

            var refunds = await _context.Refunds.Where(r => r.OrderId == order.Id).ToListAsync();
            Assert.Single(refunds);
            Assert.Equal(150_000, refunds[0].Amount);
        }

        private class CustomMultiOrderPaymentGateway : IPaymentGateway
        {
            private readonly Func<Guid, bool> _shouldSucceed;
            public CustomMultiOrderPaymentGateway(Func<Guid, bool> shouldSucceed) => _shouldSucceed = shouldSucceed;
            public string ProviderName => "CustomMock";
            public bool SupportsRefund => true;
            public Task<PaymentCreationResult> CreatePaymentAsync(PaymentCreationRequest request, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
            public bool VerifyWebhookSignature(string webhookPayload, string signature, string? webhookSecret = null) => true;
            public WebhookParseResult ParseWebhookData(string webhookPayload) => throw new NotImplementedException();
            public Task<PaymentResultDto?> QueryPaymentStatusAsync(long orderCode, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult<PaymentResultDto?>(null);
            public Task<RefundCreationResult> CreateRefundAsync(RefundCreationRequest request, System.Threading.CancellationToken cancellationToken = default)
            {
                if (request != null && _shouldSucceed(request.OrderId))
                {
                    return Task.FromResult(RefundCreationResult.CreateSuccess($"REFUND-{request.OrderId}"));
                }
                return Task.FromResult(RefundCreationResult.CreateFailure("Cổng mô phỏng hoàn tiền thất bại cho đơn này."));
            }
            public Task<EventTicketBooking.Api.DTOs.Payment.RefundExecutionResult> ExecuteRefundAsync(RefundExecutionRequest request, System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult(EventTicketBooking.Api.DTOs.Payment.RefundExecutionResult.CreateFailure("NOT_IMPLEMENTED", "Stub", isRetryable: false));
            public Task<RefundStatusDto?> QueryRefundStatusAsync(string? refundTransactionId, long orderCode, System.Threading.CancellationToken cancellationToken = default) =>
                Task.FromResult<RefundStatusDto?>(null);
        }

        #endregion
    }
}
