using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class PublicEventsUiAndFlowTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Mock<IDistributedCache> _mockCache;

        public PublicEventsUiAndFlowTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _mockCache = new Mock<IDistributedCache>();
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private PublicShowtimeController CreateController()
        {
            var httpContext = new DefaultHttpContext(); // Public Unauthenticated User
            return new PublicShowtimeController(
                _context,
                _mockCache.Object,
                NullLogger<PublicShowtimeController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };
        }

        [Fact]
        public async Task GetPublicShowtimeDetail_ShouldReturn200_WhenShowtimeExists()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = Guid.NewGuid(),
                Title = "Live Concert 2026",
                Description = "A great music event",
                Location = "National Stadium",
                StartTime = DateTime.UtcNow.AddDays(5),
                EndTime = DateTime.UtcNow.AddDays(5).AddHours(3),
                TotalSeats = 1000
            };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                AvailableSeats = 500,
                StartTime = ev.StartTime,
                EndTime = ev.EndTime
            };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);

            showtime.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Name = "GA", Price = 300000m });
            showtime.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Name = "VIP", Price = 1200000m });

            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            // Act
            var result = await controller.GetPublicShowtimeDetail(ev.Id, showtime.Id);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<PublicShowtimeDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Equal("Live Concert 2026", response.Data.EventTitle);
            Assert.Equal("A great music event", response.Data.EventDescription);
            Assert.Equal(300000m, response.Data.MinPrice);
            Assert.Equal(1200000m, response.Data.MaxPrice);
            Assert.Equal("OnSale", response.Data.Status);
        }

        [Fact]
        public async Task GetPublicShowtimeDetail_ShouldReturn404_WhenNotFound()
        {
            // Arrange
            var controller = CreateController();

            // Act
            var result = await controller.GetPublicShowtimeDetail(Guid.NewGuid(), Guid.NewGuid());

            // Assert
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            var response = Assert.IsType<ApiResponse<object>>(notFoundResult.Value);
            Assert.False(response.Success);
            Assert.Equal("Không tìm thấy suất chiếu.", response.Message);
        }
    }
}
