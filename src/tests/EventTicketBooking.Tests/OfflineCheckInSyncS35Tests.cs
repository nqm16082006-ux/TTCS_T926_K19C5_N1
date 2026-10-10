using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Api.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử tự động toàn diện cho User Story S-35:
    /// "Đồng bộ các lần quét ngoại tuyến lên máy chủ khi có mạng lại"
    /// Kiểm chứng đầy đủ AC1, AC2, AC3, NFR Idempotency, Transaction Atomic, Concurrency và QR Security.
    /// </summary>
    public class OfflineCheckInSyncS35Tests : IDisposable
    {
        private readonly AppDbContext _inMemoryContext;
        private readonly IQrSignatureService _qrService;
        private readonly Guid _staffUserId = Guid.NewGuid();

        private readonly string _sqliteDbPath = Path.Combine(Path.GetTempPath(), $"s35_test_{Guid.NewGuid():N}.db");
        private bool _sqliteInitialized = false;

        public OfflineCheckInSyncS35Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: "S35_InMemory_" + Guid.NewGuid().ToString())
                .Options;

            _inMemoryContext = new AppDbContext(options);
            _qrService = new QrSignatureService();
        }

        public void Dispose()
        {
            _inMemoryContext.Database.EnsureDeleted();
            _inMemoryContext.Dispose();

            SqliteConnection.ClearAllPools();
            if (File.Exists(_sqliteDbPath))
            {
                try { File.Delete(_sqliteDbPath); } catch { }
            }
        }

        private TicketCheckInController CreateController(AppDbContext context, Guid? userId = null, string role = "Staff")
        {
            var staffId = userId ?? _staffUserId;
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, staffId.ToString()),
                new Claim(ClaimTypes.Role, role),
                new Claim("role", role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            var httpContext = new DefaultHttpContext { User = claimsPrincipal };

            return new TicketCheckInController(context, NullLogger<TicketCheckInController>.Instance, _qrService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };
        }

        private async Task<AppDbContext> CreateSharedSqliteContextAsync()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_sqliteDbPath}")
                .Options;

            var context = new AppDbContext(options);
            if (!_sqliteInitialized)
            {
                await context.Database.EnsureCreatedAsync();
                _sqliteInitialized = true;
            }

            return context;
        }

        private async Task<(Showtime showtime, List<Ticket> paidTickets, User staffUser)> SeedScenarioAsync(
            AppDbContext context,
            int ticketCount = 5,
            Guid? customShowtimeId = null)
        {
            var staff = await context.Users.FindAsync(_staffUserId);
            if (staff == null)
            {
                staff = new User
                {
                    Id = _staffUserId,
                    Username = "scanner_staff_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Email = "staff_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.com",
                    PasswordHash = "hash",
                    FullName = "Staff Scanner",
                    IsActive = true
                };
                context.Users.Add(staff);
                await context.SaveChangesAsync();
            }

            var ev = new Event
            {
                Id = Guid.NewGuid(),
                Title = "S35 Live Concert",
                Location = "Stadium",
                OwnerId = staff.Id,
                Owner = staff
            };
            var showtime = new Showtime
            {
                Id = customShowtimeId ?? Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                StartTime = DateTime.UtcNow.AddHours(1),
                EndTime = DateTime.UtcNow.AddHours(4)
            };
            var category = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Name = "VIP", Price = 500000 };

            context.Events.Add(ev);
            context.Showtimes.Add(showtime);
            context.SeatCategories.Add(category);

            var paidTickets = new List<Ticket>();
            for (int i = 0; i < ticketCount; i++)
            {
                var seat = new Seat
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtime.Id,
                    SeatCategoryId = category.Id,
                    SeatCategory = category,
                    Row = "A",
                    SeatNumber = i + 1
                };
                var order = new Order
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtime.Id,
                    Status = OrderStatus.Paid,
                    TotalAmount = 500000,
                    UserId = staff.Id,
                    User = staff
                };
                var orderItem = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    Order = order,
                    SeatId = seat.Id,
                    Seat = seat,
                    Price = 500000,
                    IsCheckedIn = false
                };
                var ticket = new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderItemId = orderItem.Id,
                    OrderItem = orderItem,
                    TicketCode = $"TK-S35-{Guid.NewGuid():N}".Substring(0, 16)
                };

                context.Seats.Add(seat);
                context.Orders.Add(order);
                context.OrderItems.Add(orderItem);
                context.Tickets.Add(ticket);
                paidTickets.Add(ticket);
            }

            await context.SaveChangesAsync();
            return (showtime, paidTickets, staff);
        }

        #region AC1: Batch Sync 40 scans

        [Fact]
        public async Task S35_AC1_Batch40Scans_AllSyncedSuccessfully_QueueEmptied()
        {
            // Arrange: Chuẩn bị 40 vé hợp lệ
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 40);
            var controller = CreateController(_inMemoryContext);

            var items = new List<OfflineScanBatchItemDto>();
            foreach (var ticket in tickets)
            {
                var scanId = Guid.NewGuid().ToString();
                var qrPayload = _qrService.SignTicket(ticket.TicketCode, showtime.Id);
                items.Add(new OfflineScanBatchItemDto
                {
                    OfflineScanId = scanId,
                    TicketCode = ticket.TicketCode,
                    ShowtimeId = showtime.Id,
                    GateName = "Gate A",
                    ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                    QrPayload = qrPayload
                });
            }

            // Act: Gửi batch 40 lượt quét lên endpoint
            var result = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = items });

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(40, response.TotalSubmitted);
            Assert.Equal(40, response.SyncedCount);
            Assert.Equal(0, response.DuplicateCount);
            Assert.Equal(0, response.ConflictCount);
            Assert.Equal(0, response.RejectedCount);
            Assert.All(response.Results, r =>
            {
                Assert.Equal("synced", r.Status);
                Assert.True(r.IsAckTerminal);
            });

            // Kiểm tra DB cập nhật đầy đủ OrderItems và OfflineCheckInRecords
            var checkedInCount = await _inMemoryContext.OrderItems
                .CountAsync(oi => oi.Order.ShowtimeId == showtime.Id && oi.IsCheckedIn);
            Assert.Equal(40, checkedInCount);

            var recordCount = await _inMemoryContext.OfflineCheckInRecords
                .CountAsync(r => r.ShowtimeId == showtime.Id && r.Status == "SYNCED" && !r.IsConflict);
            Assert.Equal(40, recordCount);
        }

        #endregion

        #region AC2 & NFR: Idempotency & Replay

        [Fact]
        public async Task S35_AC2_ReplaySameBatch_ReturnsDuplicateIdempotent_NoDuplicateRecords()
        {
            // Arrange
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 5);
            var controller = CreateController(_inMemoryContext);

            var items = tickets.Select(t => new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = t.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                QrPayload = _qrService.SignTicket(t.TicketCode, showtime.Id)
            }).ToList();

            // Gửi lần 1: thành công
            var res1 = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = items });
            var ok1 = Assert.IsType<OkObjectResult>(res1);
            var dto1 = Assert.IsType<OfflineSyncBatchResponseDto>(ok1.Value);
            Assert.Equal(5, dto1.SyncedCount);

            // Gửi lần 2 (Replay cùng batch): phải nhận duplicate cho cả 5
            var res2 = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = items });
            var ok2 = Assert.IsType<OkObjectResult>(res2);
            var dto2 = Assert.IsType<OfflineSyncBatchResponseDto>(ok2.Value);

            Assert.Equal(5, dto2.TotalSubmitted);
            Assert.Equal(0, dto2.SyncedCount);
            Assert.Equal(5, dto2.DuplicateCount);
            Assert.All(dto2.Results, r =>
            {
                Assert.Equal("duplicate", r.Status);
                Assert.Equal("IDEMPOTENT_DUPLICATE", r.Reason);
                Assert.True(r.IsAckTerminal);
            });

            // Tổng số record trong DB vẫn là 5, không hề bị nhân đôi
            var totalRecords = await _inMemoryContext.OfflineCheckInRecords.CountAsync();
            Assert.Equal(5, totalRecords);
        }

        #endregion

        #region AC3: Conflict Semantics (Different Gate vs Same Gate)

        [Fact]
        public async Task S35_AC3_TicketAlreadyCheckedIn_DifferentGate_PersistsConflictRecordForS36()
        {
            // Arrange: Vé đã vào tại Gate 1
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var ticket = tickets[0];
            var orderItem = ticket.OrderItem;
            orderItem.IsCheckedIn = true;
            orderItem.CheckInGate = "Gate 1";
            var existingTime = DateTimeOffset.UtcNow.AddMinutes(-30);
            orderItem.CheckInTime = existingTime;
            await _inMemoryContext.SaveChangesAsync();

            var controller = CreateController(_inMemoryContext);

            // Act: Quét ngoại tuyến từ Gate 2
            var scanId = Guid.NewGuid().ToString();
            var deviceScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = scanId,
                TicketCode = ticket.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 2", // KHÁC CỬA
                ScannedAt = deviceScannedAt,
                QrPayload = _qrService.SignTicket(ticket.TicketCode, showtime.Id)
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.ConflictCount);
            var itemRes = dto.Results[0];
            Assert.Equal("conflict", itemRes.Status);
            Assert.Equal("DIFFERENT_GATE_CONFLICT", itemRes.Reason);
            Assert.True(itemRes.IsAckTerminal);

            // OrderItem TUYỆT ĐỐI KHÔNG bị ghi đè
            var reloadedOrderItem = await _inMemoryContext.OrderItems.FindAsync(orderItem.Id);
            Assert.NotNull(reloadedOrderItem);
            Assert.Equal("Gate 1", reloadedOrderItem.CheckInGate);
            Assert.Equal(existingTime, reloadedOrderItem.CheckInTime);

            // Bảng OfflineCheckInRecords lưu snapshot có cấu trúc cho S-36
            var conflictRecord = await _inMemoryContext.OfflineCheckInRecords.FirstOrDefaultAsync(r => r.OfflineScanId == scanId);
            Assert.NotNull(conflictRecord);
            Assert.True(conflictRecord.IsConflict);
            Assert.Equal("CONFLICT", conflictRecord.Status);
            Assert.Equal("DIFFERENT_GATE_CONFLICT", conflictRecord.ConflictReason);
            Assert.Equal("Gate 1", conflictRecord.ExistingCheckInGate);
            Assert.Equal(existingTime, conflictRecord.ExistingCheckInTime);
            Assert.Equal("Gate 2", conflictRecord.GateName);
            Assert.Equal(deviceScannedAt, conflictRecord.ScannedAtDevice);
        }

        [Fact]
        public async Task S35_TicketAlreadyCheckedIn_SameGate_ReturnsRejectedSameGate_NoConflictRecordCreated()
        {
            // Arrange: Vé đã vào tại Gate 1
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var ticket = tickets[0];
            var orderItem = ticket.OrderItem;
            orderItem.IsCheckedIn = true;
            orderItem.CheckInGate = "Gate 1";
            orderItem.CheckInTime = DateTimeOffset.UtcNow.AddMinutes(-30);
            await _inMemoryContext.SaveChangesAsync();

            var controller = CreateController(_inMemoryContext);

            // Act: Quét lại cùng tại Gate 1
            var scanId = Guid.NewGuid().ToString();
            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = scanId,
                TicketCode = ticket.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1", // CÙNG CỬA
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                QrPayload = _qrService.SignTicket(ticket.TicketCode, showtime.Id)
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal(0, dto.ConflictCount);
            var itemRes = dto.Results[0];
            Assert.Equal("rejected", itemRes.Status);
            Assert.Equal("ALREADY_CHECKED_IN_SAME_GATE", itemRes.Reason);
            Assert.True(itemRes.IsAckTerminal);

            // KHÔNG tạo record conflict rác cho S-36
            var record = await _inMemoryContext.OfflineCheckInRecords.FirstOrDefaultAsync(r => r.OfflineScanId == scanId);
            Assert.Null(record);
        }

        #endregion

        #region QR Security Validation

        [Fact]
        public async Task S35_MissingQrPayload_ReturnsRejected_MISSING_QR_PAYLOAD()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var controller = CreateController(_inMemoryContext);

            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow,
                QrPayload = "" // Rỗng
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal("MISSING_QR_PAYLOAD", dto.Results[0].Reason);
            Assert.True(dto.Results[0].IsAckTerminal);
        }

        [Fact]
        public async Task S35_InvalidQrSignature_ReturnsRejected_INVALID_QR_SIGNATURE()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var controller = CreateController(_inMemoryContext);

            var fakeQr = $"TICKET|v1|{tickets[0].TicketCode}|{showtime.Id}|INVALID_BASE64_SIGNATURE";
            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow,
                QrPayload = fakeQr
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal("INVALID_QR_SIGNATURE", dto.Results[0].Reason);
            Assert.True(dto.Results[0].IsAckTerminal);
        }

        [Fact]
        public async Task S35_QrPayloadMismatch_TicketCodeOrShowtimeId_ReturnsRejected_QR_PAYLOAD_MISMATCH()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 2);
            var controller = CreateController(_inMemoryContext);

            // Ký vé 1 nhưng gửi vé 2 trong request body
            var validQrForTicket1 = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id);
            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = tickets[1].TicketCode, // Mismatch!
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow,
                QrPayload = validQrForTicket1
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal("QR_PAYLOAD_MISMATCH", dto.Results[0].Reason);
            Assert.True(dto.Results[0].IsAckTerminal);
        }

        #endregion

        #region Business Validation: Wrong Showtime, Unpaid, Unknown Ticket

        [Fact]
        public async Task S35_WrongShowtime_ReturnsRejected_WRONG_SHOWTIME()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var controller = CreateController(_inMemoryContext);

            var otherShowtimeId = Guid.NewGuid();
            var validQr = _qrService.SignTicket(tickets[0].TicketCode, otherShowtimeId);

            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = otherShowtimeId, // Sai suất diễn
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow,
                QrPayload = validQr
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal("WRONG_SHOWTIME", dto.Results[0].Reason);
        }

        [Fact]
        public async Task S35_UnpaidOrder_ReturnsRejected_UNPAID_ORDER()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var ticket = tickets[0];
            ticket.OrderItem.Order.Status = OrderStatus.Pending; // Chưa thanh toán
            await _inMemoryContext.SaveChangesAsync();

            var controller = CreateController(_inMemoryContext);
            var validQr = _qrService.SignTicket(ticket.TicketCode, showtime.Id);

            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = ticket.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow,
                QrPayload = validQr
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal("UNPAID_ORDER", dto.Results[0].Reason);
        }

        [Fact]
        public async Task S35_UnknownTicket_ReturnsRejected_UNKNOWN_TICKET()
        {
            var (showtime, _, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var controller = CreateController(_inMemoryContext);

            var nonExistentCode = "TK-NON-EXISTENT";
            var validQr = _qrService.SignTicket(nonExistentCode, showtime.Id);

            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = nonExistentCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow,
                QrPayload = validQr
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal("UNKNOWN_TICKET", dto.Results[0].Reason);
        }

        #endregion

        #region Relational SQLite Concurrency Tests (Two Separate DbContexts)

        [Fact]
        public async Task S35_Relational_ConcurrentSameOfflineScanId_TwoSeparateDbContexts_OneSyncedOneDuplicate()
        {
            // Yêu cầu: KHÔNG dùng chung AppDbContext giữa 2 Task
            // DbContext A / Scope A và DbContext B / Scope B cùng relational database (SQLite shared connection)
            using var contextA = await CreateSharedSqliteContextAsync();
            using var contextB = await CreateSharedSqliteContextAsync();

            var (showtime, tickets, _) = await SeedScenarioAsync(contextA, ticketCount: 1);
            var ticket = tickets[0];
            var sharedScanId = Guid.NewGuid().ToString();
            var qrPayload = _qrService.SignTicket(ticket.TicketCode, showtime.Id);

            var itemA = new OfflineScanBatchItemDto
            {
                OfflineScanId = sharedScanId,
                TicketCode = ticket.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                QrPayload = qrPayload
            };

            var itemB = new OfflineScanBatchItemDto
            {
                OfflineScanId = sharedScanId,
                TicketCode = ticket.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                QrPayload = qrPayload
            };

            var controllerA = CreateController(contextA);
            var controllerB = CreateController(contextB);

            // Bắn 2 request đồng thời từ 2 luồng độc lập
            var taskA = Task.Run(() => controllerA.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { itemA } }));
            var taskB = Task.Run(() => controllerB.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { itemB } }));

            var responses = await Task.WhenAll(taskA, taskB);

            // Cả 2 đều phải trả Ok 200 (không có Task nào văng 500 do lỗi unique index)
            var okA = Assert.IsType<OkObjectResult>(responses[0]);
            var okB = Assert.IsType<OkObjectResult>(responses[1]);
            var resA = Assert.IsType<OfflineSyncBatchResponseDto>(okA.Value);
            var resB = Assert.IsType<OfflineSyncBatchResponseDto>(okB.Value);

            // Chính xác 1 request thành công và 1 request nhận duplicate
            var totalSynced = resA.SyncedCount + resB.SyncedCount;
            var totalDuplicate = resA.DuplicateCount + resB.DuplicateCount;

            Assert.Equal(1, totalSynced);
            Assert.Equal(1, totalDuplicate);

            // Kiểm tra DB chỉ có đúng 1 bản ghi
            using var verifyContext = await CreateSharedSqliteContextAsync();
            var recordCount = await verifyContext.OfflineCheckInRecords.CountAsync(r => r.OfflineScanId == sharedScanId);
            Assert.Equal(1, recordCount);
        }

        [Fact]
        public async Task S35_Relational_ConcurrentSameTicketDifferentGates_OneSyncedOneConflict()
        {
            // Hai máy ngoại tuyến cùng quét 1 vé tại 2 cửa khác nhau, sau đó sync đồng thời
            using var contextA = await CreateSharedSqliteContextAsync();
            using var contextB = await CreateSharedSqliteContextAsync();

            var (showtime, tickets, _) = await SeedScenarioAsync(contextA, ticketCount: 1);
            var ticket = tickets[0];
            var qrPayload = _qrService.SignTicket(ticket.TicketCode, showtime.Id);

            var itemGate1 = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = ticket.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate A",
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                QrPayload = qrPayload
            };

            var itemGate2 = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = ticket.TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate B",
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                QrPayload = qrPayload
            };

            var controllerA = CreateController(contextA);
            var controllerB = CreateController(contextB);

            var taskA = Task.Run(() => controllerA.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { itemGate1 } }));
            var taskB = Task.Run(() => controllerB.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { itemGate2 } }));

            var responses = await Task.WhenAll(taskA, taskB);

            var okA = Assert.IsType<OkObjectResult>(responses[0]);
            var okB = Assert.IsType<OkObjectResult>(responses[1]);
            var resA = Assert.IsType<OfflineSyncBatchResponseDto>(okA.Value);
            var resB = Assert.IsType<OfflineSyncBatchResponseDto>(okB.Value);

            // Chính xác 1 request thành công và 1 request trở thành conflict
            var totalSynced = resA.SyncedCount + resB.SyncedCount;
            var totalConflict = resA.ConflictCount + resB.ConflictCount;

            Assert.Equal(1, totalSynced);
            Assert.Equal(1, totalConflict);

            // Vé đã checked in
            using var verifyContext = await CreateSharedSqliteContextAsync();
            var orderItem = await verifyContext.OrderItems.FindAsync(ticket.OrderItemId);
            Assert.NotNull(orderItem);
            Assert.True(orderItem.IsCheckedIn);

            // Có đúng 1 record conflict
            var conflictRecord = await verifyContext.OfflineCheckInRecords.FirstOrDefaultAsync(r => r.IsConflict);
            Assert.NotNull(conflictRecord);
            Assert.Equal("DIFFERENT_GATE_CONFLICT", conflictRecord.ConflictReason);
        }

        [Fact]
        public async Task S35_Relational_OnlineScanVsOfflineSync_Concurrent()
        {
            using var contextOnline = await CreateSharedSqliteContextAsync();
            using var contextOffline = await CreateSharedSqliteContextAsync();

            var (showtime, tickets, _) = await SeedScenarioAsync(contextOnline, ticketCount: 1);
            var ticket = tickets[0];
            var qrPayload = _qrService.SignTicket(ticket.TicketCode, showtime.Id);

            var controllerOnline = CreateController(contextOnline);
            var controllerOffline = CreateController(contextOffline);

            var onlineReq = new TicketScanRequestDto
            {
                SelectedShowtimeId = showtime.Id,
                TicketCode = ticket.TicketCode,
                GateName = "Gate Online",
                QrPayload = qrPayload
            };

            var offlineReq = new OfflineSyncBatchRequestDto
            {
                Items = new()
                {
                    new OfflineScanBatchItemDto
                    {
                        OfflineScanId = Guid.NewGuid().ToString(),
                        TicketCode = ticket.TicketCode,
                        ShowtimeId = showtime.Id,
                        GateName = "Gate Offline",
                        ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                        QrPayload = qrPayload
                    }
                }
            };

            var taskOnline = Task.Run(() => controllerOnline.ScanTicket(onlineReq));
            var taskOffline = Task.Run(() => controllerOffline.OfflineSync(offlineReq));

            var responses = await Task.WhenAll(taskOnline, taskOffline);

            // Không task nào bị 500
            Assert.NotNull(responses[0]);
            Assert.NotNull(responses[1]);

            // Trạng thái cuối:
            // 1. OrderItem.IsCheckedIn == true
            // 2. Chỉ đúng một bên được ghi nhận check-in chính
            // 3. Không có half-written state
            using var verifyContext = await CreateSharedSqliteContextAsync();
            var orderItem = await verifyContext.OrderItems.FindAsync(ticket.OrderItemId);
            Assert.NotNull(orderItem);
            Assert.True(orderItem.IsCheckedIn);

            if (orderItem.CheckInGate == "Gate Online")
            {
                // Online thắng: Offline sync khác cửa ("Gate Offline") phải ghi nhận record CONFLICT cho S-36
                var conflictRecord = await verifyContext.OfflineCheckInRecords.FirstOrDefaultAsync(r => r.OfflineScanId == offlineReq.Items[0].OfflineScanId);
                Assert.NotNull(conflictRecord);
                Assert.True(conflictRecord.IsConflict);
                Assert.Equal("DIFFERENT_GATE_CONFLICT", conflictRecord.ConflictReason);
                Assert.Equal("Gate Online", conflictRecord.ExistingCheckInGate);
                Assert.Equal("Gate Offline", conflictRecord.GateName);
            }
            else if (orderItem.CheckInGate == "Gate Offline")
            {
                // Offline thắng: Offline sync ghi nhận SYNCED thành công
                var syncRecord = await verifyContext.OfflineCheckInRecords.FirstOrDefaultAsync(r => r.OfflineScanId == offlineReq.Items[0].OfflineScanId);
                Assert.NotNull(syncRecord);
                Assert.False(syncRecord.IsConflict);
                Assert.Equal("SYNCED", syncRecord.Status);
                Assert.Equal("Gate Offline", syncRecord.GateName);
            }
            else
            {
                Assert.Fail($"CheckInGate mang giá trị không hợp lệ: {orderItem.CheckInGate}");
            }
        }

        #endregion

        #region Pre-check Duplicate & Batch Processing

        [Fact]
        public async Task S35_PreCheckDuplicate_SubsequentValidItemInBatchSucceeds()
        {
            using var context = await CreateSharedSqliteContextAsync();
            var (showtime, tickets, _) = await SeedScenarioAsync(context, ticketCount: 2);
            var controller = CreateController(context);

            var existingScanId = Guid.NewGuid().ToString();

            // Pre-seed 1 record đã có sẵn trong DB
            var existingRecord = new OfflineCheckInRecord
            {
                Id = Guid.NewGuid(),
                OfflineScanId = existingScanId,
                OrderItemId = tickets[0].OrderItemId,
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAtDevice = DateTimeOffset.UtcNow.AddMinutes(-20),
                SyncedByUserId = _staffUserId,
                Status = "SYNCED"
            };
            context.OfflineCheckInRecords.Add(existingRecord);
            await context.SaveChangesAsync();

            // Tạo batch:
            // Item 1: chứa scanId đã tồn tại (sẽ bị duplicate qua pre-check)
            // Item 2: chứa scanId mới và vé 2 hợp lệ (phải sync thành công!)
            var batch = new OfflineSyncBatchRequestDto
            {
                Items = new List<OfflineScanBatchItemDto>
                {
                    new OfflineScanBatchItemDto
                    {
                        OfflineScanId = existingScanId,
                        TicketCode = tickets[0].TicketCode,
                        ShowtimeId = showtime.Id,
                        GateName = "Gate 1",
                        ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                        QrPayload = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id)
                    },
                    new OfflineScanBatchItemDto
                    {
                        OfflineScanId = Guid.NewGuid().ToString(),
                        TicketCode = tickets[1].TicketCode,
                        ShowtimeId = showtime.Id,
                        GateName = "Gate 1",
                        ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                        QrPayload = _qrService.SignTicket(tickets[1].TicketCode, showtime.Id)
                    }
                }
            };

            var res = await controller.OfflineSync(batch);
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(2, dto.TotalSubmitted);
            Assert.Equal(1, dto.DuplicateCount);
            Assert.Equal(1, dto.SyncedCount);

            Assert.Equal("duplicate", dto.Results[0].Status);
            Assert.Equal("synced", dto.Results[1].Status);
        }

        #endregion

        #region Authorization Configuration Check

        [Fact]
        public void S35_AuthorizationConfiguration_OfflineSyncHasAdminAndStaffRoleMetadata()
        {
            var method = typeof(TicketCheckInController).GetMethod(nameof(TicketCheckInController.OfflineSync));
            Assert.NotNull(method);

            var methodAttr = method.GetCustomAttributes(typeof(RequireRoleAttribute), true)
                .FirstOrDefault() as RequireRoleAttribute;

            var classAttr = typeof(TicketCheckInController)
                .GetCustomAttributes(typeof(RequireRoleAttribute), true)
                .FirstOrDefault() as RequireRoleAttribute;

            var activeAttr = methodAttr ?? classAttr;
            Assert.NotNull(activeAttr);
            Assert.Contains("Admin", activeAttr.Roles);
            Assert.Contains("Staff", activeAttr.Roles);
            Assert.DoesNotContain("Customer", activeAttr.Roles);
        }

        #endregion

        #region Extended Input Validations & Limits

        [Fact]
        public async Task S35_OfflineScanIdOver100Chars_RejectedBeforeDatabaseWrite()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var controller = CreateController(_inMemoryContext);

            var longScanId = new string('x', 101); // 101 ký tự
            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = longScanId,
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.UtcNow,
                QrPayload = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id)
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.RejectedCount);
            Assert.Equal("INVALID_OFFLINE_SCAN_ID", dto.Results[0].Reason);
            Assert.True(dto.Results[0].IsAckTerminal);
        }

        [Fact]
        public async Task S35_ScannedAtDefaultOrMinValue_RejectedWithInvalidScannedAt()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var controller = CreateController(_inMemoryContext);

            var item1 = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = default, // 0001-01-01
                QrPayload = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id)
            };

            var item2 = new OfflineScanBatchItemDto
            {
                OfflineScanId = Guid.NewGuid().ToString(),
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = DateTimeOffset.MinValue,
                QrPayload = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id)
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item1, item2 } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(2, dto.RejectedCount);
            Assert.Equal("INVALID_SCANNED_AT", dto.Results[0].Reason);
            Assert.Equal("INVALID_SCANNED_AT", dto.Results[1].Reason);
            Assert.True(dto.Results[0].IsAckTerminal);
            Assert.True(dto.Results[1].IsAckTerminal);
        }

        [Fact]
        public async Task S35_IdempotencyKeyReuse_DifferentTicketOrShowtimeOrGate_RejectedWithMismatch()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 2);
            var controller = CreateController(_inMemoryContext);

            var reusedScanId = Guid.NewGuid().ToString();

            // 1. Quét vé 0 thành công với reusedScanId
            var item1 = new OfflineScanBatchItemDto
            {
                OfflineScanId = reusedScanId,
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate A",
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                QrPayload = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id)
            };
            var res1 = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item1 } });
            var ok1 = Assert.IsType<OkObjectResult>(res1);
            var dto1 = Assert.IsType<OfflineSyncBatchResponseDto>(ok1.Value);
            Assert.Equal(1, dto1.SyncedCount);

            // 2. Tái sử dụng reusedScanId nhưng gửi vé 1 (khác TicketCode)
            var itemMismatchTicket = new OfflineScanBatchItemDto
            {
                OfflineScanId = reusedScanId,
                TicketCode = tickets[1].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate A",
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                QrPayload = _qrService.SignTicket(tickets[1].TicketCode, showtime.Id)
            };

            // 3. Tái sử dụng reusedScanId nhưng gửi khác GateName
            var itemMismatchGate = new OfflineScanBatchItemDto
            {
                OfflineScanId = reusedScanId,
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate B", // Khác cửa
                ScannedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                QrPayload = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id)
            };

            var res2 = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { itemMismatchTicket, itemMismatchGate } });
            var ok2 = Assert.IsType<OkObjectResult>(res2);
            var dto2 = Assert.IsType<OfflineSyncBatchResponseDto>(ok2.Value);

            Assert.Equal(2, dto2.RejectedCount);
            Assert.Equal("IDEMPOTENCY_KEY_REUSE_MISMATCH", dto2.Results[0].Reason);
            Assert.Equal("IDEMPOTENCY_KEY_REUSE_MISMATCH", dto2.Results[1].Reason);
            Assert.True(dto2.Results[0].IsAckTerminal);
            Assert.True(dto2.Results[1].IsAckTerminal);

            // Đảm bảo vé 1 không hề bị check-in lén
            var ticket1OrderItem = await _inMemoryContext.OrderItems.FindAsync(tickets[1].OrderItemId);
            Assert.False(ticket1OrderItem!.IsCheckedIn);
        }

        [Fact]
        public async Task S35_BatchSizeOverLimit_ReturnsBadRequest()
        {
            var controller = CreateController(_inMemoryContext);
            var items = new List<OfflineScanBatchItemDto>();
            for (int i = 0; i < TicketCheckInController.MAX_OFFLINE_SYNC_BATCH_SIZE + 1; i++)
            {
                items.Add(new OfflineScanBatchItemDto
                {
                    OfflineScanId = Guid.NewGuid().ToString(),
                    TicketCode = $"TK-{i}",
                    ShowtimeId = Guid.NewGuid(),
                    GateName = "Gate 1",
                    ScannedAt = DateTimeOffset.UtcNow,
                    QrPayload = "payload"
                });
            }

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = items });
            var badRequest = Assert.IsType<BadRequestObjectResult>(res);
            Assert.NotNull(badRequest.Value);
        }

        [Fact]
        public void S35_IsSameOfflineOperation_HelperValidation()
        {
            var method = typeof(TicketCheckInController).GetMethod("IsSameOfflineOperation",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var showtimeId = Guid.NewGuid();
            var record = new OfflineCheckInRecord
            {
                TicketCode = "TK-100",
                ShowtimeId = showtimeId,
                GateName = "Gate A"
            };

            // 1. Cùng mọi trường -> true
            var itemSame = new OfflineScanBatchItemDto
            {
                TicketCode = "tk-100 ",
                ShowtimeId = showtimeId,
                GateName = "Gate A "
            };
            Assert.True((bool)method.Invoke(null, new object[] { record, itemSame })!);

            // 2. Khác TicketCode -> false
            var itemDiffTicket = new OfflineScanBatchItemDto { TicketCode = "TK-200", ShowtimeId = showtimeId, GateName = "Gate A" };
            Assert.False((bool)method.Invoke(null, new object[] { record, itemDiffTicket })!);

            // 3. Khác ShowtimeId -> false
            var itemDiffShowtime = new OfflineScanBatchItemDto { TicketCode = "TK-100", ShowtimeId = Guid.NewGuid(), GateName = "Gate A" };
            Assert.False((bool)method.Invoke(null, new object[] { record, itemDiffShowtime })!);

            // 4. Khác GateName -> false
            var itemDiffGate = new OfflineScanBatchItemDto { TicketCode = "TK-100", ShowtimeId = showtimeId, GateName = "Gate B" };
            Assert.False((bool)method.Invoke(null, new object[] { record, itemDiffGate })!);
        }

        [Fact]
        public async Task S35_TimestampSanity_ClampsFutureScannedAtForOrderItem_PreservesRawScannedAtDevice()
        {
            var (showtime, tickets, _) = await SeedScenarioAsync(_inMemoryContext, ticketCount: 1);
            var controller = CreateController(_inMemoryContext);

            var rawFutureTime = DateTimeOffset.UtcNow.AddHours(2); // Lệch 2 tiếng về tương lai
            var scanId = Guid.NewGuid().ToString();

            var item = new OfflineScanBatchItemDto
            {
                OfflineScanId = scanId,
                TicketCode = tickets[0].TicketCode,
                ShowtimeId = showtime.Id,
                GateName = "Gate 1",
                ScannedAt = rawFutureTime,
                QrPayload = _qrService.SignTicket(tickets[0].TicketCode, showtime.Id)
            };

            var res = await controller.OfflineSync(new OfflineSyncBatchRequestDto { Items = new() { item } });
            var okResult = Assert.IsType<OkObjectResult>(res);
            var dto = Assert.IsType<OfflineSyncBatchResponseDto>(okResult.Value);

            Assert.Equal(1, dto.SyncedCount);

            // Kiểm tra OrderItem.CheckInTime bị clamp (không vượt quá UtcNow + 1 phút)
            var orderItem = await _inMemoryContext.OrderItems.FindAsync(tickets[0].OrderItemId);
            Assert.NotNull(orderItem);
            Assert.True(orderItem.CheckInTime <= DateTimeOffset.UtcNow.AddMinutes(1));

            // Kiểm tra OfflineCheckInRecord.ScannedAtDevice BẢO TOÀN RAW TIMESTAMP
            var record = await _inMemoryContext.OfflineCheckInRecords.FirstOrDefaultAsync(r => r.OfflineScanId == scanId);
            Assert.NotNull(record);
            Assert.Equal(rawFutureTime, record.ScannedAtDevice);
        }

        [Fact]
        public void S35_UniqueViolationHelper_PostgresAndSqlite_FiltersPrecisely()
        {
            // Kiểm tra qua reflection helper private IsOfflineScanIdUniqueViolation
            var method = typeof(TicketCheckInController).GetMethod("IsOfflineScanIdUniqueViolation",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            // 1. SQLite đúng mã 2067 và đúng bảng/cột
            var sqliteUniqueMatch = new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed: OfflineCheckInRecords.OfflineScanId", 19, 2067);
            var ex1 = new DbUpdateException("Error", sqliteUniqueMatch);
            Assert.True((bool)method.Invoke(null, new object[] { ex1 })!);

            // 2. SQLite mã 19 nhưng sai extended code (ví dụ 787 = FOREIGN KEY)
            var sqliteFk = new Microsoft.Data.Sqlite.SqliteException("FOREIGN KEY constraint failed", 19, 787);
            var ex2 = new DbUpdateException("Error", sqliteFk);
            Assert.False((bool)method.Invoke(null, new object[] { ex2 })!);

            // 3. SQLite mã 19 nhưng cho bảng/cột khác
            var sqliteOtherTable = new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed: Users.Username", 19, 2067);
            var ex3 = new DbUpdateException("Error", sqliteOtherTable);
            Assert.False((bool)method.Invoke(null, new object[] { ex3 })!);

            // 4. Exception thông thường khác
            var ex4 = new DbUpdateException("Network timeout", new InvalidOperationException("Timeout"));
            Assert.False((bool)method.Invoke(null, new object[] { ex4 })!);
        }

        #endregion
    }
}
