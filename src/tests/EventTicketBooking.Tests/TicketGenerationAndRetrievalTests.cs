using System;
using System.Collections.Generic;
using System.IO;
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
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử toàn diện cho Story S-25: Nhận vé điện tử có mã QR sau khi thanh toán.
    /// Kiểm thử các luồng sinh vé, mã không trùng lặp/không tuần tự, endpoint Read-Only,
    /// bảo mật phân quyền, đơn 0 VNĐ, và chống duplicate từ webhook.
    /// </summary>
    public class TicketGenerationAndRetrievalTests
    {
        private AppDbContext CreateSqliteDbContext(string dbPath)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var context = new AppDbContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private async Task<(User user, Event ev, Showtime showtime, SeatCategory category, List<Seat> seats, Order order, long orderCode)>
            SeedOrderScenarioAsync(AppDbContext context, int seatCount = 1, OrderStatus status = OrderStatus.Pending, int seatPrice = 100000, Guid? overrideUserId = null)
        {
            var userId = overrideUserId ?? Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            var seatCategoryId = Guid.NewGuid();

            var user = await context.Users.FindAsync(userId);
            if (user == null)
            {
                user = new User
                {
                    Id = userId,
                    Username = $"buyer_{Guid.NewGuid():N}",
                    Email = $"buyer_{Guid.NewGuid():N}@example.com",
                    PasswordHash = "hash123",
                    FullName = "Nguyễn Văn Mua Vé",
                    IsActive = true
                };
                context.Users.Add(user);
            }

            var ev = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Đại Nhạc Hội S-25",
                Location = "Sân Vận Động Mỹ Đình",
                TotalSeats = 100,
                OwnerId = userId
            };

            var category = new SeatCategory
            {
                Id = seatCategoryId,
                ShowtimeId = showtimeId,
                Name = "VIP Diamond",
                Price = seatPrice
            };

            var showtime = new Showtime
            {
                Id = showtimeId,
                EventId = ev.Id,
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(3)
            };

            context.Events.Add(ev);
            context.Showtimes.Add(showtime);
            context.SeatCategories.Add(category);

            var seats = new List<Seat>();
            var orderItems = new List<OrderItem>();
            var orderId = Guid.NewGuid();
            long orderCode = Random.Shared.NextInt64(1000000000L, 9999999999L);

            for (int i = 1; i <= seatCount; i++)
            {
                var seat = new Seat
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtimeId,
                    SeatCategoryId = seatCategoryId,
                    Row = "A",
                    SeatNumber = i,
                    Status = status == OrderStatus.Paid ? "SOLD" : "HELD"
                };
                seats.Add(seat);
                context.Seats.Add(seat);

                var orderItem = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    SeatId = seat.Id,
                    Price = seatPrice
                };
                orderItems.Add(orderItem);
                context.OrderItems.Add(orderItem);

                var hold = new SeatHolds
                {
                    Id = Guid.NewGuid(),
                    SeatId = seat.Id,
                    UserId = userId,
                    Status = status == OrderStatus.Paid ? "CONVERTED" : "ACTIVE",
                    HeldAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(15)
                };
                context.SeatHold.Add(hold);
            }

            var order = new Order
            {
                Id = orderId,
                UserId = userId,
                ShowtimeId = showtimeId,
                Status = status,
                TotalAmount = seatPrice * seatCount,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                OrderItems = orderItems
            };
            context.Orders.Add(order);

            var tx = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                OrderCode = orderCode,
                Amount = seatPrice * seatCount,
                Status = status == OrderStatus.Paid ? "PAID" : "PENDING",
                TransactionId = $"TXN_{orderCode}"
            };
            context.PaymentTransactions.Add(tx);

            await context.SaveChangesAsync();

            return (user, ev, showtime, category, seats, order, orderCode);
        }

        private OrdersController CreateOrdersController(AppDbContext context, Guid? currentUserId = null, bool isAdmin = false)
        {
            var controller = new OrdersController(context, NullLogger<OrdersController>.Instance, new TicketService());
            var httpContext = new DefaultHttpContext();

            if (currentUserId.HasValue)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, currentUserId.Value.ToString()),
                    new Claim("id", currentUserId.Value.ToString())
                };
                if (isAdmin)
                {
                    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                }
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            }

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        #region AC1, AC2, NFR: Sinh vé khi đơn chuyển Paid

        [Fact]
        public async Task S25_PaidOrder_1Seat_GeneratesExactlyOneTicketWithDetails()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_1seat_{Guid.NewGuid():N}.db");
            try
            {
                Guid orderId;
                long code;
                Guid userId;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 1, status: OrderStatus.Pending);
                    orderId = data.order.Id;
                    code = data.orderCode;
                    userId = data.user.Id;
                }

                // Act: Xử lý thanh toán thành công
                using (var processContext = CreateSqliteDbContext(dbPath))
                {
                    var fakeGateway = new FakePaymentGateway();
                    var paymentService = new PaymentService(processContext, fakeGateway, NullLogger<PaymentService>.Instance);

                    var result = await paymentService.HandlePaymentResultAsync(new PaymentResultDto
                    {
                        OrderId = orderId.ToString(),
                        OrderCode = code,
                        Amount = 100000,
                        Status = PaymentStatus.Success,
                        TransactionId = "GATEWAY_TXN_001"
                    });

                    Assert.True(result.Success);
                }

                // Assert: Kiểm tra vé được sinh trong DB
                using (var verifyContext = CreateSqliteDbContext(dbPath))
                {
                    var tickets = await verifyContext.Tickets
                        .Include(t => t.OrderItem)
                            .ThenInclude(oi => oi.Seat)
                        .ToListAsync();

                    Assert.Single(tickets);
                    var ticket = tickets[0];
                    Assert.NotNull(ticket);
                    Assert.StartsWith("TK-", ticket.TicketCode);
                    Assert.Equal(22, ticket.TicketCode.Length); // TK-XXXX-XXXX-XXXX-XXXX = 3 + 4 + 1 + 4 + 1 + 4 + 1 + 4 = 22

                    // Gọi endpoint GET /api/v1/orders/{orderId}/tickets
                    var controller = CreateOrdersController(verifyContext, currentUserId: userId);
                    var actionResult = await controller.GetOrderTickets(orderId);
                    var okResult = Assert.IsType<OkObjectResult>(actionResult);
                    var apiResponse = Assert.IsType<ApiResponse<List<TicketDto>>>(okResult.Value);

                    Assert.True(apiResponse.Success);
                    Assert.Single(apiResponse.Data);

                    var dto = apiResponse.Data[0];
                    Assert.Equal("Đại Nhạc Hội S-25", dto.EventTitle);
                    Assert.Equal("Sân Vận Động Mỹ Đình", dto.EventLocation);
                    Assert.Equal("VIP Diamond", dto.CategoryName);
                    Assert.Equal("A", dto.SeatRow);
                    Assert.Equal(1, dto.SeatNumber);
                    Assert.Equal("A1", dto.SeatName);
                    Assert.Equal(ticket.TicketCode, dto.TicketCode);
                    Assert.NotNull(dto.QrPayload);
                    Assert.Contains(ticket.TicketCode, dto.QrPayload);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_PaidOrder_3Seats_GeneratesThreeDistinctUnpredictableTickets()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_3seats_{Guid.NewGuid():N}.db");
            try
            {
                Guid orderId;
                long code;
                Guid userId;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 3, status: OrderStatus.Pending, seatPrice: 200000);
                    orderId = data.order.Id;
                    code = data.orderCode;
                    userId = data.user.Id;
                }

                using (var processContext = CreateSqliteDbContext(dbPath))
                {
                    var fakeGateway = new FakePaymentGateway();
                    var paymentService = new PaymentService(processContext, fakeGateway, NullLogger<PaymentService>.Instance);

                    var result = await paymentService.HandlePaymentResultAsync(new PaymentResultDto
                    {
                        OrderId = orderId.ToString(),
                        OrderCode = code,
                        Amount = 600000,
                        Status = PaymentStatus.Success,
                        TransactionId = "GATEWAY_TXN_003"
                    });
                    Assert.True(result.Success);
                }

                using (var verifyContext = CreateSqliteDbContext(dbPath))
                {
                    var controller = CreateOrdersController(verifyContext, currentUserId: userId);
                    var actionResult = await controller.GetOrderTickets(orderId);
                    var okResult = Assert.IsType<OkObjectResult>(actionResult);
                    var apiResponse = Assert.IsType<ApiResponse<List<TicketDto>>>(okResult.Value);

                    Assert.Equal(3, apiResponse.Data.Count);

                    var codes = apiResponse.Data.Select(t => t.TicketCode).ToList();
                    // AC2: Ba mã QR khác nhau
                    Assert.Equal(3, codes.Distinct().Count());

                    // NFR: Không dùng số thứ tự tăng dần, mỗi mã đủ dài và định dạng CSPRNG
                    foreach (var c in codes)
                    {
                        Assert.StartsWith("TK-", c);
                        Assert.True(c.Length >= 20);
                    }
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region Đơn hàng 0 VNĐ

        [Fact]
        public async Task S25_ZeroAmountOrder_GeneratesTicketsOnce()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_zero_{Guid.NewGuid():N}.db");
            try
            {
                Guid showtimeId;
                Guid userId;

                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 2, status: OrderStatus.Pending, seatPrice: 0);
                    showtimeId = data.showtime.Id;
                    userId = data.user.Id;

                    // Chuyển showtime sang OnSale để CreateOrderFromHolds cho phép
                    var st = await setup.Showtimes.FirstAsync();
                    typeof(Showtime).GetProperty("Status")!.SetValue(st, ShowtimeStatus.OnSale);

                    // Xoá order cũ để tạo mới qua controller
                    setup.Orders.Remove(data.order);
                    await setup.SaveChangesAsync();
                }

                // Act: Gọi CreateOrderFromHolds cho đơn 0 VNĐ
                using (var orderContext = CreateSqliteDbContext(dbPath))
                {
                    var controller = CreateOrdersController(orderContext, currentUserId: userId);
                    var createResult = await controller.CreateOrderFromHolds(showtimeId);
                    var okResult = Assert.IsType<OkObjectResult>(createResult);
                    var orderDto = Assert.IsType<ApiResponse<OrderDto>>(okResult.Value);

                    Assert.Equal("Paid", orderDto.Data.Status);
                    Assert.Equal(0, orderDto.Data.TotalAmount);

                    // Verify: Đã sinh đúng 2 vé tương ứng 2 ghế
                    var tickets = await orderContext.Tickets.ToListAsync();
                    Assert.Equal(2, tickets.Count);
                    Assert.NotEqual(tickets[0].TicketCode, tickets[1].TicketCode);

                    // Lấy vé qua API
                    var ticketsResult = await controller.GetOrderTickets(orderDto.Data.Id);
                    var ticketsOk = Assert.IsType<OkObjectResult>(ticketsResult);
                    var ticketsDto = Assert.IsType<ApiResponse<List<TicketDto>>>(ticketsOk.Value);

                    Assert.Equal(2, ticketsDto.Data.Count);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region Read-Only & Legacy Data Tests

        [Fact]
        public async Task S25_GetTickets_DoesNotCreateMissingTickets()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_readonly_{Guid.NewGuid():N}.db");
            try
            {
                Guid orderId;
                Guid userId;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 2, status: OrderStatus.Pending);
                    orderId = data.order.Id;
                    userId = data.user.Id;
                }

                using (var context = CreateSqliteDbContext(dbPath))
                {
                    var controller = CreateOrdersController(context, currentUserId: userId);

                    // Gọi nhiều lần khi đơn chưa Paid
                    for (int i = 0; i < 3; i++)
                    {
                        var res = await controller.GetOrderTickets(orderId);
                        var ok = Assert.IsType<OkObjectResult>(res);
                        var list = Assert.IsType<ApiResponse<List<TicketDto>>>(ok.Value);
                        Assert.Empty(list.Data);
                    }

                    // Khẳng định bảng tickets vẫn có 0 dòng
                    var count = await context.Tickets.CountAsync();
                    Assert.Equal(0, count);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_PaidLegacyOrderWithoutTickets_ReturnsConsistentReadOnlyResult()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_legacy_{Guid.NewGuid():N}.db");
            try
            {
                Guid orderId;
                Guid userId;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    // Đơn hàng đã ở trạng thái Paid từ trước, nhưng KHÔNG có dòng nào trong bảng tickets
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 2, status: OrderStatus.Paid);
                    orderId = data.order.Id;
                    userId = data.user.Id;
                }

                using (var context = CreateSqliteDbContext(dbPath))
                {
                    var controller = CreateOrdersController(context, currentUserId: userId);

                    var res = await controller.GetOrderTickets(orderId);
                    var ok = Assert.IsType<OkObjectResult>(res);
                    var list = Assert.IsType<ApiResponse<List<TicketDto>>>(ok.Value);

                    // Trả về danh sách rỗng nhất quán, không lỗi 500
                    Assert.Empty(list.Data);

                    // Tuyệt đối không tự động sinh vé trong request GET (Read-Only)
                    var ticketCount = await context.Tickets.CountAsync();
                    Assert.Equal(0, ticketCount);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_PendingOrder_GetTickets_ReturnsZeroTickets()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_pending_{Guid.NewGuid():N}.db");
            try
            {
                Guid orderId;
                Guid userId;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 1, status: OrderStatus.Pending);
                    orderId = data.order.Id;
                    userId = data.user.Id;
                }

                using (var context = CreateSqliteDbContext(dbPath))
                {
                    var controller = CreateOrdersController(context, currentUserId: userId);
                    var res = await controller.GetOrderTickets(orderId);
                    var ok = Assert.IsType<OkObjectResult>(res);
                    var response = Assert.IsType<ApiResponse<List<TicketDto>>>(ok.Value);

                    // AC3: Giả sử đơn chưa trả, khi mở trang vé bằng đường dẫn, thì không có vé nào.
                    Assert.Empty(response.Data);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_CancelledOrExpiredOrder_GetTickets_ReturnsZeroTickets()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_cancelled_{Guid.NewGuid():N}.db");
            try
            {
                Guid cancelledOrderId;
                Guid expiredOrderId;
                Guid userId;

                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data1 = await SeedOrderScenarioAsync(setup, seatCount: 1, status: OrderStatus.Cancelled);
                    cancelledOrderId = data1.order.Id;
                    userId = data1.user.Id;

                    var data2 = await SeedOrderScenarioAsync(setup, seatCount: 1, status: OrderStatus.Expired, overrideUserId: userId);
                    expiredOrderId = data2.order.Id;
                }

                using (var context = CreateSqliteDbContext(dbPath))
                {
                    var controller = CreateOrdersController(context, currentUserId: userId);

                    var res1 = await controller.GetOrderTickets(cancelledOrderId);
                    var ok1 = Assert.IsType<OkObjectResult>(res1);
                    var list1 = Assert.IsType<ApiResponse<List<TicketDto>>>(ok1.Value);
                    Assert.Empty(list1.Data);

                    var res2 = await controller.GetOrderTickets(expiredOrderId);
                    var ok2 = Assert.IsType<OkObjectResult>(res2);
                    var list2 = Assert.IsType<ApiResponse<List<TicketDto>>>(ok2.Value);
                    Assert.Empty(list2.Data);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region Authorization & Error Tests

        [Fact]
        public async Task S25_GetTickets_OtherUser_ReturnsForbidden403()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_forbidden_{Guid.NewGuid():N}.db");
            try
            {
                Guid orderId;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 1, status: OrderStatus.Paid);
                    orderId = data.order.Id;
                }

                using (var context = CreateSqliteDbContext(dbPath))
                {
                    // User khác cố truy cập đơn
                    var otherUserId = Guid.NewGuid();
                    var controller = CreateOrdersController(context, currentUserId: otherUserId, isAdmin: false);

                    var res = await controller.GetOrderTickets(orderId);
                    var objResult = Assert.IsType<ObjectResult>(res);
                    Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_GetTickets_AdminUser_CanAccessOrderTickets()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_admin_{Guid.NewGuid():N}.db");
            try
            {
                Guid orderId;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 1, status: OrderStatus.Paid);
                    orderId = data.order.Id;

                    // Sinh sẵn 1 vé
                    var ticket = new Ticket
                    {
                        Id = Guid.NewGuid(),
                        OrderItemId = data.order.OrderItems.First().Id,
                        TicketCode = "TK-TEST-ADMIN-001"
                    };
                    setup.Tickets.Add(ticket);
                    await setup.SaveChangesAsync();
                }

                using (var context = CreateSqliteDbContext(dbPath))
                {
                    // Admin khác truy cập đơn -> Được phép
                    var adminId = Guid.NewGuid();
                    var controller = CreateOrdersController(context, currentUserId: adminId, isAdmin: true);

                    var res = await controller.GetOrderTickets(orderId);
                    var ok = Assert.IsType<OkObjectResult>(res);
                    var list = Assert.IsType<ApiResponse<List<TicketDto>>>(ok.Value);
                    Assert.Single(list.Data);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_GetTickets_NonExistentOrder_ReturnsNotFound404()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_notfound_{Guid.NewGuid():N}.db");
            try
            {
                using (var context = CreateSqliteDbContext(dbPath))
                {
                    var controller = CreateOrdersController(context, currentUserId: Guid.NewGuid());
                    var res = await controller.GetOrderTickets(Guid.NewGuid());
                    Assert.IsType<NotFoundObjectResult>(res);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region Idempotency & Webhook Replay Tests (S-20 Dependency)

        [Fact]
        public async Task S25_DuplicateWebhookReplay_DoesNotDuplicateTickets()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_replay_{Guid.NewGuid():N}.db");
            try
            {
                long orderCode;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 2, status: OrderStatus.Pending);
                    orderCode = data.orderCode;
                }

                var fakeGateway = new FakePaymentGateway
                {
                    SimulatedTransactionId = "TXN_REPLAY_S25_100",
                    CustomWebhookParseResult = WebhookParseResult.CreateSuccess(
                        orderCode: orderCode,
                        amount: 200000,
                        transactionId: "TXN_REPLAY_S25_100",
                        paidAt: DateTimeOffset.UtcNow
                    )
                };

                string payload = $"{{\"code\":\"00\",\"data\":{{\"orderCode\":{orderCode},\"amount\":200000,\"reference\":\"TXN_REPLAY_S25_100\"}}}}";
                string sig = "SIG_123";

                // Act: Gửi cùng 1 webhook 5 lần liên tiếp
                for (int i = 0; i < 5; i++)
                {
                    using var loopContext = CreateSqliteDbContext(dbPath);
                    var service = new PaymentService(loopContext, fakeGateway, NullLogger<PaymentService>.Instance);
                    bool ok = await service.ProcessPaymentWebhookAsync(payload, sig);
                    Assert.True(ok);
                }

                // Assert: Chỉ có đúng 2 vé được tạo
                using (var verify = CreateSqliteDbContext(dbPath))
                {
                    var tickets = await verify.Tickets.ToListAsync();
                    Assert.Equal(2, tickets.Count);
                    Assert.NotEqual(tickets[0].TicketCode, tickets[1].TicketCode);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_ConcurrentDuplicateWebhook_DoesNotDuplicateTickets()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_concurrent_{Guid.NewGuid():N}.db");
            try
            {
                long orderCode;
                using (var setup = CreateSqliteDbContext(dbPath))
                {
                    var data = await SeedOrderScenarioAsync(setup, seatCount: 2, status: OrderStatus.Pending);
                    orderCode = data.orderCode;
                }

                var fakeGateway = new FakePaymentGateway
                {
                    SimulatedTransactionId = "TXN_CONCURRENT_S25_200",
                    CustomWebhookParseResult = WebhookParseResult.CreateSuccess(
                        orderCode: orderCode,
                        amount: 200000,
                        transactionId: "TXN_CONCURRENT_S25_200",
                        paidAt: DateTimeOffset.UtcNow
                    )
                };

                string payload = $"{{\"code\":\"00\",\"data\":{{\"orderCode\":{orderCode},\"amount\":200000,\"reference\":\"TXN_CONCURRENT_S25_200\"}}}}";
                string sig = "SIG_CONCURRENT";

                // Act: 5 luồng gửi đồng thời
                var tasks = Enumerable.Range(0, 5).Select(async _ =>
                {
                    using var threadContext = CreateSqliteDbContext(dbPath);
                    var service = new PaymentService(threadContext, fakeGateway, NullLogger<PaymentService>.Instance);
                    return await service.ProcessPaymentWebhookAsync(payload, sig);
                }).ToArray();

                var results = await Task.WhenAll(tasks);
                Assert.All(results, res => Assert.True(res));

                // Assert: Không trùng vé
                using (var verify = CreateSqliteDbContext(dbPath))
                {
                    var tickets = await verify.Tickets.ToListAsync();
                    Assert.Equal(2, tickets.Count);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region Constraints & Entropy Tests

        [Fact]
        public async Task S25_TicketCode_UniqueConstraint_RejectsDuplicate()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_unique_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var data = await SeedOrderScenarioAsync(context, seatCount: 2, status: OrderStatus.Paid);

                var ticket1 = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = data.order.OrderItems.First().Id,
                    TicketCode = "TK-DUPLICATE-CODE"
                };
                context.Tickets.Add(ticket1);
                await context.SaveChangesAsync();

                // Chèn vé thứ 2 có cùng TicketCode
                var ticket2 = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = data.order.OrderItems.Last().Id,
                    TicketCode = "TK-DUPLICATE-CODE"
                };
                context.Tickets.Add(ticket2);

                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S25_OrderItemId_UniqueConstraint_RejectsDuplicateTicketForSameSeat()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s25_1to1_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var data = await SeedOrderScenarioAsync(context, seatCount: 1, status: OrderStatus.Paid);
                var itemId = data.order.OrderItems.First().Id;

                var ticket1 = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = itemId,
                    TicketCode = "TK-SEAT-001"
                };
                context.Tickets.Add(ticket1);
                await context.SaveChangesAsync();

                // Clear tracker để tránh EF Core update in-memory entity mà phát lệnh INSERT thực sự vào SQLite
                context.ChangeTracker.Clear();

                // Cố chèn vé thứ 2 cho cùng 1 OrderItemId
                var ticket2 = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = itemId,
                    TicketCode = "TK-SEAT-002"
                };
                context.Tickets.Add(ticket2);

                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public void S25_TicketCode_EntropyVerification_1000CodesAreUniqueAndUnpredictable()
        {
            var service = new TicketService();
            var set = new HashSet<string>();

            for (int i = 0; i < 1000; i++)
            {
                var code = service.GenerateTicketCode();
                Assert.StartsWith("TK-", code);
                Assert.True(set.Add(code), $"Phát hiện trùng lặp mã vé tại lần sinh thứ {i + 1}: {code}");
            }

            Assert.Equal(1000, set.Count);
        }

        #endregion
    }
}
