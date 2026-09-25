using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    public class PublicShowtimeApiTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Mock<IDistributedCache> _mockCache;
        private readonly Dictionary<string, (byte[] Data, DistributedCacheEntryOptions Options)> _inMemoryCacheStore;

        public PublicShowtimeApiTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);

            _inMemoryCacheStore = new Dictionary<string, (byte[], DistributedCacheEntryOptions)>();
            _mockCache = new Mock<IDistributedCache>();

            // Setup Mock Redis GetStringAsync / SetStringAsync behavior
            _mockCache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken token) =>
                {
                    if (_inMemoryCacheStore.TryGetValue(key, out var val))
                    {
                        return val.Data;
                    }
                    return null;
                });

            _mockCache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
                .Returns((string key, byte[] val, DistributedCacheEntryOptions opt, CancellationToken token) =>
                {
                    _inMemoryCacheStore[key] = (val, opt);
                    return Task.CompletedTask;
                });
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private PublicShowtimeController CreateController()
        {
            var httpContext = new DefaultHttpContext(); // No user identity (Unauthenticated)
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
        public async Task Test1_PublicApi_ShouldSucceed_WithoutAuthentication()
        {
            // Arrange
            var controller = CreateController();

            // Act
            var result = await controller.GetOnSaleShowtimes();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(okResult.Value);
            Assert.True(response.Success);
        }

        [Fact]
        public async Task Test2_3_4_Filter_ShouldOnlyReturnOnSaleShowtimes()
        {
            // Arrange
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = Guid.NewGuid(),
                Title = "Test Event",
                Location = "Hanoi Stadium",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(2),
                TotalSeats = 300
            };
            _context.Events.Add(ev);

            // Draft showtime
            var draft = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddHours(2), EndTime = DateTime.UtcNow.AddHours(4) };
            
            // OnSale showtime
            var onSale = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddHours(5), EndTime = DateTime.UtcNow.AddHours(7) };
            onSale.ChangeStatus(ShowtimeStatus.OnSale);

            // Closed showtime
            var closed = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddHours(8), EndTime = DateTime.UtcNow.AddHours(10) };
            closed.ChangeStatus(ShowtimeStatus.OnSale);
            closed.ChangeStatus(ShowtimeStatus.Closed);

            _context.Showtimes.AddRange(draft, onSale, closed);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            // Act
            var result = await controller.GetOnSaleShowtimes();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(okResult.Value);
            Assert.Single(response.Data.Items);
            Assert.Equal(onSale.Id, response.Data.Items.First().Id);
            Assert.Equal("OnSale", response.Data.Items.First().Status);
        }

        [Fact]
        public async Task Test5_6_7_8_9_CursorPagination_Sorting_NoDuplicates_NoMissing()
        {
            // Arrange
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "Concert", Location = "HCM City", TotalSeats = 500 };
            _context.Events.Add(ev);

            var now = DateTime.UtcNow.AddDays(1);
            var showtimes = new List<Showtime>();

            for (int i = 0; i < 5; i++)
            {
                var s = new Showtime
                {
                    Id = Guid.NewGuid(),
                    EventId = ev.Id,
                    AvailableSeats = 50,
                    StartTime = now.AddHours(i * 2),
                    EndTime = now.AddHours(i * 2 + 1)
                };
                s.ChangeStatus(ShowtimeStatus.OnSale);
                showtimes.Add(s);
            }

            _context.Showtimes.AddRange(showtimes);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            // Act - Fetch Page 1 (limit = 2)
            var page1Result = await controller.GetOnSaleShowtimes(cursor: null, limit: 2);
            var page1Ok = Assert.IsType<OkObjectResult>(page1Result);
            var page1Data = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(page1Ok.Value).Data;

            // Assert Page 1
            Assert.Equal(2, page1Data.Items.Count);
            Assert.True(page1Data.HasMore);
            Assert.NotNull(page1Data.NextCursor);

            // Page 1 ordering: StartTime ASC
            Assert.True(page1Data.Items[0].StartTime <= page1Data.Items[1].StartTime);

            // Act - Fetch Page 2 (limit = 2)
            var page2Result = await controller.GetOnSaleShowtimes(cursor: page1Data.NextCursor, limit: 2);
            var page2Ok = Assert.IsType<OkObjectResult>(page2Result);
            var page2Data = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(page2Ok.Value).Data;

            // Assert Page 2
            Assert.Equal(2, page2Data.Items.Count);
            Assert.True(page2Data.HasMore);
            Assert.NotNull(page2Data.NextCursor);

            // Act - Fetch Page 3 (limit = 2)
            var page3Result = await controller.GetOnSaleShowtimes(cursor: page2Data.NextCursor, limit: 2);
            var page3Ok = Assert.IsType<OkObjectResult>(page3Result);
            var page3Data = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(page3Ok.Value).Data;

            // Assert Page 3
            Assert.Single(page3Data.Items);
            Assert.False(page3Data.HasMore);
            Assert.Null(page3Data.NextCursor);

            // Verify total items retrieved across pages: 2 + 2 + 1 = 5, with NO duplicates and NO missing items
            var allRetrievedIds = page1Data.Items.Select(x => x.Id)
                .Concat(page2Data.Items.Select(x => x.Id))
                .Concat(page3Data.Items.Select(x => x.Id))
                .ToList();

            Assert.Equal(5, allRetrievedIds.Count);
            Assert.Equal(5, allRetrievedIds.Distinct().Count());
            Assert.Equal(showtimes.Select(s => s.Id).OrderBy(id => id), allRetrievedIds.OrderBy(id => id));
        }

        [Fact]
        public async Task Test10_11_12_MinMaxPrice_Calculation()
        {
            // Arrange
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "Music Fest", Location = "Da Nang", TotalSeats = 500 };
            _context.Events.Add(ev);

            // Showtime 1 with Seat Categories
            var s1 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            s1.ChangeStatus(ShowtimeStatus.OnSale);

            s1.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s1.Id, Name = "Standard", Price = 200000m });
            s1.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s1.Id, Name = "VIP", Price = 800000m });
            s1.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s1.Id, Name = "VVIP", Price = 1500000m });

            // Showtime 2 without Seat Categories
            var s2 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 50, StartTime = DateTime.UtcNow.AddDays(2), EndTime = DateTime.UtcNow.AddDays(2).AddHours(2) };
            s2.ChangeStatus(ShowtimeStatus.OnSale);

            _context.Showtimes.AddRange(s1, s2);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            // Act
            var result = await controller.GetOnSaleShowtimes(limit: 10);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(okResult.Value);
            var items = response.Data.Items;

            var dto1 = items.First(x => x.Id == s1.Id);
            Assert.Equal(200000m, dto1.MinPrice);
            Assert.Equal(1500000m, dto1.MaxPrice);

            var dto2 = items.First(x => x.Id == s2.Id);
            Assert.Equal(0m, dto2.MinPrice);
            Assert.Equal(0m, dto2.MaxPrice);
        }

        [Fact]
        public async Task Test13_14_15_16_RedisCache_Behavior_Key_And_TTL()
        {
            // Arrange
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "Fest", Location = "Can Tho", TotalSeats = 100 };
            _context.Events.Add(ev);

            var s1 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 50, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            s1.ChangeStatus(ShowtimeStatus.OnSale);

            _context.Showtimes.Add(s1);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            string expectedCacheKey = "public:showtimes:cursor:none:limit:10";

            // Act 1: Cache Miss (First Call)
            var res1 = await controller.GetOnSaleShowtimes(cursor: null, limit: 10);
            Assert.IsType<OkObjectResult>(res1);

            // Assert Cache Miss -> Ghi key vào cache với TTL 30s
            Assert.True(_inMemoryCacheStore.ContainsKey(expectedCacheKey));
            var cacheEntry = _inMemoryCacheStore[expectedCacheKey];
            Assert.Equal(TimeSpan.FromSeconds(30), cacheEntry.Options.AbsoluteExpirationRelativeToNow);

            // Verify SetAsync was called on IDistributedCache
            _mockCache.Verify(c => c.SetAsync(
                expectedCacheKey,
                It.IsAny<byte[]>(),
                It.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromSeconds(30)),
                It.IsAny<CancellationToken>()
            ), Times.Once);

            // Act 2: Cache Hit (Second Call)
            var res2 = await controller.GetOnSaleShowtimes(cursor: null, limit: 10);
            Assert.IsType<OkObjectResult>(res2);

            // Verify GetAsync was called twice
            _mockCache.Verify(c => c.GetAsync(expectedCacheKey, It.IsAny<CancellationToken>()), Times.Exactly(2));

            // Act 3: Call with different limit/cursor -> Should use different cache key
            string expectedCacheKey2 = "public:showtimes:cursor:none:limit:20";
            await controller.GetOnSaleShowtimes(cursor: null, limit: 20);
            Assert.True(_inMemoryCacheStore.ContainsKey(expectedCacheKey2));
        }

        [Fact]
        public async Task Test17_NoSensitiveDataLeak()
        {
            // Arrange
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "Fest", Location = "Can Tho", TotalSeats = 100 };
            _context.Events.Add(ev);

            var s1 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 50, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            s1.ChangeStatus(ShowtimeStatus.OnSale);

            _context.Showtimes.Add(s1);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            // Act
            var result = await controller.GetOnSaleShowtimes();
            var okResult = Assert.IsType<OkObjectResult>(result);
            string jsonString = JsonSerializer.Serialize(okResult.Value);

            // Assert: Ensure no sensitive field names exist in serialized JSON output
            Assert.DoesNotContain("password", jsonString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passwordHash", jsonString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerId", jsonString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connectionString", jsonString, StringComparison.OrdinalIgnoreCase);
        }
    }
}
