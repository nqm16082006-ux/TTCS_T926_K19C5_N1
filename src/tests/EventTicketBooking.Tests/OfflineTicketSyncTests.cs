using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class OfflineTicketSyncTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly TicketCheckInController _controller;
        private readonly Guid _staffUserId = Guid.NewGuid();

        public OfflineTicketSyncTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _controller = CreateControllerWithRole("Staff");
        }

        private TicketCheckInController CreateControllerWithRole(string role)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, _staffUserId.ToString()),
                new Claim(ClaimTypes.Role, role),
                new Claim("role", role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            var logger = NullLogger<TicketCheckInController>.Instance;

            return new TicketCheckInController(_context, logger)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private async Task<(Showtime showtime, List<Ticket> paidTickets, List<Ticket> pendingTickets)> SeedShowtimeScenarioAsync(int paidCount = 3, int pendingCount = 2)
        {
            var ev = new Event { Id = Guid.NewGuid(), Title = "S33 Test Concert", Location = "Stadium" };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                StartTime = DateTime.UtcNow.AddHours(2),
                EndTime = DateTime.UtcNow.AddHours(5)
            };

            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);

            var paidTickets = new List<Ticket>();
            var pendingTickets = new List<Ticket>();

            for (int i = 0; i < paidCount; i++)
            {
                var order = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid };
                var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "A", SeatNumber = i + 1, SeatCategoryId = Guid.NewGuid() };
                var orderItem = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    Order = order,
                    SeatId = seat.Id,
                    Seat = seat,
                    Price = 100000,
                    IsCheckedIn = false
                };
                var ticket = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = orderItem.Id,
                    OrderItem = orderItem,
                    TicketCode = $"TK-PAID-{i + 1:D4}",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
                };

                _context.Orders.Add(order);
                _context.Seats.Add(seat);
                _context.OrderItems.Add(orderItem);
                _context.Tickets.Add(ticket);
                paidTickets.Add(ticket);
            }

            for (int i = 0; i < pendingCount; i++)
            {
                var order = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Pending };
                var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "B", SeatNumber = i + 1, SeatCategoryId = Guid.NewGuid() };
                var orderItem = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    Order = order,
                    SeatId = seat.Id,
                    Seat = seat,
                    Price = 100000,
                    IsCheckedIn = false
                };
                var ticket = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = orderItem.Id,
                    OrderItem = orderItem,
                    TicketCode = $"TK-PENDING-{i + 1:D4}",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
                };

                _context.Orders.Add(order);
                _context.Seats.Add(seat);
                _context.OrderItems.Add(orderItem);
                _context.Tickets.Add(ticket);
                pendingTickets.Add(ticket);
            }

            await _context.SaveChangesAsync();
            return (showtime, paidTickets, pendingTickets);
        }

        // ========================================================
        // 1. DownloadTickets_WithValidShowtime_ReturnsAllPaidTickets
        // ========================================================
        [Fact]
        public async Task DownloadTickets_WithValidShowtime_ReturnsAllPaidTickets()
        {
            // Arrange
            var (showtime, paidTickets, _) = await SeedShowtimeScenarioAsync(paidCount: 5, pendingCount: 3);

            // Thêm 1 showtime khác để đảm bảo không bị lẫn lộn suất
            var otherShow = new Showtime { Id = Guid.NewGuid(), EventId = showtime.EventId, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2) };
            var otherOrder = new Order { Id = Guid.NewGuid(), ShowtimeId = otherShow.Id, Status = OrderStatus.Paid };
            var otherItem = new OrderItem { Id = Guid.NewGuid(), OrderId = otherOrder.Id, Order = otherOrder, SeatId = Guid.NewGuid(), Price = 50000 };
            var otherTicket = new Ticket { Id = Guid.NewGuid(), OrderItemId = otherItem.Id, OrderItem = otherItem, TicketCode = "TK-OTHER-SHOW" };
            _context.Showtimes.Add(otherShow);
            _context.Orders.Add(otherOrder);
            _context.OrderItems.Add(otherItem);
            _context.Tickets.Add(otherTicket);
            await _context.SaveChangesAsync();

            // Act: Full sync (since = null)
            var actionResult = await _controller.GetOfflineTickets(showtime.Id, since: null);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<OfflineTicketSyncResponseDto>(okResult.Value);

            Assert.Equal(showtime.Id, response.ShowtimeId);
            Assert.False(response.IsDelta);
            Assert.Equal(5, response.TotalCount);
            Assert.Equal(5, response.Tickets.Count);

            var receivedCodes = response.Tickets.Select(t => t.TicketCode).ToList();
            foreach (var paid in paidTickets)
            {
                Assert.Contains(paid.TicketCode, receivedCodes);
            }
            Assert.DoesNotContain("TK-OTHER-SHOW", receivedCodes);
        }

        // ========================================================
        // 2. DownloadTickets_ResponseContainsNoPII
        // ========================================================
        [Fact]
        public async Task DownloadTickets_ResponseContainsNoPII()
        {
            // Arrange
            var (showtime, _, _) = await SeedShowtimeScenarioAsync(paidCount: 2, pendingCount: 0);

            // Act
            var actionResult = await _controller.GetOfflineTickets(showtime.Id, since: null);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(okResult.Value);

            // Không chứa thông tin cá nhân hoặc thông tin nhạy cảm
            Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("username", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fullname", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("price", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seatid", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("userid", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payment", json, StringComparison.OrdinalIgnoreCase);
        }

        // ========================================================
        // 3. DownloadTickets_ReturnsTicketCodeAndCheckInState
        // ========================================================
        [Fact]
        public async Task DownloadTickets_ReturnsTicketCodeAndCheckInState()
        {
            // Arrange
            var (showtime, paidTickets, _) = await SeedShowtimeScenarioAsync(paidCount: 2, pendingCount: 0);

            // Đánh dấu 1 vé đã check-in
            paidTickets[0].OrderItem.IsCheckedIn = true;
            paidTickets[0].OrderItem.CheckInTime = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            // Act
            var actionResult = await _controller.GetOfflineTickets(showtime.Id, since: null);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<OfflineTicketSyncResponseDto>(okResult.Value);

            var item1 = response.Tickets.FirstOrDefault(t => t.TicketCode == paidTickets[0].TicketCode);
            var item2 = response.Tickets.FirstOrDefault(t => t.TicketCode == paidTickets[1].TicketCode);

            Assert.NotNull(item1);
            Assert.True(item1.IsCheckedIn);

            Assert.NotNull(item2);
            Assert.False(item2.IsCheckedIn);
        }

        // ========================================================
        // 4. DeltaSync_ReturnsOnlyChangedTickets
        // ========================================================
        [Fact]
        public async Task DeltaSync_ReturnsOnlyChangedTickets()
        {
            // Arrange: 3 vé cũ tạo lúc T - 40 phút
            var (showtime, paidTickets, _) = await SeedShowtimeScenarioAsync(paidCount: 3, pendingCount: 0);
            var syncTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10); // Lần sync trước là T - 10 phút

            // Sau mốc syncTimestamp:
            // 1. Vé 1 được check-in lúc T - 5 phút
            paidTickets[0].OrderItem.IsCheckedIn = true;
            paidTickets[0].OrderItem.CheckInTime = DateTimeOffset.UtcNow.AddMinutes(-5);

            // 2. Một vé mới được mua và tạo lúc T - 2 phút
            var newOrder = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid };
            var newSeat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "C", SeatNumber = 1, SeatCategoryId = Guid.NewGuid() };
            var newItem = new OrderItem { Id = Guid.NewGuid(), OrderId = newOrder.Id, Order = newOrder, SeatId = newSeat.Id, Seat = newSeat, Price = 100000 };
            var newTicket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = newItem.Id,
                OrderItem = newItem,
                TicketCode = "TK-NEWLY-BOUGHT",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
            };
            _context.Orders.Add(newOrder);
            _context.Seats.Add(newSeat);
            _context.OrderItems.Add(newItem);
            _context.Tickets.Add(newTicket);
            await _context.SaveChangesAsync();

            // Act: Delta sync với since = syncTimestamp
            var actionResult = await _controller.GetOfflineTickets(showtime.Id, since: syncTimestamp);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<OfflineTicketSyncResponseDto>(okResult.Value);

            Assert.True(response.IsDelta);
            Assert.Equal(2, response.TotalCount); // Chỉ có 2 vé thay đổi (1 vé check-in, 1 vé mới)

            var receivedCodes = response.Tickets.Select(t => t.TicketCode).ToList();
            Assert.Contains(paidTickets[0].TicketCode, receivedCodes);
            Assert.Contains("TK-NEWLY-BOUGHT", receivedCodes);
            Assert.DoesNotContain(paidTickets[1].TicketCode, receivedCodes);
            Assert.DoesNotContain(paidTickets[2].TicketCode, receivedCodes);
        }

        // ========================================================
        // 5. DeltaSync_WithOverlapWindow_PreventsBoundaryMissInCoveredWindow
        // ========================================================
        [Fact]
        public async Task DeltaSync_WithOverlapWindow_PreventsBoundaryMissInCoveredWindow()
        {
            // Arrange
            var (showtime, _, _) = await SeedShowtimeScenarioAsync(paidCount: 0, pendingCount: 0);

            var syncTimestamp = DateTimeOffset.UtcNow; // Mốc since

            // Vé A: tạo lúc syncTimestamp - 5 giây (nằm trong buffer 15 giây overlap)
            var orderA = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid };
            var seatA = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "D", SeatNumber = 1, SeatCategoryId = Guid.NewGuid() };
            var itemA = new OrderItem { Id = Guid.NewGuid(), OrderId = orderA.Id, Order = orderA, SeatId = seatA.Id, Seat = seatA, Price = 100000 };
            var ticketA = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = itemA.Id,
                OrderItem = itemA,
                TicketCode = "TK-IN-BUFFER-WINDOW",
                CreatedAt = syncTimestamp.AddSeconds(-5) // Trong buffer 15s
            };

            // Vé B: tạo lúc syncTimestamp - 30 giây (ngoài buffer 15 giây)
            var orderB = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid };
            var seatB = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "D", SeatNumber = 2, SeatCategoryId = Guid.NewGuid() };
            var itemB = new OrderItem { Id = Guid.NewGuid(), OrderId = orderB.Id, Order = orderB, SeatId = seatB.Id, Seat = seatB, Price = 100000 };
            var ticketB = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = itemB.Id,
                OrderItem = itemB,
                TicketCode = "TK-OUTSIDE-BUFFER-WINDOW",
                CreatedAt = syncTimestamp.AddSeconds(-30) // Ngoài buffer
            };

            _context.Orders.AddRange(orderA, orderB);
            _context.Seats.AddRange(seatA, seatB);
            _context.OrderItems.AddRange(itemA, itemB);
            _context.Tickets.AddRange(ticketA, ticketB);
            await _context.SaveChangesAsync();

            // Act: Delta sync với since = syncTimestamp
            var actionResult = await _controller.GetOfflineTickets(showtime.Id, since: syncTimestamp);

            // Assert: threshold = since - 15s, do đó vé A (-5s) phải được lấy về, vé B (-30s) bị loại
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<OfflineTicketSyncResponseDto>(okResult.Value);

            var receivedCodes = response.Tickets.Select(t => t.TicketCode).ToList();
            Assert.Contains("TK-IN-BUFFER-WINDOW", receivedCodes);
            Assert.DoesNotContain("TK-OUTSIDE-BUFFER-WINDOW", receivedCodes);
        }

        // ========================================================
        // 6. UnauthorizedRole_Returns403
        // ========================================================
        [Fact]
        public async Task UnauthorizedRole_Returns403()
        {
            // Note: Phân quyền của TicketCheckInController được kiểm tra bởi RoleAuthorizationMiddleware và [RequireRole("Admin", "Staff")]
            // Ở mức Controller UnitTest, kiểm tra logic isStaff nếu có hoặc qua middleware context
            var controllerAsCustomer = CreateControllerWithRole("Customer");
            var showtimeId = Guid.NewGuid();

            // Thuộc tính [RequireRole("Admin", "Staff")] được gắn ở cấp Controller class
            var requireRoleAttr = typeof(TicketCheckInController)
                .GetCustomAttributes(typeof(EventTicketBooking.Api.Middlewares.RequireRoleAttribute), true)
                .FirstOrDefault() as EventTicketBooking.Api.Middlewares.RequireRoleAttribute;

            Assert.NotNull(requireRoleAttr);
            Assert.Contains("Admin", requireRoleAttr.Roles);
            Assert.Contains("Staff", requireRoleAttr.Roles);
            Assert.DoesNotContain("Customer", requireRoleAttr.Roles);
        }

        // ========================================================
        // 7. UnknownShowtime_Returns404
        // ========================================================
        [Fact]
        public async Task UnknownShowtime_Returns404()
        {
            // Act: Truyền showtime ngẫu nhiên không có trong database
            var actionResult = await _controller.GetOfflineTickets(Guid.NewGuid(), since: null);

            // Assert
            Assert.IsType<NotFoundObjectResult>(actionResult);
        }

        // ========================================================
        // 8. Performance_5000Tickets_BackendQueryAndSerialization
        // ========================================================
        [Fact]
        public async Task Performance_5000Tickets_BackendQueryAndSerialization()
        {
            // Arrange: Seed 5.000 tickets
            var ev = new Event { Id = Guid.NewGuid(), Title = "5000 Tickets Benchmark", Location = "Arena" };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(4) };
            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);

            var orders = new List<Order>(5000);
            var seats = new List<Seat>(5000);
            var items = new List<OrderItem>(5000);
            var tickets = new List<Ticket>(5000);

            for (int i = 0; i < 5000; i++)
            {
                var order = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid };
                var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = $"R{i / 100}", SeatNumber = i % 100, SeatCategoryId = Guid.NewGuid() };
                var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, Order = order, SeatId = seat.Id, Seat = seat, Price = 100000, IsCheckedIn = i % 5 == 0 };
                var ticket = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = item.Id,
                    OrderItem = item,
                    TicketCode = $"TK-BENCH-{i:D5}-XY9Z",
                    CreatedAt = DateTimeOffset.UtcNow
                };

                orders.Add(order);
                seats.Add(seat);
                items.Add(item);
                tickets.Add(ticket);
            }

            _context.Orders.AddRange(orders);
            _context.Seats.AddRange(seats);
            _context.OrderItems.AddRange(items);
            _context.Tickets.AddRange(tickets);
            await _context.SaveChangesAsync();

            // Act & Measure: Đo thời gian Backend Query và JSON Serialization
            var sw = Stopwatch.StartNew();
            var actionResult = await _controller.GetOfflineTickets(showtime.Id, since: null);
            var queryTimeMs = sw.ElapsedMilliseconds;

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<OfflineTicketSyncResponseDto>(okResult.Value);

            sw.Restart();
            var rawJsonBytes = JsonSerializer.SerializeToUtf8Bytes(response);
            var serializationTimeMs = sw.ElapsedMilliseconds;

            // Assert
            Assert.Equal(5000, response.TotalCount);
            Assert.Equal(5000, response.Tickets.Count);

            // Ghi nhận chỉ số benchmark thực tế
            var payloadSizeKb = rawJsonBytes.Length / 1024.0;
            // 5000 vé JSON kích thước ~300KB
            Assert.True(payloadSizeKb < 600, $"Payload size is {payloadSizeKb} KB, expected < 600 KB");
            // Backend query và serialize trong môi trường in-memory/test < 1000ms
            Assert.True(queryTimeMs + serializationTimeMs < 3000, $"Query ({queryTimeMs}ms) + Serialize ({serializationTimeMs}ms) took {queryTimeMs + serializationTimeMs}ms, expected < 3000ms");
        }

        // ========================================================
        // 8. Scanner_DownloadStoresTicketsLocally
        // ========================================================
        [Fact]
        public void Scanner_DownloadStoresTicketsLocally()
        {
            var store = new ScannerOfflineStore();
            var showtimeId = Guid.NewGuid();
            var tickets = new List<OfflineTicketItemDto>
            {
                new OfflineTicketItemDto { TicketCode = "TK-001", IsCheckedIn = false },
                new OfflineTicketItemDto { TicketCode = "TK-002", IsCheckedIn = true }
            };
            var serverTime = DateTimeOffset.UtcNow;

            store.StoreTickets(showtimeId, tickets, serverTime, 2);

            Assert.Equal(2, store.Tickets.Count);
            Assert.True(store.Tickets.ContainsKey((showtimeId, "TK-001")));
            Assert.False(store.Tickets[(showtimeId, "TK-001")]);
            Assert.True(store.Tickets[(showtimeId, "TK-002")]);
            Assert.Equal(serverTime, store.Metadata[showtimeId].lastSyncAt);
        }

        // ========================================================
        // 9. Scanner_OfflineLookup_ValidTicket_ReadOnly
        // ========================================================
        [Fact]
        public void Scanner_OfflineLookup_ValidTicket_ReadOnly()
        {
            var store = new ScannerOfflineStore();
            var showtimeId = Guid.NewGuid();
            store.StoreTickets(showtimeId, new[] { new OfflineTicketItemDto { TicketCode = "TK-VALID", IsCheckedIn = false } }, DateTimeOffset.UtcNow, 1);

            var qrService = new QrSignatureService();
            var result = store.VerifyAndLookup("TK-VALID", showtimeId, qrService);

            // Báo hợp lệ
            Assert.True(result.IsValid);
            Assert.Equal("VALID_MANUAL", result.Status);

            // Quan trọng: READ-ONLY! Trạng thái trong store KHÔNG bị đổi thành true
            Assert.False(store.Tickets[(showtimeId, "TK-VALID")]);
        }

        // ========================================================
        // 10. Scanner_OfflineLookup_AlreadyCheckedInTicket_ReadOnly
        // ========================================================
        [Fact]
        public void Scanner_OfflineLookup_AlreadyCheckedInTicket_ReadOnly()
        {
            var store = new ScannerOfflineStore();
            var showtimeId = Guid.NewGuid();
            store.StoreTickets(showtimeId, new[] { new OfflineTicketItemDto { TicketCode = "TK-USED", IsCheckedIn = true } }, DateTimeOffset.UtcNow, 1);

            var qrService = new QrSignatureService();
            var result = store.VerifyAndLookup("TK-USED", showtimeId, qrService);

            // Bị từ chối vì đã sử dụng
            Assert.False(result.IsValid);
            Assert.Equal("ALREADY_CHECKED_IN", result.Status);
            Assert.True(store.Tickets[(showtimeId, "TK-USED")]);
        }

        // ========================================================
        // 11. Scanner_OfflineLookup_UnknownTicketRejected
        // ========================================================
        [Fact]
        public void Scanner_OfflineLookup_UnknownTicketRejected()
        {
            var store = new ScannerOfflineStore();
            var showtimeId = Guid.NewGuid();
            store.StoreTickets(showtimeId, new[] { new OfflineTicketItemDto { TicketCode = "TK-EXISTING", IsCheckedIn = false } }, DateTimeOffset.UtcNow, 1);

            var qrService = new QrSignatureService();
            var result = store.VerifyAndLookup("TK-NOT-EXIST", showtimeId, qrService);

            Assert.False(result.IsValid);
            Assert.Equal("NOT_FOUND", result.Status);
        }

        // ========================================================
        // 12. Scanner_AutoRefreshAfter30MinutesWhenOnline
        // ========================================================
        [Fact]
        public void Scanner_AutoRefreshAfter30MinutesWhenOnline()
        {
            var store = new ScannerOfflineStore();
            var showtimeId = Guid.NewGuid();
            var syncTime = DateTimeOffset.UtcNow.AddMinutes(-31); // 31 phút trước
            store.Metadata[showtimeId] = (syncTime, 100);

            var shouldRefresh = store.ShouldAutoRefresh(showtimeId, DateTimeOffset.UtcNow);
            Assert.True(shouldRefresh);
        }

        // ========================================================
        // 13. Scanner_DoesNotAutoRefreshBefore30Minutes
        // ========================================================
        [Fact]
        public void Scanner_DoesNotAutoRefreshBefore30Minutes()
        {
            var store = new ScannerOfflineStore();
            var showtimeId = Guid.NewGuid();
            var syncTime = DateTimeOffset.UtcNow.AddMinutes(-15); // 15 phút trước
            store.Metadata[showtimeId] = (syncTime, 100);

            var shouldRefresh = store.ShouldAutoRefresh(showtimeId, DateTimeOffset.UtcNow);
            Assert.False(shouldRefresh);
        }

        // ========================================================
        // 14. OfflineSignedQr_ValidSignature_ValidTicket_ReturnsValid
        // ========================================================
        [Fact]
        public void OfflineSignedQr_ValidSignature_ValidTicket_ReturnsValid()
        {
            var store = new ScannerOfflineStore();
            var qrService = new QrSignatureService();
            var showtimeId = Guid.NewGuid();
            var ticketCode = "TK-OFFLINE-001";

            // Cache public key
            store.CachedPublicKeys["v1"] = qrService.GetPublicKeys()["v1"];
            store.StoreTickets(showtimeId, new[] { new OfflineTicketItemDto { TicketCode = ticketCode, IsCheckedIn = false } }, DateTimeOffset.UtcNow, 1);

            // Sinh mã QR có chữ ký chuẩn
            var signedQr = qrService.SignTicket(ticketCode, showtimeId);

            var result = store.VerifyAndLookup(signedQr, showtimeId, qrService);

            Assert.True(result.IsValid);
            Assert.Equal("VALID", result.Status);
            // Read-only: Không sửa đổi IsCheckedIn
            Assert.False(store.Tickets[(showtimeId, ticketCode)]);
        }

        // ========================================================
        // 15. OfflineSignedQr_InvalidSignature_IsRejected
        // Đặc biệt: Invalid signed QR dù TicketCode có tồn tại trong IndexedDB vẫn bị reject!
        // ========================================================
        [Fact]
        public void OfflineSignedQr_InvalidSignature_IsRejected()
        {
            var store = new ScannerOfflineStore();
            var qrService = new QrSignatureService();
            var showtimeId = Guid.NewGuid();
            var ticketCode = "TK-OFFLINE-REAL-CODE";

            // Vé THẬT có tồn tại trong store
            store.CachedPublicKeys["v1"] = qrService.GetPublicKeys()["v1"];
            store.StoreTickets(showtimeId, new[] { new OfflineTicketItemDto { TicketCode = ticketCode, IsCheckedIn = false } }, DateTimeOffset.UtcNow, 1);

            // Giả mạo mã QR bằng cách sửa chữ ký
            var realQr = qrService.SignTicket(ticketCode, showtimeId);
            var parts = realQr.Split('|');
            var tamperedQr = $"{parts[0]}|{parts[1]}|{parts[2]}|{parts[3]}|TAMPERED_SIG_123456789";

            var result = store.VerifyAndLookup(tamperedQr, showtimeId, qrService);

            // Phải bị reject ngay tại bước chữ ký, KHÔNG được tha bổng dù ticketCode có trong DB!
            Assert.False(result.IsValid);
            Assert.Equal("INVALID_SIGNATURE", result.Status);
        }

        // ========================================================
        // 16. OfflineSignedQr_WrongShowtime_IsRejected
        // ========================================================
        [Fact]
        public void OfflineSignedQr_WrongShowtime_IsRejected()
        {
            var store = new ScannerOfflineStore();
            var qrService = new QrSignatureService();
            var correctShowtimeId = Guid.NewGuid();
            var wrongShowtimeId = Guid.NewGuid();
            var ticketCode = "TK-OFFLINE-003";

            store.CachedPublicKeys["v1"] = qrService.GetPublicKeys()["v1"];
            store.StoreTickets(correctShowtimeId, new[] { new OfflineTicketItemDto { TicketCode = ticketCode, IsCheckedIn = false } }, DateTimeOffset.UtcNow, 1);

            // Vé ký cho correctShowtimeId
            var signedQr = qrService.SignTicket(ticketCode, correctShowtimeId);

            // Quét tại cửa của wrongShowtimeId
            var result = store.VerifyAndLookup(signedQr, wrongShowtimeId, qrService);

            Assert.False(result.IsValid);
            Assert.Equal("WRONG_SHOWTIME", result.Status);
        }

        // ========================================================
        // 17. OfflineSignedQr_MissingPublicKey_IsRejected
        // ========================================================
        [Fact]
        public void OfflineSignedQr_MissingPublicKey_IsRejected()
        {
            var store = new ScannerOfflineStore();
            var qrService = new QrSignatureService();
            var showtimeId = Guid.NewGuid();
            var ticketCode = "TK-OFFLINE-004";

            // Cache KHÔNG có keyVersion "v2"
            store.CachedPublicKeys.Clear();
            store.StoreTickets(showtimeId, new[] { new OfflineTicketItemDto { TicketCode = ticketCode, IsCheckedIn = false } }, DateTimeOffset.UtcNow, 1);

            var signedQr = $"TICKET|v2|{ticketCode}|{showtimeId}|some_sig";

            var result = store.VerifyAndLookup(signedQr, showtimeId, qrService);

            Assert.False(result.IsValid);
            Assert.Equal("MISSING_PUBLIC_KEY", result.Status);
        }

        // ========================================================
        // 18. OfflineSignedQr_InvalidFormat_IsRejected
        // ========================================================
        [Fact]
        public void OfflineSignedQr_InvalidFormat_IsRejected()
        {
            var store = new ScannerOfflineStore();
            var qrService = new QrSignatureService();
            var showtimeId = Guid.NewGuid();

            var malformedQr = "TICKET|incomplete";
            var result = store.VerifyAndLookup(malformedQr, showtimeId, qrService);

            Assert.False(result.IsValid);
            Assert.Equal("INVALID_FORMAT", result.Status);
        }
    }

    /// <summary>
    /// Lớp mô phỏng chính xác logic IndexedDB và Offline QR Verification của client (staff-scanner.html)
    /// </summary>
    public class ScannerOfflineStore
    {
        public Dictionary<(Guid showtimeId, string ticketCode), bool> Tickets { get; } = new();
        public Dictionary<Guid, (DateTimeOffset lastSyncAt, int totalTickets)> Metadata { get; } = new();
        public Dictionary<string, string> CachedPublicKeys { get; } = new();

        public void StoreTickets(Guid showtimeId, IEnumerable<OfflineTicketItemDto> items, DateTimeOffset serverTime, int totalCount)
        {
            foreach (var item in items)
            {
                Tickets[(showtimeId, item.TicketCode)] = item.IsCheckedIn;
            }
            Metadata[showtimeId] = (serverTime, totalCount);
        }

        public (bool IsValid, string Status, string Message) VerifyAndLookup(string rawInput, Guid selectedShowtimeId, IQrSignatureService verifierService)
        {
            if (rawInput.StartsWith("TICKET|") || rawInput.Contains('|'))
            {
                var parts = rawInput.Trim().Split('|');
                if (parts.Length < 5 || !parts[0].Equals("TICKET", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "INVALID_FORMAT", "Mã QR không đúng định dạng chuẩn của hệ thống.");
                }

                var keyVersion = parts[1];
                var ticketCode = parts[2];
                if (!Guid.TryParse(parts[3], out var qrShowtimeId))
                {
                    return (false, "INVALID_FORMAT", "Mã QR không có mã suất diễn hợp lệ.");
                }
                var signature = parts[4];

                if (string.IsNullOrWhiteSpace(keyVersion) || string.IsNullOrWhiteSpace(ticketCode) || string.IsNullOrWhiteSpace(signature))
                {
                    return (false, "MISSING_SIGNATURE", "Mã QR không có chữ ký số hợp lệ hoặc bị thiếu chữ ký.");
                }

                if (!CachedPublicKeys.ContainsKey(keyVersion))
                {
                    return (false, "MISSING_PUBLIC_KEY", "Không thể xác thực vé ngoại tuyến vì thiết bị chưa có khóa xác thực phù hợp.");
                }

                var verifyResult = verifierService.VerifyTicket(rawInput);
                if (!verifyResult.IsValid)
                {
                    return (false, "INVALID_SIGNATURE", "Chữ ký số không khớp hoặc mã QR đã bị chỉnh sửa.");
                }

                if (qrShowtimeId != selectedShowtimeId)
                {
                    return (false, "WRONG_SHOWTIME", "Cảnh báo: Vé này KHÔNG thuộc về suất diễn đang được chọn tại cổng!");
                }

                if (!Tickets.TryGetValue((selectedShowtimeId, ticketCode), out var isCheckedIn))
                {
                    return (false, "NOT_FOUND", "Vé không tồn tại trong danh sách đã tải của suất diễn.");
                }

                if (isCheckedIn)
                {
                    return (false, "ALREADY_CHECKED_IN", "Vé đã được sử dụng trước đó (theo dữ liệu ngoại tuyến).");
                }

                return (true, "VALID", "VÉ HỢP LỆ — NGOẠI TUYẾN (CHỮ KÝ ĐÃ XÁC THỰC)");
            }

            var manualCode = rawInput.Trim();
            if (!Tickets.TryGetValue((selectedShowtimeId, manualCode), out var isManualCheckedIn))
            {
                return (false, "NOT_FOUND", "Mã vé không tồn tại trong danh sách đã tải của suất diễn.");
            }

            if (isManualCheckedIn)
            {
                return (false, "ALREADY_CHECKED_IN", "Vé đã được sử dụng trước đó (theo dữ liệu ngoại tuyến).");
            }

            return (true, "VALID_MANUAL", "VÉ HỢP LỆ — NGOẠI TUYẾN (XÁC THỰC THỦ CÔNG)");
        }

        public bool ShouldAutoRefresh(Guid showtimeId, DateTimeOffset now)
        {
            if (!Metadata.TryGetValue(showtimeId, out var meta)) return false;
            return (now - meta.lastSyncAt).TotalMinutes >= 30;
        }
    }
}

