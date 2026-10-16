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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử chuyên biệt cho Story S-52: Ban tổ chức xem số khách đã vào cửa theo thời gian thực.
    /// </summary>
    public class CheckinLiveStatisticsS52Tests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly CheckinStatisticsService _service;
        private readonly TicketCheckInController _controller;
        private readonly Guid _organizerId = Guid.NewGuid();
        private readonly Guid _otherOrganizerId = Guid.NewGuid();
        private readonly Guid _adminId = Guid.NewGuid();

        public CheckinLiveStatisticsS52Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _service = new CheckinStatisticsService(_context);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, _organizerId.ToString()),
                new Claim(ClaimTypes.Role, "Organizer")
            };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var principal = new ClaimsPrincipal(identity);

            _controller = new TicketCheckInController(
                _context,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketCheckInController>.Instance,
                null,
                null,
                null,
                _service,
                null)
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

        private async Task<(Event ev, Showtime showtime)> SeedEventAndShowtimeAsync(Guid ownerId)
        {
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Live Concert Hòa Nhạc S-52",
                OwnerId = ownerId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                StartTime = DateTime.UtcNow.AddHours(1),
                EndTime = DateTime.UtcNow.AddHours(4)
            };

            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);
            await _context.SaveChangesAsync();
            return (ev, showtime);
        }

        private async Task SeedTicketAsync(
            Showtime showtime,
            OrderStatus orderStatus,
            bool isCheckedIn,
            string gate = "Cổng A",
            DateTimeOffset? checkInTime = null)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = $"user_{Guid.NewGuid():N}",
                Email = $"user_{Guid.NewGuid():N}@test.com",
                PasswordHash = "hash"
            };
            _context.Users.Add(user);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ShowtimeId = showtime.Id,
                Status = orderStatus,
                TotalAmount = 500000,
                CreatedAt = DateTime.UtcNow
            };
            _context.Orders.Add(order);

            var seat = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Row = "A",
                SeatNumber = 1,
                Status = "SOLD"
            };
            _context.Seats.Add(seat);

            var orderItem = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Order = order,
                SeatId = seat.Id,
                Seat = seat,
                Price = 500000,
                IsCheckedIn = isCheckedIn,
                CheckInGate = isCheckedIn ? gate : null,
                CheckInTime = isCheckedIn ? (checkInTime ?? DateTimeOffset.UtcNow) : null
            };
            _context.OrderItems.Add(orderItem);

            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = orderItem.Id,
                OrderItem = orderItem,
                TicketCode = $"TK-{Guid.NewGuid():N}",
                CreatedAt = DateTime.UtcNow
            };
            _context.Tickets.Add(ticket);

            await _context.SaveChangesAsync();
        }

        [Fact]
        public async Task GetLiveCheckinStats_ThrowsKeyNotFoundException_WhenShowtimeDoesNotExist()
        {
            var fakeId = Guid.NewGuid();
            await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetLiveCheckinStatsAsync(fakeId));
        }

        [Fact]
        public async Task GetLiveCheckinStats_ReturnsCorrectOverviewMetrics_WhenMixedTicketsExist()
        {
            var (_, showtime) = await SeedEventAndShowtimeAsync(_organizerId);

            // 6 vé hợp lệ đã vào cửa
            for (int i = 0; i < 6; i++)
            {
                await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A");
            }

            // 4 vé hợp lệ chưa vào cửa
            for (int i = 0; i < 4; i++)
            {
                await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: false);
            }

            // 2 vé đơn bị hủy (Cancelled) - không được tính vào vé bán ra
            await SeedTicketAsync(showtime, OrderStatus.Cancelled, isCheckedIn: false);
            await SeedTicketAsync(showtime, OrderStatus.Cancelled, isCheckedIn: true);

            var stats = await _service.GetLiveCheckinStatsAsync(showtime.Id);

            Assert.Equal(10, stats.TotalSoldTickets);
            Assert.Equal(6, stats.CheckedInCount);
            Assert.Equal(4, stats.RemainingCount);
            Assert.Equal(60.0, stats.CheckInPercentage);
            Assert.Equal("Live Concert Hòa Nhạc S-52", stats.EventTitle);
        }

        [Fact]
        public async Task GetLiveCheckinStats_ReturnsGateBreakdownAccurately()
        {
            var (_, showtime) = await SeedEventAndShowtimeAsync(_organizerId);

            // 4 vé Cổng A
            for (int i = 0; i < 4; i++)
            {
                await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A");
            }

            // 2 vé Cổng B
            for (int i = 0; i < 2; i++)
            {
                await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng B");
            }

            var stats = await _service.GetLiveCheckinStatsAsync(showtime.Id);

            Assert.Equal(2, stats.GateBreakdown.Count);
            var gateA = stats.GateBreakdown.FirstOrDefault(g => g.GateName == "Cổng A");
            var gateB = stats.GateBreakdown.FirstOrDefault(g => g.GateName == "Cổng B");

            Assert.NotNull(gateA);
            Assert.Equal(4, gateA.CheckedInCount);
            Assert.Equal(66.7, gateA.PercentageOfTotal);

            Assert.NotNull(gateB);
            Assert.Equal(2, gateB.CheckedInCount);
            Assert.Equal(33.3, gateB.PercentageOfTotal);
        }

        [Fact]
        public async Task GetLiveCheckinStats_CalculatesVelocityPerMinute_BasedOnRecent5Minutes()
        {
            var (_, showtime) = await SeedEventAndShowtimeAsync(_organizerId);
            var now = DateTimeOffset.UtcNow;

            // 5 vé quét trong vòng 2 phút gần nhất
            for (int i = 0; i < 5; i++)
            {
                await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A", checkInTime: now.AddMinutes(-2));
            }

            // 1 vé quét từ 20 phút trước (ngoài phạm vi 5 phút tính velocity tức thời)
            await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A", checkInTime: now.AddMinutes(-20));

            var stats = await _service.GetLiveCheckinStatsAsync(showtime.Id);

            // 5 khách trong 5 phút = 1.0 khách/phút
            Assert.Equal(1.0, stats.CurrentVelocityPerMinute);
            Assert.Equal(6, stats.CheckedInCount);
        }

        [Fact]
        public async Task GetLiveCheckinStats_BucketsTimelinePoints_Into30Minutes()
        {
            var (_, showtime) = await SeedEventAndShowtimeAsync(_organizerId);
            var now = DateTimeOffset.UtcNow;

            await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A", checkInTime: now.AddMinutes(-5));
            await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng A", checkInTime: now.AddMinutes(-5));

            var stats = await _service.GetLiveCheckinStatsAsync(showtime.Id);

            Assert.Equal(30, stats.Timeline.Count);
            var sumTimeline = stats.Timeline.Sum(t => t.Count);
            Assert.Equal(2, sumTimeline);
        }

        [Fact]
        public async Task GetLiveCheckinStats_IncludesOfflineSyncStats_WhenOfflineRecordsExist()
        {
            var (_, showtime) = await SeedEventAndShowtimeAsync(_organizerId);

            var record1 = new OfflineCheckInRecord
            {
                Id = Guid.NewGuid(),
                OfflineScanId = "SCAN-OFFLINE-001",
                ShowtimeId = showtime.Id,
                TicketCode = "TK-001",
                GateName = "Cổng B",
                Status = "SYNCED",
                IsConflict = false,
                ReceivedAtServer = DateTimeOffset.UtcNow.AddMinutes(-10),
                ScannedAtDevice = DateTimeOffset.UtcNow.AddMinutes(-15)
            };

            var record2 = new OfflineCheckInRecord
            {
                Id = Guid.NewGuid(),
                OfflineScanId = "SCAN-OFFLINE-002",
                ShowtimeId = showtime.Id,
                TicketCode = "TK-002",
                GateName = "Cổng B",
                Status = "SYNCED",
                IsConflict = false,
                ReceivedAtServer = DateTimeOffset.UtcNow.AddMinutes(-5),
                ScannedAtDevice = DateTimeOffset.UtcNow.AddMinutes(-8)
            };

            var conflictRecord = new OfflineCheckInRecord
            {
                Id = Guid.NewGuid(),
                OfflineScanId = "SCAN-OFFLINE-003",
                ShowtimeId = showtime.Id,
                TicketCode = "TK-003",
                GateName = "Cổng C",
                Status = "conflict",
                IsConflict = true,
                ReceivedAtServer = DateTimeOffset.UtcNow,
                ScannedAtDevice = DateTimeOffset.UtcNow.AddMinutes(-1)
            };

            _context.OfflineCheckInRecords.AddRange(record1, record2, conflictRecord);
            await _context.SaveChangesAsync();

            var stats = await _service.GetLiveCheckinStatsAsync(showtime.Id);

            Assert.Equal(2, stats.OfflineSyncedCount);
            Assert.NotNull(stats.LastSyncTime);
            Assert.Equal(record2.ReceivedAtServer, stats.LastSyncTime);
        }

        [Fact]
        public async Task Controller_GetLiveCheckinStats_ReturnsForbidden_WhenOrganizerDoesNotOwnEvent()
        {
            // Event thuộc _otherOrganizerId nhưng controller được gọi bởi _organizerId
            var (_, showtime) = await SeedEventAndShowtimeAsync(_otherOrganizerId);

            var result = await _controller.GetLiveCheckinStats(showtime.Id);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        }

        [Fact]
        public async Task Controller_GetLiveCheckinStats_ReturnsOk_WhenOrganizerOwnsEvent()
        {
            var (_, showtime) = await SeedEventAndShowtimeAsync(_organizerId);
            await SeedTicketAsync(showtime, OrderStatus.Paid, isCheckedIn: true, gate: "Cổng VIP");

            var result = await _controller.GetLiveCheckinStats(showtime.Id);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var data = Assert.IsType<ShowtimeLiveCheckinDto>(okResult.Value);
            Assert.Equal(1, data.TotalSoldTickets);
            Assert.Equal(1, data.CheckedInCount);
        }

        [Fact]
        public async Task Controller_GetLiveCheckinStats_ReturnsOk_WhenAdminCallsEvenIfNotOwner()
        {
            var (_, showtime) = await SeedEventAndShowtimeAsync(_otherOrganizerId);

            var adminClaims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, _adminId.ToString()),
                new Claim(ClaimTypes.Role, "Admin")
            };
            var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(adminClaims, "TestAuth"));
            _controller.ControllerContext.HttpContext.User = adminPrincipal;

            var result = await _controller.GetLiveCheckinStats(showtime.Id);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var data = Assert.IsType<ShowtimeLiveCheckinDto>(okResult.Value);
            Assert.NotNull(data);
        }

        [Fact]
        public async Task Controller_GetLiveCheckinStats_ReturnsBadRequest_WhenGuidEmpty()
        {
            var result = await _controller.GetLiveCheckinStats(Guid.Empty);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequest.Value);
        }

        [Fact]
        public async Task Controller_GetLiveCheckinStats_ReturnsNotFound_WhenShowtimeDoesNotExist()
        {
            var result = await _controller.GetLiveCheckinStats(Guid.NewGuid());
            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.NotNull(notFound.Value);
        }
    }
}
