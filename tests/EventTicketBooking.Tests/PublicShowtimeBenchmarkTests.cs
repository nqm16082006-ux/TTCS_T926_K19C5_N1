using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace EventTicketBooking.Tests
{
    public class PublicShowtimeBenchmarkTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly TestDistributedCache _testCache;
        private readonly ITestOutputHelper _output;

        public PublicShowtimeBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _testCache = new TestDistributedCache();
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
                _testCache,
                NullLogger<PublicShowtimeController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }

        [Fact]
        public async Task Performance_200Showtimes_QueryResponseTime_MustBeUnder500ms()
        {
            // 1. Seed 200 Showtimes with 3 SeatCategories each
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = Guid.NewGuid(),
                Title = "Mega Festival 2026",
                Location = "National Exhibition Center",
                TotalSeats = 100000
            };
            _context.Events.Add(ev);

            var baseTime = DateTime.UtcNow.AddDays(1);
            var showtimes = new List<Showtime>(200);

            for (int i = 0; i < 200; i++)
            {
                var s = new Showtime
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    AvailableSeats = 500,
                    StartTime = baseTime.AddMinutes(i * 15),
                    EndTime = baseTime.AddMinutes(i * 15 + 120)
                };
                s.ChangeStatus(ShowtimeStatus.OnSale);

                s.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s.Id, Name = "Standard", Price = 100000m + (i * 100) });
                s.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s.Id, Name = "VIP", Price = 500000m + (i * 100) });
                s.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s.Id, Name = "VVIP", Price = 2000000m + (i * 100) });

                showtimes.Add(s);
            }

            _context.Showtimes.AddRange(showtimes);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            // 2. Measure Cold Query (DB Hit, Cache Miss)
            var sw = Stopwatch.StartNew();
            var result1 = await controller.GetOnSaleShowtimes(cursor: null, limit: 10);
            sw.Stop();
            long dbQueryMs = sw.ElapsedMilliseconds;

            _output.WriteLine($"[Benchmark] Cold Query (200 Showtimes DB Hit): {dbQueryMs} ms");

            var okResult1 = Assert.IsType<OkObjectResult>(result1);
            var response1 = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(okResult1.Value);
            Assert.Equal(10, response1.Data.Items.Count);
            Assert.NotNull(response1.Data.NextCursor);

            // Verify < 500ms Criteria
            Assert.True(dbQueryMs < 500, $"DB Query time {dbQueryMs}ms exceeds 500ms threshold!");

            // 3. Measure Hot Query (Redis Cache Hit)
            sw.Restart();
            var result2 = await controller.GetOnSaleShowtimes(cursor: null, limit: 10);
            sw.Stop();
            long cacheHitMs = sw.ElapsedMilliseconds;

            _output.WriteLine($"[Benchmark] Hot Query (Redis Cache Hit): {cacheHitMs} ms");

            Assert.True(cacheHitMs < 500, $"Cache Hit query time {cacheHitMs}ms exceeds 500ms threshold!");

            // 4. Measure Page 2 Query with NextCursor
            sw.Restart();
            var resultPage2 = await controller.GetOnSaleShowtimes(cursor: response1.Data.NextCursor, limit: 10);
            sw.Stop();
            long page2Ms = sw.ElapsedMilliseconds;

            _output.WriteLine($"[Benchmark] Page 2 Query with Cursor: {page2Ms} ms");

            var okResultPage2 = Assert.IsType<OkObjectResult>(resultPage2);
            var responsePage2 = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(okResultPage2.Value);
            Assert.Equal(10, responsePage2.Data.Items.Count);
            Assert.True(page2Ms < 500, $"Page 2 Cursor Query time {page2Ms}ms exceeds 500ms threshold!");
        }
    }
}
