using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class SeatStatusQueryTests : IDisposable
    {
        private readonly AppDbContext _context;

        public SeatStatusQueryTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _context = new AppDbContext(options);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private PublicShowtimeController CreateController()
        {
            return new PublicShowtimeController(
                _context,
                new TestDistributedCache(),
                NullLogger<PublicShowtimeController>.Instance);
        }

        [Fact]
        public async Task Test1_ReturnsCorrectStatuses_Available_Mocked()
        {
            var eventId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            
            _context.Events.Add(new Event { Id = eventId, OwnerId = Guid.NewGuid(), Title = "Test Event", Location = "Hanoi", TotalSeats = 100 });
            _context.Showtimes.Add(new Showtime { Id = showtimeId, EventId = eventId, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2), AvailableSeats = 100 });
            
            var category = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtimeId, Name = "VIP", Price = 500000m };
            _context.SeatCategories.Add(category);

            var availableSeat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtimeId, SeatCategoryId = category.Id, Row = "A", SeatNumber = 1 };
            var availableSeat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtimeId, SeatCategoryId = category.Id, Row = "A", SeatNumber = 2 };
            
            _context.Seats.AddRange(availableSeat, availableSeat2);
            await _context.SaveChangesAsync();

            var controller = CreateController();
            var result = await controller.GetShowtimeSeats(showtimeId);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<List<SeatStatusDto>>>(okResult.Value);
            
            Assert.True(response.Success);
            Assert.Equal(2, response.Data.Count);

            // Vì T-19 hoàn toàn độc lập, chưa nối bảng SeatHolds nên trả về AVAILABLE hết
            var seat1 = response.Data.First(s => s.Id == availableSeat.Id);
            Assert.Equal("AVAILABLE", seat1.Status);

            var seat2 = response.Data.First(s => s.Id == availableSeat2.Id);
            Assert.Equal("AVAILABLE", seat2.Status);
        }

        [Fact]
        public async Task Test2_Performance_2000Seats_Under200ms_Mocked()
        {
            var eventId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();
            
            _context.Events.Add(new Event { Id = eventId, OwnerId = Guid.NewGuid(), Title = "Mega Event", Location = "Stadium", TotalSeats = 2000 });
            _context.Showtimes.Add(new Showtime { Id = showtimeId, EventId = eventId, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2), AvailableSeats = 2000 });
            
            var category = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtimeId, Name = "Standard", Price = 100000m };
            _context.SeatCategories.Add(category);

            var seats = new List<Seat>(2000);

            for (int i = 0; i < 2000; i++)
            {
                var seat = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtimeId, SeatCategoryId = category.Id, Row = $"R{i/100}", SeatNumber = i % 100 };
                seats.Add(seat);
            }

            _context.Seats.AddRange(seats);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            // Warm up
            await controller.GetShowtimeSeats(showtimeId);

            // Measure
            var sw = Stopwatch.StartNew();
            var result = await controller.GetShowtimeSeats(showtimeId);
            sw.Stop();

            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<List<SeatStatusDto>>>(okResult.Value);
            
            Assert.Equal(2000, response.Data.Count);
            Assert.Equal(2000, response.Data.Count(s => s.Status == "AVAILABLE"));
            
            Assert.True(sw.ElapsedMilliseconds < 200, $"Query took {sw.ElapsedMilliseconds}ms, which exceeds 200ms");
        }
    }
}
