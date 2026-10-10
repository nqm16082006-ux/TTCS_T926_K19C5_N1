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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class EventControllerHoldStatsTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly EventController _controller;
        private readonly Guid _userId = Guid.NewGuid();

        public EventControllerHoldStatsTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim(ClaimTypes.Role, "Organizer")
            };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            var httpContext = new DefaultHttpContext { User = claimsPrincipal };

            _controller = new EventController(_context)
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

        [Fact]
        public async Task GetHoldStats_ReturnsCorrectMetrics()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _userId,
                Title = "Report Event",
                Location = "Location",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(2),
                TotalSeats = 100
            };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                AvailableSeats = 100,
                StartTime = ev.StartTime,
                EndTime = ev.EndTime
            };
            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);

            // Seats
            var seat1 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "A", SeatNumber = 1 };
            var seat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "A", SeatNumber = 2 };
            var seat3 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "A", SeatNumber = 3 };
            var seat4 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "A", SeatNumber = 4 };
            _context.Seats.AddRange(seat1, seat2, seat3, seat4);

            var now = DateTime.UtcNow;

            // 1 Active hold
            _context.SeatHold.Add(new SeatHolds { SeatId = seat1.Id, UserId = _userId, Status = "ACTIVE", HeldAt = now });
            // 1 Expired hold
            _context.SeatHold.Add(new SeatHolds { SeatId = seat2.Id, UserId = _userId, Status = "EXPIRED", HeldAt = now });

            // 2 Converted holds for same order
            var heldAt = now.AddMinutes(-5);
            var paidAt = now.AddMinutes(-2);
            _context.SeatHold.Add(new SeatHolds { Id = Guid.NewGuid(), SeatId = seat3.Id, UserId = _userId, Status = "CONVERTED", HeldAt = heldAt });
            _context.SeatHold.Add(new SeatHolds { Id = Guid.NewGuid(), SeatId = seat4.Id, UserId = _userId, Status = "CONVERTED", HeldAt = heldAt });

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                CreatedAt = heldAt.AddSeconds(10), // Ensures order is created after hold
                UpdatedAt = paidAt,
                OrderItems = new List<OrderItem>
                {
                    new OrderItem { SeatId = seat3.Id, Price = 10 },
                    new OrderItem { SeatId = seat4.Id, Price = 10 }
                }
            };
            _context.Orders.Add(order);

            var pt = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Status = "PAID",
                CreatedAt = heldAt.AddSeconds(15),
                UpdatedAt = paidAt // The actual payment time
            };
            _context.PaymentTransactions.Add(pt);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.GetHoldStats(ev.Id, showtime.Id) as OkObjectResult;

            // Assert
            Assert.NotNull(result);
            var response = result.Value as ApiResponse<SeatHoldReportDto>;
            Assert.NotNull(response);
            Assert.True(response.Success);

            var stats = response.Data;
            Assert.Equal(showtime.Id, stats.ShowtimeId);
            Assert.Equal(4, stats.TotalHoldCount);
            Assert.Equal(2, stats.ConvertedHoldCount);
            Assert.Equal(1, stats.ExpiredHoldCount);
            Assert.Equal(0, stats.ReleasedHoldCount);
            Assert.Equal(1, stats.UnsuccessfulHoldCount); // Only expired/released
            Assert.Equal(66.67, stats.ConversionRate); // 2 / (2 + 1) = 66.67

            var expectedDuration = (paidAt - heldAt).TotalSeconds;
            Assert.Equal(Math.Round(expectedDuration, 2), stats.AveragePaymentDurationSeconds);
            Assert.Equal(Math.Round(expectedDuration, 2), stats.P90PaymentDurationSeconds);
        }

        [Fact]
        public async Task GetHoldStats_NoHolds_ReturnsZeroesAndNulls()
        {
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = _userId, Title = "E", Location = "L", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1) };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, StartTime = ev.StartTime, EndTime = ev.EndTime };
            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);
            await _context.SaveChangesAsync();

            var result = await _controller.GetHoldStats(ev.Id, showtime.Id) as OkObjectResult;
            var stats = ((ApiResponse<SeatHoldReportDto>)result.Value).Data;

            Assert.Equal(0, stats.TotalHoldCount);
            Assert.Equal(0, stats.ConversionRate);
            Assert.Null(stats.AveragePaymentDurationSeconds);
            Assert.Null(stats.P90PaymentDurationSeconds);
        }

        [Fact]
        public async Task GetHoldStats_UnauthorizedUser_ReturnsForbidden()
        {
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "E", Location = "L", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1) };
            var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, StartTime = ev.StartTime, EndTime = ev.EndTime };
            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);
            await _context.SaveChangesAsync();

            var result = await _controller.GetHoldStats(ev.Id, showtime.Id) as ObjectResult;
            Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        }
    }
}
