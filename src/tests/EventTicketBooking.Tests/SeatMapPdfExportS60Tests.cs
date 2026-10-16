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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class SeatMapPdfExportS60Tests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SeatMapPdfExportService _service;
        private readonly Guid _adminUserId = Guid.NewGuid();
        private readonly Guid _organizerOwnerId = Guid.NewGuid();
        private readonly Guid _otherOrganizerId = Guid.NewGuid();

        public SeatMapPdfExportS60Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _service = new SeatMapPdfExportService(_context);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private SeatMapExportController CreateController(Guid userId, string role)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var principal = new ClaimsPrincipal(identity);

            return new SeatMapExportController(
                _service,
                _context,
                NullLogger<SeatMapExportController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = principal }
                }
            };
        }

        private async Task<(Event ev, Showtime showtime, List<Seat> seats)> SeedShowtimeDataAsync(Guid ownerId)
        {
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Concert Đêm Nhạc Mùa Thu S-60",
                Description = "Sự kiện âm nhạc thử nghiệm xuất sơ đồ ghế PDF",
                Location = "Nhà hát Lớn Hà Nội",
                OwnerId = ownerId,
                CreatedAt = DateTime.UtcNow
            };

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                Name = "Đêm Diễn 1",
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(3),
                AvailableSeats = 10
            };

            var category = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Name = "VIP",
                Price = 500000
            };

            // Tạo danh sách 4 ghế: A1, A2, B1, B2
            var seats = new List<Seat>
            {
                new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "A", SeatNumber = 1, Status = "AVAILABLE" },
                new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "A", SeatNumber = 2, Status = "AVAILABLE" },
                new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "B", SeatNumber = 1, Status = "AVAILABLE" },
                new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, Row = "B", SeatNumber = 2, Status = "AVAILABLE" }
            };

            await _context.Events.AddAsync(ev);
            await _context.Showtimes.AddAsync(showtime);
            await _context.SeatCategories.AddAsync(category);
            await _context.Seats.AddRangeAsync(seats);
            await _context.SaveChangesAsync();

            return (ev, showtime, seats);
        }

        [Fact]
        public async Task GetSeatMapData_ThrowsKeyNotFoundException_WhenShowtimeDoesNotExist()
        {
            // Act & Assert
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                _service.GetSeatMapDataAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task GetSeatMapData_AccuratelyReflectsSeatStatuses_Sold_Held_Available_AndExpiredHold()
        {
            // Arrange
            var (ev, showtime, seats) = await SeedShowtimeDataAsync(_organizerOwnerId);
            var now = DateTime.UtcNow;

            var customerUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "customer_test",
                Email = "customer@example.com",
                PasswordHash = "hash"
            };
            await _context.Users.AddAsync(customerUser);

            // 1. Ghế seats[0] (A1): Đã BÁN (Order Paid)
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = customerUser.Id,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500000,
                CreatedAt = now.AddMinutes(-30)
            };
            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = seats[0].Id,
                Price = 500000
            };
            await _context.Orders.AddAsync(order);
            await _context.OrderItems.AddAsync(orderItem);

            // 2. Ghế seats[1] (A2): Đang GIỮ CHỖ CÒN HIỆU LỰC (ExpiresAt > now)
            var activeHold = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seats[1].Id,
                UserId = customerUser.Id,
                Status = "ACTIVE",
                HeldAt = now.AddMinutes(-5),
                ExpiresAt = now.AddMinutes(10) // còn 10 phút
            };
            await _context.SeatHold.AddAsync(activeHold);

            // 3. Ghế seats[2] (B1): Đã từng giữ chỗ nhưng ĐÃ HẾT HẠN (ExpiresAt < now) -> Phải là AVAILABLE
            var expiredHold = new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seats[2].Id,
                UserId = customerUser.Id,
                Status = "ACTIVE",
                HeldAt = now.AddMinutes(-20),
                ExpiresAt = now.AddMinutes(-5) // hết hạn cách đây 5 phút
            };
            await _context.SeatHold.AddAsync(expiredHold);

            // 4. Ghế seats[3] (B2): Không có giao dịch gì -> Phải là AVAILABLE
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetSeatMapDataAsync(showtime.Id, nowOverride: now);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(showtime.Id, result.ShowtimeId);
            Assert.Equal(ev.Title, result.EventTitle);
            Assert.Equal(4, result.TotalSeats);

            // Kiểm tra trạng thái từng ghế
            var seatA1 = result.Seats.First(s => s.Row == "A" && s.SeatNumber == 1);
            Assert.Equal("SOLD", seatA1.Status);

            var seatA2 = result.Seats.First(s => s.Row == "A" && s.SeatNumber == 2);
            Assert.Equal("HELD", seatA2.Status);

            var seatB1 = result.Seats.First(s => s.Row == "B" && s.SeatNumber == 1);
            Assert.Equal("AVAILABLE", seatB1.Status); // Hết hạn hold -> AVAILABLE

            var seatB2 = result.Seats.First(s => s.Row == "B" && s.SeatNumber == 2);
            Assert.Equal("AVAILABLE", seatB2.Status);

            // Thống kê
            Assert.Equal(1, result.SoldCount);
            Assert.Equal(1, result.HeldCount);
            Assert.Equal(2, result.AvailableCount);
            Assert.Equal(25.0, result.SoldPercentage);
            Assert.Equal(25.0, result.HeldPercentage);
            Assert.Equal(50.0, result.AvailablePercentage);
        }

        [Fact]
        public async Task GenerateSeatMapPdf_ProducesValidPdfBytes_WithMagicHeader()
        {
            // Arrange
            var (ev, showtime, seats) = await SeedShowtimeDataAsync(_organizerOwnerId);
            var data = await _service.GetSeatMapDataAsync(showtime.Id);

            // Act
            var pdfBytes = _service.GenerateSeatMapPdf(data);

            // Assert
            Assert.NotNull(pdfBytes);
            Assert.True(pdfBytes.Length > 0, "Dung lượng tệp PDF xuất ra phải lớn hơn 0");

            // Kiểm tra Magic Bytes tiêu chuẩn của PDF: "%PDF-"
            var pdfHeader = Encoding.ASCII.GetString(pdfBytes.Take(5).ToArray());
            Assert.Equal("%PDF-", pdfHeader);
        }

        [Fact]
        public async Task ExportSeatMapPdf_ViaController_ReturnsFileContentResult_WithPdfContentType()
        {
            // Arrange
            var (ev, showtime, seats) = await SeedShowtimeDataAsync(_organizerOwnerId);
            var controller = CreateController(_organizerOwnerId, "Organizer");

            // Act
            var actionResult = await controller.ExportSeatMapPdf(showtime.Id);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(actionResult);
            Assert.Equal("application/pdf", fileResult.ContentType);
            Assert.StartsWith("so-do-ghe-", fileResult.FileDownloadName);
            Assert.EndsWith(".pdf", fileResult.FileDownloadName);
            Assert.True(fileResult.FileContents.Length > 0);

            var pdfHeader = Encoding.ASCII.GetString(fileResult.FileContents.Take(5).ToArray());
            Assert.Equal("%PDF-", pdfHeader);
        }

        [Fact]
        public async Task ExportSeatMapPdf_ViaController_ReturnsForbidden_WhenOrganizerDoesNotOwnEvent()
        {
            // Arrange
            var (ev, showtime, seats) = await SeedShowtimeDataAsync(_organizerOwnerId);
            var controller = CreateController(_otherOrganizerId, "Organizer"); // Khác ownerId

            // Act
            var actionResult = await controller.ExportSeatMapPdf(showtime.Id);

            // Assert
            var objResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);
        }

        [Fact]
        public async Task ExportSeatMapPdf_ViaController_AllowsAdmin_RegardlessOfEventOwner()
        {
            // Arrange
            var (ev, showtime, seats) = await SeedShowtimeDataAsync(_organizerOwnerId);
            var controller = CreateController(_adminUserId, "Admin"); // Admin có quyền truy cập toàn bộ

            // Act
            var actionResult = await controller.ExportSeatMapPdf(showtime.Id);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(actionResult);
            Assert.Equal("application/pdf", fileResult.ContentType);
        }

        [Fact]
        public void GenerateSeatMapPdf_StressTest_3000Seats_FitsExactlyOnePage()
        {
            // Arrange: Giả lập 3.000 ghế (50 hàng x 60 cột)
            var seats = new List<SeatMapExportItemDto>();
            int cols = 60;
            int rows = 50; // 50 * 60 = 3000 seats

            for (int r = 0; r < rows; r++)
            {
                // Đặt tên hàng: A..Z, sau đó AA..AZ
                string rowName = r < 26 
                    ? ((char)('A' + r)).ToString() 
                    : $"A{(char)('A' + (r - 26))}";

                for (int c = 1; c <= cols; c++)
                {
                    string status = (r * cols + c) % 4 == 0 ? "SOLD" : ((r * cols + c) % 5 == 0 ? "HELD" : "AVAILABLE");
                    seats.Add(new SeatMapExportItemDto
                    {
                        Id = Guid.NewGuid(),
                        Row = rowName,
                        SeatNumber = c,
                        CategoryName = "Standard",
                        Price = 250000,
                        Status = status
                    });
                }
            }

            var data = new SeatMapExportDataDto
            {
                ShowtimeId = Guid.NewGuid(),
                ShowtimeName = "Suất diễn Quy mô Lớn (3.000 Chỗ)",
                StartTime = DateTime.UtcNow.AddDays(10),
                EndTime = DateTime.UtcNow.AddDays(10).AddHours(4),
                EventTitle = "Sân Vận Động Mỹ Đình - Mega Concert 3000 Chỗ",
                Location = "Sân Vận Động Quốc Gia Mỹ Đình, Hà Nội",
                Seats = seats
            };

            // Act: Render PDF
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var pdfBytes = _service.GenerateSeatMapPdf(data);
            stopwatch.Stop();

            // Assert
            Assert.NotNull(pdfBytes);
            Assert.True(pdfBytes.Length > 0, "Dung lượng file PDF 3000 ghế phải lớn hơn 0");
            Assert.True(stopwatch.ElapsedMilliseconds < 10000, $"Thời gian kết xuất 3000 ghế phải < 10s (thực tế: {stopwatch.ElapsedMilliseconds}ms)");

            // Kiểm tra số trang trong PDF: Đúng 1 trang duy nhất (/Type /Page)
            var pdfContentString = Encoding.ASCII.GetString(pdfBytes);
            int pageCount = System.Text.RegularExpressions.Regex.Matches(pdfContentString, @"/Type\s*/Page\b").Count;
            Assert.Equal(1, pageCount);
        }
    }
}
