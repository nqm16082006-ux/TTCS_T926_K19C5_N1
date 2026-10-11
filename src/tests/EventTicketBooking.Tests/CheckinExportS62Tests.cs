using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class CheckinExportS62Tests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly CheckinExportService _service;
        private readonly TicketCheckInController _controller;
        private readonly Guid _userId = Guid.NewGuid();

        public CheckinExportS62Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _service = new CheckinExportService(_context);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim(ClaimTypes.Role, "Staff")
            };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var principal = new ClaimsPrincipal(identity);

            _controller = new TicketCheckInController(
                _context,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketCheckInController>.Instance,
                null,
                null,
                _service)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = principal }
                }
            };
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        [Fact]
        public async Task GetCheckinExportData_ThrowsKeyNotFoundException_WhenShowtimeDoesNotExist()
        {
            var query = new ExportCheckinListQuery { ShowId = Guid.NewGuid() };
            await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetCheckinExportDataAsync(query));
        }

        [Fact]
        public async Task GetCheckinExportData_ReturnsCheckedInTickets_WithCorrectProperties()
        {
            // Arrange
            var (showtime, ticket) = await SeedShowtimeWithTicketAsync(OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A", row: "B", seatNumber: 12);

            var query = new ExportCheckinListQuery { ShowId = showtime.Id };

            // Act
            var result = await _service.GetCheckinExportDataAsync(query);

            // Assert
            Assert.Single(result);
            var item = result.First();
            Assert.Equal(ticket.TicketCode, item.TicketCode);
            Assert.Equal("B12", item.SeatNumber);
            Assert.Equal("B12", item.SeatCode);
            Assert.Equal("Cổng A", item.GateName);
            Assert.Equal("Cổng A", item.DoorName);
            Assert.False(item.IsConflict);
            Assert.False(item.HasConflict);
            Assert.Null(item.ConflictNote);
            Assert.NotNull(item.CheckinTime);
        }

        [Fact]
        public async Task GetCheckinExportData_FlagsConflict_WhenOrderIsNotPaidButCheckedIn()
        {
            // Arrange: Vé thuộc đơn hàng Cancelled nhưng vẫn có dữ liệu check-in
            var (showtime, ticket) = await SeedShowtimeWithTicketAsync(OrderStatus.Cancelled, isCheckedIn: true, gate: "Cổng VIP", row: "A", seatNumber: 1);

            var query = new ExportCheckinListQuery { ShowId = showtime.Id };

            // Act
            var result = await _service.GetCheckinExportDataAsync(query);

            // Assert
            Assert.Single(result);
            var item = result.First();
            Assert.True(item.IsConflict);
            Assert.True(item.HasConflict);
            Assert.NotNull(item.ConflictNote);
            Assert.Contains("Cancelled", item.ConflictNote);
        }

        [Fact]
        public async Task GetCheckinExportData_FlagsConflictAndIncludesReadmissions_WhenTicketScannedMultipleTimes()
        {
            // Arrange: Vé có lượt vào ban đầu và 1 lượt vào bổ sung (S-31 Readmission)
            var (showtime, ticket) = await SeedShowtimeWithTicketAsync(OrderStatus.Paid, isCheckedIn: true, gate: "Cổng 1", row: "C", seatNumber: 5);

            var readmission = new TicketReadmission
            {
                Id = Guid.NewGuid(),
                OrderItemId = ticket.OrderItemId,
                Gate = "Cổng 2",
                AdmittedAt = DateTimeOffset.UtcNow.AddMinutes(30),
                StaffUserId = _userId,
                StaffName = "Nguyễn Văn A",
                Reason = "Khách ra ngoài nghe điện thoại"
            };
            _context.TicketReadmissions.Add(readmission);
            await _context.SaveChangesAsync();

            var query = new ExportCheckinListQuery { ShowId = showtime.Id };

            // Act
            var result = await _service.GetCheckinExportDataAsync(query);

            // Assert: Phải có 2 dòng (dòng check-in ban đầu + dòng readmission)
            Assert.Equal(2, result.Count);

            var primary = result.First(r => r.GateName == "Cổng 1");
            Assert.True(primary.IsConflict);
            Assert.Contains("quét lại nhiều lần", primary.ConflictNote);

            var second = result.First(r => r.GateName == "Cổng 2");
            Assert.True(second.IsConflict);
            Assert.Contains("Vào bổ sung", second.ConflictNote);
            Assert.Contains("Khách ra ngoài nghe điện thoại", second.ConflictNote);
        }

        [Fact]
        public async Task GetCheckinExportData_FiltersByGateName_Correctly()
        {
            // Arrange: Tạo 2 vé ở 2 cổng khác nhau
            var ev = new Event { Id = Guid.NewGuid(), Title = "Concert", Location = "Hà Nội" };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2) };
            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);

            var cat = new SeatCategory { Id = Guid.NewGuid(), Name = "VIP", ShowtimeId = showtime.Id };
            _context.SeatCategories.Add(cat);

            var seat1 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = cat.Id, Row = "A", SeatNumber = 1 };
            var seat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = cat.Id, Row = "A", SeatNumber = 2 };
            _context.Seats.AddRange(seat1, seat2);

            var user = new User { Id = Guid.NewGuid(), Username = "user1", Email = "u1@test.com", PasswordHash = "hash" };
            _context.Users.Add(user);

            var order = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, UserId = user.Id, Status = OrderStatus.Paid };
            _context.Orders.Add(order);

            var oi1 = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, SeatId = seat1.Id, IsCheckedIn = true, CheckInGate = "Cổng Tây", CheckInTime = DateTimeOffset.UtcNow };
            var oi2 = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, SeatId = seat2.Id, IsCheckedIn = true, CheckInGate = "Cổng Đông", CheckInTime = DateTimeOffset.UtcNow };
            _context.OrderItems.AddRange(oi1, oi2);

            var t1 = new Ticket { Id = Guid.NewGuid(), OrderItemId = oi1.Id, TicketCode = "TC-GATE-WEST" };
            var t2 = new Ticket { Id = Guid.NewGuid(), OrderItemId = oi2.Id, TicketCode = "TC-GATE-EAST" };
            _context.Tickets.AddRange(t1, t2);
            await _context.SaveChangesAsync();

            // Act: Lọc chỉ lấy Cổng Tây
            var query = new ExportCheckinListQuery { ShowId = showtime.Id, GateName = "Cổng Tây" };
            var result = await _service.GetCheckinExportDataAsync(query);

            // Assert
            Assert.Single(result);
            Assert.Equal("TC-GATE-WEST", result[0].TicketCode);
            Assert.Equal("Cổng Tây", result[0].GateName);
        }

        [Fact]
        public async Task GetCheckinExportData_FiltersByOnlyConflicts_Correctly()
        {
            // Arrange: 1 vé hợp lệ, 1 vé xung đột (đơn Cancelled)
            var ev = new Event { Id = Guid.NewGuid(), Title = "Concert", Location = "Hà Nội" };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2) };
            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);

            var cat = new SeatCategory { Id = Guid.NewGuid(), Name = "Standard", ShowtimeId = showtime.Id };
            _context.SeatCategories.Add(cat);

            var seat1 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = cat.Id, Row = "A", SeatNumber = 1 };
            var seat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = cat.Id, Row = "A", SeatNumber = 2 };
            _context.Seats.AddRange(seat1, seat2);

            var user = new User { Id = Guid.NewGuid(), Username = "user2", Email = "u2@test.com", PasswordHash = "hash" };
            _context.Users.Add(user);

            var paidOrder = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, UserId = user.Id, Status = OrderStatus.Paid };
            var cancelledOrder = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, UserId = user.Id, Status = OrderStatus.Cancelled };
            _context.Orders.AddRange(paidOrder, cancelledOrder);

            var oi1 = new OrderItem { Id = Guid.NewGuid(), OrderId = paidOrder.Id, SeatId = seat1.Id, IsCheckedIn = true, CheckInGate = "Cổng 1", CheckInTime = DateTimeOffset.UtcNow };
            var oi2 = new OrderItem { Id = Guid.NewGuid(), OrderId = cancelledOrder.Id, SeatId = seat2.Id, IsCheckedIn = true, CheckInGate = "Cổng 1", CheckInTime = DateTimeOffset.UtcNow };
            _context.OrderItems.AddRange(oi1, oi2);

            var t1 = new Ticket { Id = Guid.NewGuid(), OrderItemId = oi1.Id, TicketCode = "TC-VALID" };
            var t2 = new Ticket { Id = Guid.NewGuid(), OrderItemId = oi2.Id, TicketCode = "TC-CONFLICT" };
            _context.Tickets.AddRange(t1, t2);
            await _context.SaveChangesAsync();

            // Act
            var query = new ExportCheckinListQuery { ShowId = showtime.Id, OnlyConflicts = true };
            var result = await _service.GetCheckinExportDataAsync(query);

            // Assert
            Assert.Single(result);
            Assert.Equal("TC-CONFLICT", result[0].TicketCode);
            Assert.True(result[0].IsConflict);
        }

        [Fact]
        public void GenerateCsvBytes_GeneratesValidCsv_WithUtf8Bom_AndCorrectHeaders()
        {
            // Arrange
            var items = new List<CheckinExportItemDto>
            {
                new CheckinExportItemDto
                {
                    TicketCode = "TC-001",
                    SeatNumber = "VIP-01",
                    CheckinTime = new DateTimeOffset(2026, 10, 11, 19, 30, 0, TimeSpan.FromHours(7)),
                    GateName = "Cổng VIP",
                    IsConflict = false,
                    ConflictNote = null
                },
                new CheckinExportItemDto
                {
                    TicketCode = "TC-002",
                    SeatNumber = "A15",
                    CheckinTime = new DateTimeOffset(2026, 10, 11, 19, 35, 0, TimeSpan.FromHours(7)),
                    GateName = "Cổng Bắc, Tầng 1", // Chứa dấu phẩy để test RFC 4180 escaping
                    IsConflict = true,
                    ConflictNote = "Vé quét lại \"lần 2\"" // Chứa dấu nháy kép để test escaping
                }
            };

            // Act
            var bytes = _service.GenerateCsvBytes(items);

            // Assert BOM
            Assert.NotNull(bytes);
            Assert.True(bytes.Length >= 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);

            // Decode as string
            var csvString = Encoding.UTF8.GetString(bytes);
            var lines = csvString.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            // Kiểm tra Header (bỏ qua ký tự BOM \uFEFF ở đầu)
            Assert.Equal("Mã vé,Số ghế,Thời gian vào,Cửa vào,Cờ xung đột,Ghi chú xung đột", lines[0].TrimStart('\uFEFF'));

            // Kiểm tra dòng 1
            Assert.Contains("TC-001,VIP-01,2026-10-11 19:30:00,Cổng VIP,Không,", lines[1]);

            // Kiểm tra dòng 2 (escaping nháy kép và dấu phẩy)
            Assert.Contains("\"Cổng Bắc, Tầng 1\"", lines[2]);
            Assert.Contains("Có,", lines[2]);
            Assert.Contains("\"Vé quét lại \"\"lần 2\"\"\"", lines[2]);
        }

        [Fact]
        public async Task ExportCheckinListCsv_ControllerEndpoint_ReturnsFileResult_WithCorrectHeaders()
        {
            // Arrange
            var (showtime, ticket) = await SeedShowtimeWithTicketAsync(OrderStatus.Paid, isCheckedIn: true, gate: "Cổng Chính", row: "H", seatNumber: 8);

            // Act
            var actionResult = await _controller.ExportCheckinListCsv(showtime.Id);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(actionResult);
            Assert.Equal("text/csv; charset=utf-8", fileResult.ContentType);
            Assert.Equal($"danh-sach-checkin-suat-{showtime.Id}.csv", fileResult.FileDownloadName);
            Assert.NotEmpty(fileResult.FileContents);

            // Verify BOM
            Assert.Equal(0xEF, fileResult.FileContents[0]);
            Assert.Equal(0xBB, fileResult.FileContents[1]);
            Assert.Equal(0xBF, fileResult.FileContents[2]);
        }

        [Fact]
        public async Task ExportCheckinListCsv_ControllerEndpoint_ReturnsNotFound_WhenShowtimeDoesNotExist()
        {
            var notFoundShowId = Guid.NewGuid();
            var actionResult = await _controller.ExportCheckinListCsv(notFoundShowId);
            Assert.IsType<NotFoundObjectResult>(actionResult);
        }

        [Fact]
        public async Task ExportCheckinListCsv_ControllerEndpoint_ReturnsBadRequest_WhenShowIdIsEmpty()
        {
            var actionResult = await _controller.ExportCheckinListCsv(Guid.Empty);
            Assert.IsType<BadRequestObjectResult>(actionResult);
        }

        [Fact]
        public async Task ExportCheckinListCsv_WritesSampleCsvFile_ForEndToEndInspection()
        {
            // Seed 1 vé hợp lệ
            var (showtime, ticket1) = await SeedShowtimeWithTicketAsync(OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A", row: "A", seatNumber: 12);

            // Seed 1 vé có xung đột (đơn Cancelled nhưng đã có lượt quét)
            var cat = await _context.SeatCategories.FirstAsync(c => c.ShowtimeId == showtime.Id);
            var seat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = cat.Id, Row = "VIP", SeatNumber = 1 };
            _context.Seats.Add(seat2);
            var order2 = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, UserId = _userId, Status = OrderStatus.Cancelled };
            _context.Orders.Add(order2);
            var oi2 = new OrderItem { Id = Guid.NewGuid(), OrderId = order2.Id, SeatId = seat2.Id, IsCheckedIn = true, CheckInGate = "Cổng VIP", CheckInTime = DateTimeOffset.UtcNow };
            _context.OrderItems.Add(oi2);
            var ticket2 = new Ticket { Id = Guid.NewGuid(), OrderItemId = oi2.Id, TicketCode = "TK-CONFLICT-CANCELLED" };
            _context.Tickets.Add(ticket2);

            // Seed 1 lượt vào bổ sung (S-31 Readmission - vé quét lại nhiều lần)
            var readmission = new TicketReadmission
            {
                Id = Guid.NewGuid(),
                OrderItemId = ticket1.OrderItemId,
                Gate = "Cổng B",
                AdmittedAt = DateTimeOffset.UtcNow.AddMinutes(45),
                StaffUserId = _userId,
                StaffName = "Nguyễn Văn Bảo Vệ",
                Reason = "Khách ra ngoài hút thuốc vào lại"
            };
            _context.TicketReadmissions.Add(readmission);
            await _context.SaveChangesAsync();

            // Gọi endpoint Controller
            var actionResult = await _controller.ExportCheckinListCsv(showtime.Id);
            var fileResult = Assert.IsType<FileContentResult>(actionResult);

            var rootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
            var outputPath = Path.Combine(rootDir, "danh-sach-checkin-test.csv");
            await File.WriteAllBytesAsync(outputPath, fileResult.FileContents);

            Assert.True(File.Exists(outputPath));
            Assert.True(fileResult.FileContents.Length > 100);
        }

        private async Task<(Showtime showtime, Ticket ticket)> SeedShowtimeWithTicketAsync(
            OrderStatus orderStatus, bool isCheckedIn, string gate, string row, int seatNumber)
        {
            var ev = new Event { Id = Guid.NewGuid(), Title = "Live Show 2026", Location = "TP.HCM" };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                StartTime = DateTime.UtcNow.AddHours(1),
                EndTime = DateTime.UtcNow.AddHours(3)
            };
            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);

            var cat = new SeatCategory { Id = Guid.NewGuid(), Name = "Tiêu chuẩn", ShowtimeId = showtime.Id };
            _context.SeatCategories.Add(cat);

            var seat = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                SeatCategoryId = cat.Id,
                Row = row,
                SeatNumber = seatNumber
            };
            _context.Seats.Add(seat);

            var user = new User { Id = Guid.NewGuid(), Username = "buyer", Email = "buyer@test.com", PasswordHash = "hash" };
            _context.Users.Add(user);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                UserId = user.Id,
                Status = orderStatus
            };
            _context.Orders.Add(order);

            var oi = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = seat.Id,
                IsCheckedIn = isCheckedIn,
                CheckInGate = gate,
                CheckInTime = isCheckedIn ? DateTimeOffset.UtcNow : null
            };
            _context.OrderItems.Add(oi);

            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = oi.Id,
                TicketCode = $"TK-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}"
            };
            _context.Tickets.Add(ticket);

            await _context.SaveChangesAsync();
            return (showtime, ticket);
        }
    }
}
