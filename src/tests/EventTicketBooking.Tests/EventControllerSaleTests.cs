using System;
using System.Collections.Generic;
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
    public class EventControllerSaleTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly EventController _controller;
        private readonly Guid _userId = Guid.NewGuid();

        public EventControllerSaleTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);

            // Setup User Context
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
        public async Task OpenSale_ShouldReturnOk_WhenConditionsAreMet()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _userId,
                Title = "Test Event",
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
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.OpenSale(ev.Id, showtime.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.Equal(ShowtimeStatus.OnSale, apiResponse.Data.Status);

            var showtimeInDb = await _context.Showtimes.FindAsync(showtime.Id);
            Assert.Equal(ShowtimeStatus.OnSale, showtimeInDb.Status);
        }

        [Fact]
        public async Task OpenSale_ShouldReturnBadRequest_WhenNoAvailableSeats()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _userId,
                Title = "Test Event",
                Location = "Location",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(2),
                TotalSeats = 100
            };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                AvailableSeats = 0, // No seats
                StartTime = ev.StartTime,
                EndTime = ev.EndTime
            };
            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.OpenSale(ev.Id, showtime.Id);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);
            Assert.False(apiResponse.Success);
            Assert.Equal("Không thể chuyển sang trạng thái Đang bán vì suất diễn chưa có ghế.", apiResponse.Message);
        }

        [Fact]
        public async Task OpenSale_ShouldReturnForbidden_WhenNotOwner()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = Guid.NewGuid(), // Different user
                Title = "Test Event",
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
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.OpenSale(ev.Id, showtime.Id);

            // Assert
            var forbiddenResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
        }

        [Fact]
        public async Task CloseSale_ShouldReturnOk_WhenShowtimeIsOnSale()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _userId,
                Title = "Test Event",
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

            // Set status to OnSale first using ChangeStatus
            showtime.ChangeStatus(ShowtimeStatus.OnSale);

            _context.Events.Add(ev);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.CloseSale(ev.Id, showtime.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.Equal(ShowtimeStatus.Closed, apiResponse.Data.Status);
        }

        [Fact]
        public async Task GetEventShowtimes_ShouldReturnStatusAndActionMessage()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _userId,
                Title = "Test Event",
                Location = "Location",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(2),
                TotalSeats = 100
            };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                AvailableSeats = 100, // Has seats -> Draft status, valid to open
                StartTime = ev.StartTime,
                EndTime = ev.EndTime
            };
            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.GetEventShowtimes(ev.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<List<ShowtimeResponseDto>>>(okResult.Value);
            Assert.True(apiResponse.Success);

            var dto = Assert.Single(apiResponse.Data);
            Assert.Equal(ShowtimeStatus.Draft, dto.Status);
            Assert.True(dto.CanOpenSale);
            Assert.False(dto.CanCloseSale);
            Assert.Equal("Suất diễn đủ điều kiện để mở bán.", dto.StatusActionMessage);
        }
    }
}
