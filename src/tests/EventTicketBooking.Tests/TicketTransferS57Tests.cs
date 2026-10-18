using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class TicketTransferS57Tests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly ITicketService _ticketService;
        private readonly IQrSignatureService _qrSignatureService;
        private readonly ITicketTransferService _transferService;

        private readonly User _senderUser;
        private readonly User _recipientUser;
        private readonly Event _event;
        private readonly Showtime _showtime;
        private readonly SeatCategory _category;
        private readonly Seat _seat;
        private readonly Order _order;
        private readonly OrderItem _orderItem;
        private readonly Ticket _ticket;

        public TicketTransferS57Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: "TicketTransferS57Db_" + Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _qrSignatureService = new QrSignatureService();
            _ticketService = new TicketService(_qrSignatureService);
            _transferService = new TicketTransferService(
                _context,
                _ticketService,
                NullLogger<TicketTransferService>.Instance);

            // Seed Users
            _senderUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "sender",
                Email = "sender@example.com",
                FullName = "Nguyễn Văn Người Gửi",
                IsActive = true
            };

            _recipientUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "recipient",
                Email = "recipient@example.com",
                FullName = "Trần Thị Người Nhận",
                IsActive = true
            };

            // Seed Event with transfer allowed, cutoff 2 hours
            _event = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Hòa Nhạc Mùa Thu 2026",
                Location = "Nhà Hát Lớn Hà Nội",
                StartTime = DateTime.UtcNow.AddDays(7),
                EndTime = DateTime.UtcNow.AddDays(7).AddHours(3),
                TotalSeats = 100,
                AllowTicketTransfer = true,
                TransferCutoffHours = 2,
                OwnerId = _senderUser.Id
            };

            _showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = _event.Id,
                StartTime = DateTime.UtcNow.AddDays(7),
                EndTime = DateTime.UtcNow.AddDays(7).AddHours(3),
                AvailableSeats = 100
            };

            _category = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = _showtime.Id,
                Name = "VIP",
                Price = 500000
            };

            _seat = new Seat
            {
                Id = Guid.NewGuid(),
                SeatCategoryId = _category.Id,
                Row = "A",
                SeatNumber = 1
            };

            // Seed Order & Ticket
            _order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = _senderUser.Id,
                ShowtimeId = _showtime.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500000,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            };

            _orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = _order.Id,
                SeatId = _seat.Id,
                Price = 500000,
                IsCheckedIn = false
            };

            _ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = _orderItem.Id,
                TicketCode = "TK-ABCD-1234-EFGH-5678",
                CreatedAt = DateTimeOffset.UtcNow,
                CurrentUserId = _senderUser.Id,
                TransferCount = 0
            };

            _orderItem.Ticket = _ticket;
            _order.OrderItems.Add(_orderItem);

            _context.Users.AddRange(_senderUser, _recipientUser);
            _context.Events.Add(_event);
            _context.Showtimes.Add(_showtime);
            _context.SeatCategories.Add(_category);
            _context.Seats.Add(_seat);
            _context.Orders.Add(_order);
            _context.OrderItems.Add(_orderItem);
            _context.Tickets.Add(_ticket);
            _context.SaveChanges();
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private TicketsController CreateTicketsController(Guid userId, string role = "Customer")
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var principal = new ClaimsPrincipal(identity);

            var httpContext = new DefaultHttpContext { User = principal };
            return new TicketsController(_qrSignatureService, _transferService)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        }

        [Fact]
        public async Task TransferTicket_Success_HappyPath()
        {
            // Arrange
            var controller = CreateTicketsController(_senderUser.Id);
            var oldTicketCode = _ticket.TicketCode;
            var request = new TransferTicketRequestDto
            {
                RecipientEmail = "recipient@example.com",
                Note = "Tặng vé xem kịch nhé bạn!"
            };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(okResult.Value);
            Assert.True(response.Success);
            Assert.NotNull(response.Data);

            Assert.Equal(oldTicketCode, response.Data.OldTicketCode);
            Assert.NotEqual(oldTicketCode, response.Data.NewTicketCode);
            Assert.Equal("sender@example.com", response.Data.FromEmail);
            Assert.Equal("recipient@example.com", response.Data.ToEmail);

            // Kiểm tra DB
            var updatedTicket = await _context.Tickets.FindAsync(_ticket.Id);
            Assert.NotNull(updatedTicket);
            Assert.Equal(_recipientUser.Id, updatedTicket.CurrentUserId);
            Assert.Equal(response.Data.NewTicketCode, updatedTicket.TicketCode);
            Assert.Equal(1, updatedTicket.TransferCount);
            Assert.NotNull(updatedTicket.LastTransferredAt);

            // Kiểm tra bản ghi TicketTransfer
            var transferRecord = await _context.TicketTransfers.FirstOrDefaultAsync(tt => tt.TicketId == _ticket.Id);
            Assert.NotNull(transferRecord);
            Assert.Equal(_senderUser.Id, transferRecord.FromUserId);
            Assert.Equal(_recipientUser.Id, transferRecord.ToUserId);
            Assert.Equal(oldTicketCode, transferRecord.OldTicketCode);
            Assert.Equal(response.Data.NewTicketCode, transferRecord.NewTicketCode);
            Assert.Equal("Tặng vé xem kịch nhé bạn!", transferRecord.Note);

            // Kiểm tra OrderAuditLog
            var auditLog = await _context.OrderAuditLogs.FirstOrDefaultAsync(l => l.OrderId == _order.Id && l.Action == "TICKET_TRANSFERRED");
            Assert.NotNull(auditLog);
            Assert.Equal("TICKET", auditLog.EntityType);
            Assert.Equal(oldTicketCode, auditLog.OldStatus);
            Assert.Equal(response.Data.NewTicketCode, auditLog.NewStatus);
        }

        [Fact]
        public async Task TransferTicket_RecipientNotFound_ReturnsBadRequest()
        {
            // Arrange
            var controller = CreateTicketsController(_senderUser.Id);
            var request = new TransferTicketRequestDto
            {
                RecipientEmail = "nonexistent@example.com"
            };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(badRequest.Value);
            Assert.False(response.Success);
            Assert.Contains("không tồn tại", response.Message);
        }

        [Fact]
        public async Task TransferTicket_ToSelf_ReturnsBadRequest()
        {
            // Arrange
            var controller = CreateTicketsController(_senderUser.Id);
            var request = new TransferTicketRequestDto
            {
                RecipientEmail = "sender@example.com"
            };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(badRequest.Value);
            Assert.False(response.Success);
            Assert.Contains("chính mình", response.Message);
        }

        [Fact]
        public async Task TransferTicket_NotOwner_ReturnsForbidden()
        {
            // Arrange: Một người khác không sở hữu vé gọi transfer
            var thirdUser = new User { Id = Guid.NewGuid(), Username = "stranger", Email = "stranger@example.com", IsActive = true };
            _context.Users.Add(thirdUser);
            await _context.SaveChangesAsync();

            var controller = CreateTicketsController(thirdUser.Id);
            var request = new TransferTicketRequestDto
            {
                RecipientEmail = "recipient@example.com"
            };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status403Forbidden, statusCodeResult.StatusCode);
        }

        [Fact]
        public async Task TransferTicket_OrderNotPaid_ReturnsBadRequest()
        {
            // Arrange
            _order.Status = OrderStatus.Pending;
            await _context.SaveChangesAsync();

            var controller = CreateTicketsController(_senderUser.Id);
            var request = new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(badRequest.Value);
            Assert.False(response.Success);
            Assert.Contains("đã thanh toán", response.Message);
        }

        [Fact]
        public async Task TransferTicket_TicketAlreadyCheckedIn_ReturnsBadRequest()
        {
            // Arrange
            _orderItem.IsCheckedIn = true;
            await _context.SaveChangesAsync();

            var controller = CreateTicketsController(_senderUser.Id);
            var request = new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(badRequest.Value);
            Assert.False(response.Success);
            Assert.Contains("soát vào cửa", response.Message);
        }

        [Fact]
        public async Task TransferTicket_EventDoesNotAllowTransfer_ReturnsBadRequest()
        {
            // Arrange
            _event.AllowTicketTransfer = false;
            await _context.SaveChangesAsync();

            var controller = CreateTicketsController(_senderUser.Id);
            var request = new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(badRequest.Value);
            Assert.False(response.Success);
            Assert.Contains("không cho phép chuyển nhượng", response.Message);
        }

        [Fact]
        public async Task TransferTicket_PastCutoffTime_ReturnsBadRequest()
        {
            // Arrange: Suất diễn diễn ra trong 1 tiếng nữa, trong khi cutoff là 2 tiếng
            _showtime.StartTime = DateTime.UtcNow.AddHours(1);
            await _context.SaveChangesAsync();

            var controller = CreateTicketsController(_senderUser.Id);
            var request = new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(badRequest.Value);
            Assert.False(response.Success);
            Assert.Contains("hết thời hạn chuyển nhượng", response.Message);
        }

        [Fact]
        public async Task TransferTicket_ExceedMaxTransfers_ReturnsBadRequest()
        {
            // Arrange: Vé đã chuyển 1 lần
            _ticket.TransferCount = 1;
            await _context.SaveChangesAsync();

            var controller = CreateTicketsController(_senderUser.Id);
            var request = new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" };

            // Act
            var actionResult = await controller.TransferTicket(_ticket.Id, request);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<TicketTransferResultDto>>(badRequest.Value);
            Assert.False(response.Success);
            Assert.Contains("tối đa 1 lần", response.Message);
        }

        [Fact]
        public async Task CheckIn_OldTicketCode_ReturnsTicketTransferredError()
        {
            // Arrange: Chuyển nhượng vé trước
            var oldTicketCode = _ticket.TicketCode;
            var transferResult = await _transferService.TransferTicketAsync(
                _ticket.Id,
                _senderUser.Id,
                new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" });
            Assert.True(transferResult.Success);

            // Tạo controller Check-in cho nhân viên Staff
            var staffUser = new User { Id = Guid.NewGuid(), Username = "staff", Email = "staff@example.com", IsActive = true };
            _context.Users.Add(staffUser);
            await _context.SaveChangesAsync();

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, staffUser.Id.ToString()),
                new Claim(ClaimTypes.Role, "Staff")
            };
            var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestStaff")) };
            var checkInController = new TicketCheckInController(_context, _qrSignatureService)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };

            // Act: Quét bằng mã vé cũ (oldTicketCode)
            var scanRequest = new TicketScanRequestDto
            {
                TicketCode = oldTicketCode,
                SelectedShowtimeId = _showtime.Id,
                GateName = "Cổng A"
            };

            var scanResult = await checkInController.ScanTicket(scanRequest);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(scanResult);
            var valStr = badRequest.Value?.ToString() ?? "";
            Assert.Contains("TICKET_TRANSFERRED", valStr);
        }

        [Fact]
        public async Task GetMyTickets_ShowsTransferredForSender_AndNewTicketForRecipient()
        {
            // Arrange: Chuyển vé từ Sender sang Recipient
            await _transferService.TransferTicketAsync(
                _ticket.Id,
                _senderUser.Id,
                new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" });

            // Act 1: Sender kiểm tra vé của mình
            var senderController = CreateTicketsController(_senderUser.Id);
            var senderResult = await senderController.GetMyTickets();
            var senderOk = Assert.IsType<OkObjectResult>(senderResult);
            var senderResp = Assert.IsType<ApiResponse<List<TicketDto>>>(senderOk.Value);
            Assert.Single(senderResp.Data!);

            var senderTicket = senderResp.Data![0];
            Assert.True(senderTicket.IsTransferred);
            Assert.Equal("recipient@example.com", senderTicket.TransferredToEmail);
            Assert.Empty(senderTicket.QrPayload); // Không hiển thị mã QR cho người gửi cũ
            Assert.False(senderTicket.CanTransfer);

            // Act 2: Recipient kiểm tra vé của mình
            var recipientController = CreateTicketsController(_recipientUser.Id);
            var recipientResult = await recipientController.GetMyTickets();
            var recipientOk = Assert.IsType<OkObjectResult>(recipientResult);
            var recipientResp = Assert.IsType<ApiResponse<List<TicketDto>>>(recipientOk.Value);
            Assert.Single(recipientResp.Data!);

            var recipientTicket = recipientResp.Data![0];
            Assert.False(recipientTicket.IsTransferred);
            Assert.Equal("sender@example.com", recipientTicket.TransferredFromEmail);
            Assert.NotEmpty(recipientTicket.QrPayload); // Có mã QR mới hợp lệ
            Assert.False(recipientTicket.CanTransfer); // Đã chuyển 1 lần, không thể chuyển tiếp
        }

        [Fact]
        public async Task GetTicketTransferHistory_ReturnsCorrectHistory()
        {
            // Arrange
            await _transferService.TransferTicketAsync(
                _ticket.Id,
                _senderUser.Id,
                new TransferTicketRequestDto { RecipientEmail = "recipient@example.com", Note = "Ghi chú test" });

            var controller = CreateTicketsController(_senderUser.Id);

            // Act
            var actionResult = await controller.GetTicketTransfers(_ticket.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<List<TicketTransferHistoryDto>>>(okResult.Value);
            Assert.True(response.Success);
            Assert.Single(response.Data!);
            var history = response.Data![0];
            Assert.Equal("sender@example.com", history.FromEmail);
            Assert.Equal("recipient@example.com", history.ToEmail);
            Assert.Equal("Ghi chú test", history.Note);
        }

        [Theory]
        [InlineData("Recipient@Example.com")]
        [InlineData("  RECIPIENT@EXAMPLE.COM  ")]
        [InlineData("recipient@EXAMPLE.COM")]
        public async Task TransferTicket_CaseInsensitiveEmail_SuccessfullyMapsRecipient(string inputEmail)
        {
            // Act: Chuyển vé với email có chữ hoa / thường / khoảng trắng thừa
            var result = await _transferService.TransferTicketAsync(
                _ticket.Id,
                _senderUser.Id,
                new TransferTicketRequestDto { RecipientEmail = inputEmail });

            // Assert
            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Data);
            Assert.Equal("recipient@example.com", result.Data.ToEmail);

            var dbTicket = await _context.Tickets.FindAsync(_ticket.Id);
            Assert.NotNull(dbTicket);
            Assert.Equal(_recipientUser.Id, dbTicket.CurrentUserId);
        }

        [Fact]
        public async Task ConcurrentCheckInAndTransfer_HandledSafely_PreventsInconsistentState()
        {
            // Trường hợp 1: Nhân viên đã quét vé tại cổng (IsCheckedIn = true) đúng thời điểm request chuyển vé gửi tới
            _orderItem.IsCheckedIn = true;
            _orderItem.CheckInTime = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            var failTransferResult = await _transferService.TransferTicketAsync(
                _ticket.Id,
                _senderUser.Id,
                new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" });

            Assert.False(failTransferResult.Success);
            Assert.Contains("soát vào cửa", failTransferResult.Message);

            // Reset trạng thái vé chưa check-in để test trường hợp chuyển vé thành công trước khi quét vé tại cổng
            _orderItem.IsCheckedIn = false;
            _orderItem.CheckInTime = null;
            await _context.SaveChangesAsync();

            var oldTicketCode = _ticket.TicketCode;
            var successTransferResult = await _transferService.TransferTicketAsync(
                _ticket.Id,
                _senderUser.Id,
                new TransferTicketRequestDto { RecipientEmail = "recipient@example.com" });

            Assert.True(successTransferResult.Success);
            var newTicketCode = successTransferResult.Data!.NewTicketCode;

            // Thiết lập controller soát vé cho nhân viên
            var staffUser = new User { Id = Guid.NewGuid(), Username = "gate_staff", Email = "gatestaff@example.com", IsActive = true };
            _context.Users.Add(staffUser);
            await _context.SaveChangesAsync();

            var staffContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, staffUser.Id.ToString()),
                        new Claim(ClaimTypes.Role, "Staff")
                    }, "StaffAuth"))
                }
            };
            var checkInController = new TicketCheckInController(_context, _qrSignatureService)
            {
                ControllerContext = staffContext
            };

            // Quét mã vé cũ (oldTicketCode) -> Bị từ chối ngay với TICKET_TRANSFERRED
            var scanOldResult = await checkInController.ScanTicket(new TicketScanRequestDto
            {
                TicketCode = oldTicketCode,
                SelectedShowtimeId = _showtime.Id,
                GateName = "Cổng A"
            });
            var badRequest = Assert.IsType<BadRequestObjectResult>(scanOldResult);
            Assert.Contains("TICKET_TRANSFERRED", badRequest.Value?.ToString() ?? "");

            // Quét mã vé mới (newTicketCode) do người nhận sở hữu -> Cho phép vào cửa thành công
            var scanNewResult = await checkInController.ScanTicket(new TicketScanRequestDto
            {
                TicketCode = newTicketCode,
                SelectedShowtimeId = _showtime.Id,
                GateName = "Cổng A"
            });
            var okScan = Assert.IsType<OkObjectResult>(scanNewResult);
            Assert.NotNull(okScan.Value);

            var reloadedOrderItem = await _context.OrderItems.FindAsync(_orderItem.Id);
            Assert.True(reloadedOrderItem!.IsCheckedIn);
        }

        [Fact]
        public async Task TransferredTicket_HistoryAndVisibilityRules_EnforcedCorrectly()
        {
            // Arrange
            var transferResult = await _transferService.TransferTicketAsync(
                _ticket.Id,
                _senderUser.Id,
                new TransferTicketRequestDto { RecipientEmail = "recipient@example.com", Note = "Tặng bạn vé xem hòa nhạc" });

            Assert.True(transferResult.Success);

            // 1. Phía người nhận (Recipient):
            // - Xem được nguồn gốc vé (TransferredFromEmail)
            // - Có QR code hợp lệ để vào cổng
            // - Thấy rõ chi tiết ghế ngồi (hàng ghế, số ghế, hạng vé, giá vé)
            var recipientTicketsController = CreateTicketsController(_recipientUser.Id);
            var recipientTicketsResponse = await recipientTicketsController.GetMyTickets();
            var recipientOk = Assert.IsType<OkObjectResult>(recipientTicketsResponse);
            var recipientData = Assert.IsType<ApiResponse<List<TicketDto>>>(recipientOk.Value).Data!;
            Assert.Single(recipientData);
            var recipientTicket = recipientData[0];

            Assert.Equal("sender@example.com", recipientTicket.TransferredFromEmail);
            Assert.NotEmpty(recipientTicket.QrPayload);
            Assert.Equal("A", recipientTicket.SeatRow);
            Assert.Equal(1, recipientTicket.SeatNumber);
            Assert.Equal("VIP", recipientTicket.CategoryName);
            Assert.Equal(500000, recipientTicket.Price);

            // - Người nhận có quyền xem lịch sử chuyển nhượng của vé
            var recipientHistoryResponse = await recipientTicketsController.GetTicketTransfers(_ticket.Id);
            var recipientHistoryOk = Assert.IsType<OkObjectResult>(recipientHistoryResponse);
            var historyData = Assert.IsType<ApiResponse<List<TicketTransferHistoryDto>>>(recipientHistoryOk.Value).Data!;
            Assert.Single(historyData);
            Assert.Equal("sender@example.com", historyData[0].FromEmail);
            Assert.Equal("recipient@example.com", historyData[0].ToEmail);
            Assert.Equal("Tặng bạn vé xem hòa nhạc", historyData[0].Note);

            // 2. Phía người gửi (Sender):
            // - Trong API MyTickets: Vẫn thấy chi tiết ghế đã chuyển, nhưng KHÔNG còn thấy mã QR và TicketCode là "ĐÃ CHUYỂN NHƯỢNG"
            var senderTicketsController = CreateTicketsController(_senderUser.Id);
            var senderTicketsResponse = await senderTicketsController.GetMyTickets();
            var senderOk = Assert.IsType<OkObjectResult>(senderTicketsResponse);
            var senderData = Assert.IsType<ApiResponse<List<TicketDto>>>(senderOk.Value).Data!;
            Assert.Single(senderData);
            var senderTicket = senderData[0];

            Assert.True(senderTicket.IsTransferred);
            Assert.Equal("recipient@example.com", senderTicket.TransferredToEmail);
            Assert.Empty(senderTicket.QrPayload); // Tuyệt đối không hiển thị QR cho người gửi
            Assert.Equal("ĐÃ CHUYỂN NHƯỢNG", senderTicket.TicketCode);
            Assert.Equal("A", senderTicket.SeatRow); // Vẫn thấy thông tin hàng ghế
            Assert.Equal(1, senderTicket.SeatNumber); // Vẫn thấy số ghế
            Assert.Equal("VIP", senderTicket.CategoryName); // Vẫn thấy hạng vé
            Assert.Equal(500000, senderTicket.Price); // Vẫn thấy giá vé

            // - Trong API GetOrderTickets của đơn hàng gốc (OrdersController):
            var ordersController = new OrdersController(_context, NullLogger<OrdersController>.Instance, null, _ticketService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, _senderUser.Id.ToString()),
                            new Claim(ClaimTypes.Role, "Customer")
                        }, "TestAuth"))
                    }
                }
            };

            var orderTicketsResult = await ordersController.GetOrderTickets(_order.Id);
            var orderOk = Assert.IsType<OkObjectResult>(orderTicketsResult);
            var orderTickets = Assert.IsType<ApiResponse<List<TicketDto>>>(orderOk.Value).Data!;
            Assert.Single(orderTickets);
            var orderTicket = orderTickets[0];

            Assert.True(orderTicket.IsTransferred);
            Assert.Empty(orderTicket.QrPayload); // Không có QR
            Assert.Equal("ĐÃ CHUYỂN NHƯỢNG", orderTicket.TicketCode);
            Assert.Equal("A", orderTicket.SeatRow);
            Assert.Equal(1, orderTicket.SeatNumber);
            Assert.Equal(500000, orderTicket.Price);
        }
    }
}
