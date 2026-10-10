using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Implementations;
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
    public class WaitingRoomS40Tests
    {
        private readonly WaitingRoomOptions _defaultOptions = new WaitingRoomOptions
        {
            Enabled = true,
            DefaultActiveThreshold = 2,
            PassDurationMinutes = 10,
            HeartbeatTimeoutSeconds = 45,
            MaxQueueCapacity = 100
        };

        private WaitingRoomService CreateService(WaitingRoomOptions? options = null)
        {
            var opt = Options.Create(options ?? _defaultOptions);
            return new WaitingRoomService(opt, NullLogger<WaitingRoomService>.Instance);
        }

        [Fact]
        public async Task JoinQueue_WhenUnderThreshold_AdmitsImmediatelyWithValidPassToken()
        {
            // Arrange
            var service = CreateService();
            var showtimeId = Guid.NewGuid();

            // Act
            var result = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_1");

            // Assert
            Assert.Equal("ADMITTED", result.Status);
            Assert.Equal(0, result.Position);
            Assert.Equal(0, result.PeopleAhead);
            Assert.False(string.IsNullOrWhiteSpace(result.AccessPassToken));
            Assert.NotNull(result.PassExpiresAt);

            // Kiểm tra token có hợp lệ
            bool isValid = await service.ValidatePassTokenAsync(showtimeId, result.AccessPassToken!);
            Assert.True(isValid);
        }

        [Fact]
        public async Task JoinQueue_WhenExceedsThreshold_EnqueuesWithFifoPosition()
        {
            // Arrange (Threshold = 2)
            var service = CreateService();
            var showtimeId = Guid.NewGuid();

            // 2 người đầu vào thẳng
            var user1 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_1");
            var user2 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_2");
            Assert.Equal("ADMITTED", user1.Status);
            Assert.Equal("ADMITTED", user2.Status);

            // Act: Người thứ 3 và 4 vào -> Phải vào hàng chờ FIFO
            var user3 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_3");
            var user4 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_4");

            // Assert
            Assert.Equal("WAITING", user3.Status);
            Assert.Equal(1, user3.Position);
            Assert.Equal(0, user3.PeopleAhead);

            Assert.Equal("WAITING", user4.Status);
            Assert.Equal(2, user4.Position);
            Assert.Equal(1, user4.PeopleAhead);

            // Stats
            var stats = await service.GetStatsAsync(showtimeId);
            Assert.Equal(2, stats.ActiveCount);
            Assert.Equal(2, stats.QueueLength);
            Assert.True(stats.IsWaitingRoomActive);
        }

        [Fact]
        public async Task Heartbeat_WhenActiveUserReleasesPass_PromotesFirstInQueue()
        {
            // Arrange
            var service = CreateService();
            var showtimeId = Guid.NewGuid();

            var user1 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_1");
            var user2 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_2");
            var user3 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_3"); // Waiting pos 1

            Assert.Equal("WAITING", user3.Status);

            // Act: User 1 giải phóng pass (đã đặt vé xong hoặc thoát trang)
            await service.ReleaseActivePassAsync(showtimeId, user1.AccessPassToken!);

            // User 3 gửi heartbeat
            var user3Updated = await service.HeartbeatAsync(showtimeId, user3.QueueToken);

            // Assert: User 3 phải được tự động promote sang ADMITTED
            Assert.NotNull(user3Updated);
            Assert.Equal("ADMITTED", user3Updated!.Status);
            Assert.False(string.IsNullOrWhiteSpace(user3Updated.AccessPassToken));

            // Token của User 3 có thể sử dụng ngay
            bool valid = await service.ValidatePassTokenAsync(showtimeId, user3Updated.AccessPassToken!);
            Assert.True(valid);
        }

        [Fact]
        public async Task LeaveQueue_RemovesTicket_AndShiftsQueueForward()
        {
            // Arrange
            var service = CreateService();
            var showtimeId = Guid.NewGuid();

            await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "c1"); // Admitted
            await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "c2"); // Admitted

            var user3 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "c3"); // Waiting pos 1
            var user4 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "c4"); // Waiting pos 2

            // Act: User 3 chủ động rời hàng chờ
            bool left = await service.LeaveQueueAsync(showtimeId, user3.QueueToken);
            Assert.True(left);

            // User 4 gửi heartbeat
            var user4Status = await service.HeartbeatAsync(showtimeId, user4.QueueToken);

            // Assert: User 4 được đôn lên vị trí #1
            Assert.NotNull(user4Status);
            Assert.Equal(1, user4Status!.Position);
            Assert.Equal(0, user4Status.PeopleAhead);
        }

        [Fact]
        public async Task ValidatePassToken_WhenTokenExpired_ReturnsFalse()
        {
            // Arrange: Cấu hình pass hết hạn ngay
            var shortOptions = new WaitingRoomOptions
            {
                Enabled = true,
                DefaultActiveThreshold = 5,
                PassDurationMinutes = -1 // Đã hết hạn ngay khi tạo
            };
            var service = CreateService(shortOptions);
            var showtimeId = Guid.NewGuid();

            var status = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "c1");
            Assert.Equal("ADMITTED", status.Status);

            // Act
            bool isValid = await service.ValidatePassTokenAsync(showtimeId, status.AccessPassToken!);

            // Assert
            Assert.False(isValid);
        }

        [Fact]
        public async Task HoldSeatsController_WhenWaitingRoomActiveAndNoPass_Returns429TooManyRequests()
        {
            // Arrange
            var service = CreateService(new WaitingRoomOptions
            {
                Enabled = true,
                DefaultActiveThreshold = 1
            });
            var showtimeId = Guid.NewGuid();

            // Làm đầy ngưỡng active = 1
            await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "active_client");

            // Mock DB & Controller
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var mockSeatHoldService = new Mock<ISeatHoldService>();
            var controller = new ShowtimeSeatsController(
                new SeatImportService(db),
                mockSeatHoldService.Object,
                NullLogger<ShowtimeSeatsController>.Instance,
                service);

            var buyerId = Guid.NewGuid();
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, buyerId.ToString()),
                        new Claim(ClaimTypes.Role, "Customer")
                    }, "TestAuth"))
                }
            };

            // Act: Gọi giữ ghế nhưng KHÔNG có passToken
            var request = new HoldSeatsRequestDto { SeatIds = new List<Guid> { Guid.NewGuid() } };
            var result = await controller.HoldSeats(showtimeId, request, CancellationToken.None);

            // Assert: Trả về HTTP 429 Too Many Requests
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);

            var apiRes = Assert.IsType<ApiResponse<object>>(objectResult.Value);
            Assert.False(apiRes.Success);
            Assert.Contains("vượt ngưỡng", apiRes.Message);
        }

        [Fact]
        public async Task HoldSeatsController_WhenValidPassProvided_ProceedsSuccessfully()
        {
            // Arrange
            var service = CreateService(new WaitingRoomOptions
            {
                Enabled = true,
                DefaultActiveThreshold = 10
            });
            var showtimeId = Guid.NewGuid();

            // Cấp pass hợp lệ
            var joinResult = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "c1");
            Assert.Equal("ADMITTED", joinResult.Status);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var db = new AppDbContext(options);

            var seatId = Guid.NewGuid();
            var buyerId = Guid.NewGuid();

            var mockSeatHoldService = new Mock<ISeatHoldService>();
            mockSeatHoldService.Setup(s => s.HoldSeatsAsync(showtimeId, It.IsAny<List<Guid>>(), buyerId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(HoldSeatsResult.SuccessResult(
                    new HoldSeatsResponseDto { SeatIds = new List<Guid> { seatId } },
                    "Giữ ghế thành công"));

            var controller = new ShowtimeSeatsController(
                new SeatImportService(db),
                mockSeatHoldService.Object,
                NullLogger<ShowtimeSeatsController>.Instance,
                service);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, buyerId.ToString()),
                        new Claim(ClaimTypes.Role, "Customer")
                    }, "TestAuth"))
                }
            };

            // Act: Gửi request kèm PassToken hợp lệ
            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seatId },
                PassToken = joinResult.AccessPassToken
            };

            var result = await controller.HoldSeats(showtimeId, request, CancellationToken.None);

            // Assert: Thành công 200 OK
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
        }

        [Fact]
        public async Task ActivePing_WhenValidPassToken_RefreshesActivityAndReturnsTrue()
        {
            // Arrange
            var service = CreateService();
            var showtimeId = Guid.NewGuid();
            var user = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "tab_1");

            // Act
            bool alive = await service.ActivePingAsync(showtimeId, user.AccessPassToken!, "tab_1");

            // Assert
            Assert.True(alive);
        }

        [Fact]
        public async Task LeaveQueue_WhenTicketWasAdmitted_AlsoReleasesActivePassFromState()
        {
            // Arrange (Threshold = 1)
            var service = CreateService(new WaitingRoomOptions
            {
                Enabled = true,
                DefaultActiveThreshold = 1
            });
            var showtimeId = Guid.NewGuid();

            var user1 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_1"); // Admitted
            var user2 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_2"); // Waiting in queue

            // User 1 leaves
            await service.ReleaseActivePassAsync(showtimeId, user1.AccessPassToken!);

            // Now user 2 is promoted to ADMITTED
            var user2Ticket = await service.HeartbeatAsync(showtimeId, user2.QueueToken);
            Assert.NotNull(user2Ticket);
            Assert.Equal("ADMITTED", user2Ticket!.Status);

            // User 2 navigates away and calls LeaveQueue
            bool left = await service.LeaveQueueAsync(showtimeId, user2.QueueToken);
            Assert.True(left);

            // Assert: Stats must show 0 active passes
            var stats = await service.GetStatsAsync(showtimeId);
            Assert.Equal(0, stats.ActiveCount);
            Assert.Equal(0, stats.QueueLength);

            // Client 3 now joins and enters immediately without being blocked
            var user3 = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "client_3");
            Assert.Equal("ADMITTED", user3.Status);
        }

        [Fact]
        public async Task JoinQueue_WhenSameUserOpensSecondTab_AdmitsDirectlyWithoutTakingExtraSlot()
        {
            // Arrange: Threshold = 1
            var service = CreateService(new WaitingRoomOptions { DefaultActiveThreshold = 1 });
            var showtimeId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            // Tab 1: User gia nhập phòng chọn ghế -> ADMITTED
            var tab1 = await service.JoinQueueAsync(showtimeId, userId, "tab_1");
            Assert.Equal("ADMITTED", tab1.Status);
            Assert.False(string.IsNullOrWhiteSpace(tab1.AccessPassToken));

            var statsAfterTab1 = await service.GetStatsAsync(showtimeId);
            Assert.Equal(1, statsAfterTab1.ActiveCount);

            // Act: Cùng tài khoản người dùng mở Tab 2 (khác clientIdentifier / session)
            var tab2 = await service.JoinQueueAsync(showtimeId, userId, "tab_2");

            // Assert: Tab 2 được vào thẳng (ADMITTED) với cùng AccessPassToken và không tốn thêm slot
            Assert.Equal("ADMITTED", tab2.Status);
            Assert.Equal(tab1.AccessPassToken, tab2.AccessPassToken);

            var statsAfterTab2 = await service.GetStatsAsync(showtimeId);
            Assert.Equal(1, statsAfterTab2.ActiveCount); // Vẫn chỉ là 1 slot cho tài khoản này

            // Khi người dùng khác (tài khoản khác) truy cập -> Phải vào hàng chờ vì threshold = 1 đã đủ
            var otherUser = await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "other_user_tab");
            Assert.Equal("WAITING", otherUser.Status);
            Assert.Equal(1, otherUser.Position);
        }

        [Fact]
        public async Task ValidatePassToken_WhenSameUserValidatesFromDifferentTab_ReturnsTrue()
        {
            // Arrange
            var service = CreateService();
            var showtimeId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var tab1 = await service.JoinQueueAsync(showtimeId, userId, "tab_1");
            string passToken = tab1.AccessPassToken!;

            // Act & Assert 1: Tab 2 của cùng tài khoản validate với userId của mình -> Hợp lệ
            bool tab2Valid = await service.ValidatePassTokenAsync(showtimeId, passToken, "tab_2", userId);
            Assert.True(tab2Valid);

            // Act & Assert 2: Người dùng khác (UserId khác) cố dùng passToken này -> Bị từ chối
            var otherUserId = Guid.NewGuid();
            bool otherUserValid = await service.ValidatePassTokenAsync(showtimeId, passToken, "tab_other", otherUserId);
            Assert.False(otherUserValid);
        }

        [Fact]
        public async Task JoinQueue_WhenSameUserInQueueOpensSecondTab_ReturnsSameQueueTicket()
        {
            // Arrange: Threshold = 1, User 1 đã chiếm slot
            var service = CreateService(new WaitingRoomOptions { DefaultActiveThreshold = 1 });
            var showtimeId = Guid.NewGuid();

            await service.JoinQueueAsync(showtimeId, Guid.NewGuid(), "user_active");

            // User 2 vào hàng chờ (WAITING)
            var user2Id = Guid.NewGuid();
            var user2Tab1 = await service.JoinQueueAsync(showtimeId, user2Id, "user2_tab1");
            Assert.Equal("WAITING", user2Tab1.Status);
            Assert.Equal(1, user2Tab1.Position);

            // Act: User 2 mở Tab 2 khi đang xếp hàng
            var user2Tab2 = await service.JoinQueueAsync(showtimeId, user2Id, "user2_tab2");

            // Assert: Tab 2 nhận cùng QueueToken và vị trí, không xếp thêm slot thừa trong queue
            Assert.Equal("WAITING", user2Tab2.Status);
            Assert.Equal(user2Tab1.QueueToken, user2Tab2.QueueToken);
            Assert.Equal(1, user2Tab2.Position);

            var stats = await service.GetStatsAsync(showtimeId);
            Assert.Equal(1, stats.QueueLength); // Vẫn chỉ là 1 người trong hàng chờ
        }
    }
}
