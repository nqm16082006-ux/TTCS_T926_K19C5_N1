using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class WaitlistS59Tests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Mock<IEmailService> _emailServiceMock;
        private readonly WaitlistService _waitlistService;
        private readonly Guid _organizerId = Guid.NewGuid();
        private readonly Guid _showtimeId = Guid.NewGuid();
        private readonly Guid _eventId = Guid.NewGuid();
        private readonly Guid _seatCategoryId = Guid.NewGuid();

        public WaitlistS59Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new AppDbContext(options);
            _emailServiceMock = new Mock<IEmailService>();

            _emailServiceMock
                .Setup(m => m.SendWaitlistSeatAvailableEmailAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<string>()))
                .ReturnsAsync(true);

            var inMemorySettings = new Dictionary<string, string?>
            {
                {"App:FrontendUrl", "https://test.eventpulse.vn"}
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            _waitlistService = new WaitlistService(
                _context,
                _emailServiceMock.Object,
                configuration,
                NullLogger<WaitlistService>.Instance);

            SeedTestData();
        }

        private void SeedTestData()
        {
            var organizer = new User
            {
                Id = _organizerId,
                Username = "organizer",
                Email = "organizer@test.com",
                PasswordHash = "hash",
                FullName = "Organizer Test",
                IsActive = true
            };
            _context.Users.Add(organizer);

            var ev = new Event
            {
                Id = _eventId,
                Title = "Concert Đêm Nhạc Mùa Thu",
                OwnerId = _organizerId,
                Location = "Nhà hát Lớn Hà Nội",
                StartTime = DateTime.UtcNow.AddDays(3),
                EndTime = DateTime.UtcNow.AddDays(3).AddHours(3),
                CreatedAt = DateTime.UtcNow
            };
            _context.Events.Add(ev);

            var category = new SeatCategory
            {
                Id = _seatCategoryId,
                ShowtimeId = _showtimeId,
                Name = "VIP",
                Price = 500000
            };
            _context.SeatCategories.Add(category);

            var showtime = new Showtime
            {
                Id = _showtimeId,
                EventId = _eventId,
                StartTime = DateTime.UtcNow.AddDays(3),
                EndTime = DateTime.UtcNow.AddDays(3).AddHours(3),
                AvailableSeats = 3
            };
            showtime.SeatCategories.Add(category);
            showtime.ChangeStatus(ShowtimeStatus.OnSale);
            showtime.AvailableSeats = 0;
            _context.Showtimes.Add(showtime);

            // Add seats
            for (int i = 1; i <= 3; i++)
            {
                _context.Seats.Add(new Seat
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = _showtimeId,
                    SeatCategoryId = _seatCategoryId,
                    Row = "A",
                    SeatNumber = i,
                    Status = "SOLD" // All seats sold out initially
                });
            }

            _context.SaveChanges();
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        [Fact]
        public async Task JoinWaitlistAsync_WhenNewUser_CreatesRecordAndReturnsPosition1()
        {
            var request = new JoinWaitlistRequestDto
            {
                ShowtimeId = _showtimeId,
                Email = "user1@example.com",
                FullName = "User One",
                PhoneNumber = "0901234567"
            };

            var result = await _waitlistService.JoinWaitlistAsync(request);

            Assert.NotNull(result);
            Assert.Equal(1, result.QueuePosition);
            Assert.Equal(1, result.TotalWaiting);
            Assert.False(result.IsExisting);

            var record = await _context.ShowtimeWaitlists.FirstOrDefaultAsync(w => w.ShowtimeId == _showtimeId && w.Email == "user1@example.com");
            Assert.NotNull(record);
            Assert.Equal("WAITING", record.Status);
        }

        [Fact]
        public async Task JoinWaitlistAsync_WhenMultipleUsers_AssignsFifoQueuePositions()
        {
            var req1 = new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "first@example.com", FullName = "First" };
            var req2 = new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "second@example.com", FullName = "Second" };
            var req3 = new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "third@example.com", FullName = "Third" };

            var res1 = await _waitlistService.JoinWaitlistAsync(req1);
            var res2 = await _waitlistService.JoinWaitlistAsync(req2);
            var res3 = await _waitlistService.JoinWaitlistAsync(req3);

            Assert.Equal(1, res1.QueuePosition);
            Assert.Equal(2, res2.QueuePosition);
            Assert.Equal(3, res3.QueuePosition);
            Assert.Equal(3, res3.TotalWaiting);
        }

        [Fact]
        public async Task JoinWaitlistAsync_WhenDuplicateEmail_ReturnsExistingPositionWithoutDuplicateRecord()
        {
            var req1 = new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "first@example.com", FullName = "First" };
            var req2 = new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "second@example.com", FullName = "Second" };

            await _waitlistService.JoinWaitlistAsync(req1);
            await _waitlistService.JoinWaitlistAsync(req2);

            // Re-join with first email
            var resDuplicate = await _waitlistService.JoinWaitlistAsync(req1);

            Assert.NotNull(resDuplicate);
            Assert.True(resDuplicate.IsExisting);
            Assert.Equal(1, resDuplicate.QueuePosition);
            Assert.Equal(2, resDuplicate.TotalWaiting);

            var count = await _context.ShowtimeWaitlists.CountAsync(w => w.ShowtimeId == _showtimeId && w.Email == "first@example.com");
            Assert.Equal(1, count); // No duplicate entries
        }

        [Fact]
        public async Task JoinWaitlistAsync_WhenInvalidEmail_ThrowsArgumentException()
        {
            var request = new JoinWaitlistRequestDto
            {
                ShowtimeId = _showtimeId,
                Email = "invalid-email",
                FullName = "Test"
            };

            await Assert.ThrowsAsync<ArgumentException>(() => _waitlistService.JoinWaitlistAsync(request));
        }

        [Fact]
        public async Task JoinWaitlistAsync_WhenShowtimeDoesNotExist_ThrowsKeyNotFoundException()
        {
            var request = new JoinWaitlistRequestDto
            {
                ShowtimeId = Guid.NewGuid(),
                Email = "valid@example.com"
            };

            await Assert.ThrowsAsync<KeyNotFoundException>(() => _waitlistService.JoinWaitlistAsync(request));
        }

        [Fact]
        public async Task GetStatusAsync_ReturnsAccurateCountAndUserPosition()
        {
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "first@example.com" });
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "second@example.com" });

            var statusFirst = await _waitlistService.GetStatusAsync(_showtimeId, "first@example.com");
            Assert.Equal(2, statusFirst.TotalWaiting);
            Assert.True(statusFirst.IsUserWaiting);
            Assert.Equal(1, statusFirst.UserQueuePosition);

            var statusSecond = await _waitlistService.GetStatusAsync(_showtimeId, "second@example.com");
            Assert.Equal(2, statusSecond.TotalWaiting);
            Assert.True(statusSecond.IsUserWaiting);
            Assert.Equal(2, statusSecond.UserQueuePosition);

            var statusUnknown = await _waitlistService.GetStatusAsync(_showtimeId, "unknown@example.com");
            Assert.Equal(2, statusUnknown.TotalWaiting);
            Assert.False(statusUnknown.IsUserWaiting);
            Assert.Null(statusUnknown.UserQueuePosition);
        }

        [Fact]
        public async Task ProcessAvailableSeatsNotificationAsync_WhenSeatBecomesAvailable_NotifiesFirstInFifoOrder()
        {
            // Register 2 users
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "first@example.com", FullName = "First Person" });
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "second@example.com", FullName = "Second Person" });

            // 1 seat becomes AVAILABLE
            var seat = await _context.Seats.FirstAsync(s => s.ShowtimeId == _showtimeId);
            seat.Status = "AVAILABLE";
            await _context.SaveChangesAsync();

            // Process notifications
            int notifiedCount = await _waitlistService.ProcessAvailableSeatsNotificationAsync(_showtimeId);

            Assert.Equal(1, notifiedCount);

            // First person should be NOTIFIED
            var firstEntry = await _context.ShowtimeWaitlists.FirstAsync(w => w.Email == "first@example.com");
            Assert.Equal("NOTIFIED", firstEntry.Status);
            Assert.NotNull(firstEntry.NotifiedAt);
            Assert.Equal(1, firstEntry.NotificationCount);

            // Second person should still be WAITING
            var secondEntry = await _context.ShowtimeWaitlists.FirstAsync(w => w.Email == "second@example.com");
            Assert.Equal("WAITING", secondEntry.Status);
            Assert.Null(secondEntry.NotifiedAt);

            // Verify email was sent to first person only
            _emailServiceMock.Verify(m => m.SendWaitlistSeatAvailableEmailAsync(
                "first@example.com",
                "First Person",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                1,
                It.IsAny<string>()), Times.Once);

            _emailServiceMock.Verify(m => m.SendWaitlistSeatAvailableEmailAsync(
                "second@example.com",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task ProcessAvailableSeatsNotificationAsync_WhenAnotherSeatReleased_NotifiesSecondUser()
        {
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "first@example.com" });
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "second@example.com" });

            // Release 1 seat & notify first
            var seats = await _context.Seats.Where(s => s.ShowtimeId == _showtimeId).ToListAsync();
            seats[0].Status = "AVAILABLE";
            await _context.SaveChangesAsync();
            await _waitlistService.ProcessAvailableSeatsNotificationAsync(_showtimeId);

            // Now release another seat
            seats[1].Status = "AVAILABLE";
            await _context.SaveChangesAsync();

            int secondRunCount = await _waitlistService.ProcessAvailableSeatsNotificationAsync(_showtimeId);

            Assert.Equal(1, secondRunCount);
            var secondEntry = await _context.ShowtimeWaitlists.FirstAsync(w => w.Email == "second@example.com");
            Assert.Equal("NOTIFIED", secondEntry.Status);
        }

        [Fact]
        public async Task ProcessAvailableSeatsNotificationAsync_WhenNoAvailableSeats_ReturnsZero()
        {
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "waiter@example.com" });

            // Seats are all SOLD
            int notified = await _waitlistService.ProcessAvailableSeatsNotificationAsync(_showtimeId);

            Assert.Equal(0, notified);
            _emailServiceMock.Verify(m => m.SendWaitlistSeatAvailableEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task WaitlistController_JoinWaitlist_ValidRequest_ReturnsOkWithData()
        {
            var controller = new WaitlistController(_waitlistService);
            var request = new JoinWaitlistRequestDto
            {
                ShowtimeId = _showtimeId,
                Email = "apiuser@example.com",
                FullName = "API User"
            };

            var actionResult = await controller.JoinWaitlist(request, CancellationToken.None);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<WaitlistRegistrationResultDto>>(okResult.Value);
            Assert.True(response.Success);
            Assert.Equal(1, response.Data!.QueuePosition);
        }

        [Fact]
        public async Task WaitlistController_JoinWaitlist_InvalidModel_ReturnsBadRequest()
        {
            var controller = new WaitlistController(_waitlistService);
            controller.ModelState.AddModelError("Email", "Email is required");

            var request = new JoinWaitlistRequestDto
            {
                ShowtimeId = _showtimeId,
                Email = ""
            };

            var actionResult = await controller.JoinWaitlist(request, CancellationToken.None);

            var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<object>>(badRequest.Value);
            Assert.False(response.Success);
        }

        [Fact]
        public async Task WaitlistController_GetStatus_ReturnsOkWithStatus()
        {
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "test@example.com" });
            var controller = new WaitlistController(_waitlistService);

            var actionResult = await controller.GetStatus(_showtimeId, "test@example.com", CancellationToken.None);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<WaitlistStatusResponseDto>>(okResult.Value);
            Assert.True(response.Success);
            Assert.Equal(1, response.Data!.TotalWaiting);
            Assert.True(response.Data.IsUserWaiting);
        }

        [Fact]
        public async Task WaitlistController_GetShowtimeWaitlist_Organizer_ReturnsOkWithEntries()
        {
            await _waitlistService.JoinWaitlistAsync(new JoinWaitlistRequestDto { ShowtimeId = _showtimeId, Email = "waiter1@example.com", FullName = "Waiter One" });

            var controller = new WaitlistController(_waitlistService);

            // Mock Organizer user context
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, _organizerId.ToString()),
                new Claim(ClaimTypes.Role, "Organizer")
            }, "TestAuth"));

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            };

            var actionResult = await controller.GetShowtimeWaitlist(_showtimeId, CancellationToken.None);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<List<WaitlistEntryDto>>>(okResult.Value);
            Assert.True(response.Success);
            Assert.Single(response.Data!);
            Assert.Equal("waiter1@example.com", response.Data![0].Email);
            Assert.Equal("WAITING", response.Data![0].Status);
        }
    }
}
