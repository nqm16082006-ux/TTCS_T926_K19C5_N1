using System;
using System.Collections.Generic;
using System.Linq;
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
using Xunit;

namespace EventTicketBooking.Tests
{
    public class TestDistributedCache : IDistributedCache
    {
        public readonly Dictionary<string, (byte[] Data, DistributedCacheEntryOptions Options)> Store = new();
        public int GetCount { get; private set; }
        public int SetCount { get; private set; }

        public byte[]? Get(string key)
        {
            GetCount++;
            return Store.TryGetValue(key, out var val) ? val.Data : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            return Task.FromResult(Get(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            SetCount++;
            Store[key] = (value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => Store.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { Store.Remove(key); return Task.CompletedTask; }
    }

    public class PublicShowtimeApiTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly TestDistributedCache _testCache;

        public PublicShowtimeApiTests()
        {
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
            var httpContext = new DefaultHttpContext();
            return new PublicShowtimeController(
                _context,
                _testCache,
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
            var controller = CreateController();
            var result = await controller.GetOnSaleShowtimes();

            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(okResult.Value);
            Assert.True(response.Success);
        }

        [Fact]
        public async Task Test2_3_4_Filter_ShouldOnlyReturnOnSaleShowtimes()
        {
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

            var draft = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddHours(2), EndTime = DateTime.UtcNow.AddHours(4) };

            var onSale = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddHours(5), EndTime = DateTime.UtcNow.AddHours(7) };
            onSale.ChangeStatus(ShowtimeStatus.OnSale);

            var closed = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddHours(8), EndTime = DateTime.UtcNow.AddHours(10) };
            closed.ChangeStatus(ShowtimeStatus.OnSale);
            closed.ChangeStatus(ShowtimeStatus.Closed);

            _context.Showtimes.AddRange(draft, onSale, closed);
            await _context.SaveChangesAsync();

            var controller = CreateController();
            var result = await controller.GetOnSaleShowtimes();

            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(okResult.Value);
            Assert.Single(response.Data.Items);
            Assert.Equal(onSale.Id, response.Data.Items.First().Id);
            Assert.Equal("OnSale", response.Data.Items.First().Status);
        }

        [Fact]
        public async Task Test5_6_7_8_9_CursorPagination_Sorting_NoDuplicates_NoMissing()
        {
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

            var page1Result = await controller.GetOnSaleShowtimes(cursor: null, limit: 2);
            var page1Ok = Assert.IsType<OkObjectResult>(page1Result);
            var page1Data = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(page1Ok.Value).Data;

            Assert.Equal(2, page1Data.Items.Count);
            Assert.True(page1Data.HasMore);
            Assert.NotNull(page1Data.NextCursor);
            Assert.True(page1Data.Items[0].StartTime <= page1Data.Items[1].StartTime);

            var page2Result = await controller.GetOnSaleShowtimes(cursor: page1Data.NextCursor, limit: 2);
            var page2Ok = Assert.IsType<OkObjectResult>(page2Result);
            var page2Data = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(page2Ok.Value).Data;

            Assert.Equal(2, page2Data.Items.Count);
            Assert.True(page2Data.HasMore);
            Assert.NotNull(page2Data.NextCursor);

            var page3Result = await controller.GetOnSaleShowtimes(cursor: page2Data.NextCursor, limit: 2);
            var page3Ok = Assert.IsType<OkObjectResult>(page3Result);
            var page3Data = Assert.IsType<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(page3Ok.Value).Data;

            Assert.Single(page3Data.Items);
            Assert.False(page3Data.HasMore);
            Assert.Null(page3Data.NextCursor);

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
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "Music Fest", Location = "Da Nang", TotalSeats = 500 };
            _context.Events.Add(ev);

            var s1 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 100, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            s1.ChangeStatus(ShowtimeStatus.OnSale);

            s1.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s1.Id, Name = "Standard", Price = 200000m });
            s1.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s1.Id, Name = "VIP", Price = 800000m });
            s1.SeatCategories.Add(new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = s1.Id, Name = "VVIP", Price = 1500000m });

            var s2 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 50, StartTime = DateTime.UtcNow.AddDays(2), EndTime = DateTime.UtcNow.AddDays(2).AddHours(2) };
            s2.ChangeStatus(ShowtimeStatus.OnSale);

            _context.Showtimes.AddRange(s1, s2);
            await _context.SaveChangesAsync();

            var controller = CreateController();
            var result = await controller.GetOnSaleShowtimes(limit: 10);

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
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "Fest", Location = "Can Tho", TotalSeats = 100 };
            _context.Events.Add(ev);

            var s1 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 50, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            s1.ChangeStatus(ShowtimeStatus.OnSale);

            _context.Showtimes.Add(s1);
            await _context.SaveChangesAsync();

            var controller = CreateController();
            string expectedCacheKey = "public:showtimes:cursor:none:limit:10";

            var res1 = await controller.GetOnSaleShowtimes(cursor: null, limit: 10);
            Assert.IsType<OkObjectResult>(res1);

            Assert.True(_testCache.Store.ContainsKey(expectedCacheKey));
            var cacheEntry = _testCache.Store[expectedCacheKey];
            Assert.Equal(TimeSpan.FromSeconds(30), cacheEntry.Options.AbsoluteExpirationRelativeToNow);
            Assert.Equal(1, _testCache.SetCount);

            var res2 = await controller.GetOnSaleShowtimes(cursor: null, limit: 10);
            Assert.IsType<OkObjectResult>(res2);

            Assert.Equal(2, _testCache.GetCount);

            string expectedCacheKey2 = "public:showtimes:cursor:none:limit:20";
            await controller.GetOnSaleShowtimes(cursor: null, limit: 20);
            Assert.True(_testCache.Store.ContainsKey(expectedCacheKey2));
        }

        [Fact]
        public async Task Test17_NoSensitiveDataLeak()
        {
            var ev = new Event { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Title = "Fest", Location = "Can Tho", TotalSeats = 100 };
            _context.Events.Add(ev);

            var s1 = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, AvailableSeats = 50, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
            s1.ChangeStatus(ShowtimeStatus.OnSale);

            _context.Showtimes.Add(s1);
            await _context.SaveChangesAsync();

            var controller = CreateController();

            var result = await controller.GetOnSaleShowtimes();
            var okResult = Assert.IsType<OkObjectResult>(result);
            string jsonString = JsonSerializer.Serialize(okResult.Value);

            Assert.DoesNotContain("password", jsonString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passwordHash", jsonString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerId", jsonString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connectionString", jsonString, StringComparison.OrdinalIgnoreCase);
        }
    }
}
