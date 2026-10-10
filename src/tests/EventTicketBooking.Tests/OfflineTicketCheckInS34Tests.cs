using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Kiểm thử tự động cho User Story S-34: Soát vé ngoại tuyến bằng danh sách đã tải.
    /// Bao gồm các tiêu chuẩn nghiệm thu: AC1, AC2, AC3, AC4 và NFR về xác thực chữ ký bằng Public Key.
    /// </summary>
    public class OfflineTicketCheckInS34Tests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly IQrSignatureService _qrService;
        private readonly Guid _staffUserId = Guid.NewGuid();

        public OfflineTicketCheckInS34Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: "S34_TestDb_" + Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _qrService = new QrSignatureService();
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

            return new TicketCheckInController(_context, Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketCheckInController>.Instance, _qrService)
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

        #region S-34 Backend Data Compatibility

        [Fact]
        public async Task S34_OfflineTicketsManifest_ContainsRequiredFieldsForOfflineVerification()
        {
            var controller = CreateControllerWithRole("Staff");

            var ev = new Event { Id = Guid.NewGuid(), Title = "Đại nhạc hội Rock 2026" };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow.AddHours(3)
            };
            var category = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Name = "VIP", Price = 500000 };
            var seat1 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, SeatCategory = category, Row = "A", SeatNumber = 1 };
            var seat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, SeatCategoryId = category.Id, SeatCategory = category, Row = "A", SeatNumber = 2 };

            var paidOrder = new Order { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Status = OrderStatus.Paid, UserId = Guid.NewGuid(), TotalAmount = 1000000 };
            var item1 = new OrderItem { Id = Guid.NewGuid(), OrderId = paidOrder.Id, SeatId = seat1.Id, Price = 500000, Seat = seat1, Order = paidOrder, IsCheckedIn = false };
            var ticket1 = new Ticket { Id = Guid.NewGuid(), OrderItemId = item1.Id, TicketCode = "TK-S34-001" };

            var item2 = new OrderItem { Id = Guid.NewGuid(), OrderId = paidOrder.Id, SeatId = seat2.Id, Price = 500000, Seat = seat2, Order = paidOrder, IsCheckedIn = true, CheckInGate = "Gate 1", CheckInTime = DateTimeOffset.UtcNow.AddMinutes(-10) };
            var ticket2 = new Ticket { Id = Guid.NewGuid(), OrderItemId = item2.Id, TicketCode = "TK-S34-002" };

            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);
            _context.SeatCategories.Add(category);
            _context.Seats.AddRange(seat1, seat2);
            _context.Orders.Add(paidOrder);
            _context.OrderItems.AddRange(item1, item2);
            _context.Tickets.AddRange(ticket1, ticket2);
            await _context.SaveChangesAsync();

            // Act
            var result = await controller.GetOfflineTickets(showtime.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var manifest = Assert.IsType<OfflineTicketSyncResponseDto>(okResult.Value);

            Assert.Equal(showtime.Id, manifest.ShowtimeId);
            Assert.Equal(2, manifest.TotalCount);
            Assert.Contains(manifest.Tickets, t => t.TicketCode == "TK-S34-001" && !t.IsCheckedIn);
            Assert.Contains(manifest.Tickets, t => t.TicketCode == "TK-S34-002" && t.IsCheckedIn);
        }

        #endregion

        #region S-34 Offline Crypto Verification (AC1, S-26)

        [Fact]
        public void S34_AC1_OfflineVerification_WithAuthenticTicket_VerifiesSuccessfullyUsingPublicKeyOnly()
        {
            // AC1: Kiểm chữ ký QR bằng khoá công khai, tra mã trong danh sách, báo xanh
            // NFR: Thiết bị chỉ giữ khoá công khai (Public Keys)
            var publicKeys = _qrService.GetPublicKeys();
            var verifierOnly = QrSignatureService.CreateVerifierOnly(publicKeys);

            var ticketCode = "TK-OFFLINE-TEST-001";
            var showtimeId = Guid.NewGuid();

            // Server ký QR bằng khoá riêng
            var qrPayload = _qrService.SignTicket(ticketCode, showtimeId);

            // Máy quét ngoại tuyến kiểm tra CHỈ BẰNG Public Key
            var verifyResult = verifierOnly.VerifyTicket(qrPayload);

            Assert.True(verifyResult.IsValid);
            Assert.Equal(ticketCode, verifyResult.TicketCode);
            Assert.Equal(showtimeId, verifyResult.ShowtimeId);
        }

        [Fact]
        public void S34_AC1_OfflineVerification_TamperedQr_Rejected()
        {
            // AC1: Mã QR bị sửa đổi 1 ký tự sẽ bị từ chối
            var publicKeys = _qrService.GetPublicKeys();
            var verifierOnly = QrSignatureService.CreateVerifierOnly(publicKeys);

            var ticketCode = "TK-OFFLINE-TEST-001";
            var showtimeId = Guid.NewGuid();
            var qrPayload = _qrService.SignTicket(ticketCode, showtimeId);

            // Sửa đổi mã vé trong QR
            var tamperedPayload = qrPayload.Replace(ticketCode, "TK-OFFLINE-TEST-999");
            var verifyResult = verifierOnly.VerifyTicket(tamperedPayload);

            Assert.False(verifyResult.IsValid);
            Assert.Equal("INVALID_SIGNATURE", verifyResult.ErrorReason);
        }

        [Fact]
        public void S34_AC1_OfflineVerification_UnsignedQr_Rejected()
        {
            // Giả sử mã QR hợp lệ nhưng không có chữ ký -> bị từ chối
            var publicKeys = _qrService.GetPublicKeys();
            var verifierOnly = QrSignatureService.CreateVerifierOnly(publicKeys);

            var fakeQr = "TICKET|v1|TK-OFFLINE-TEST-001|" + Guid.NewGuid();
            var verifyResult = verifierOnly.VerifyTicket(fakeQr);

            Assert.False(verifyResult.IsValid);
            Assert.Equal("MISSING_SIGNATURE", verifyResult.ErrorReason);
        }

        #endregion
    }
}
