using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests;

public class PublicEventListingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listing_PaginatesEventsWithoutDuplicates_OnlyCountsEligibleShowtimes(bool relational)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (relational) options.UseSqlite(connection); else options.UseInMemoryDatabase(Guid.NewGuid().ToString());
        await using var db = new AppDbContext(options.Options);
        await db.Database.EnsureCreatedAsync();
        var owner = new User { Username = "owner", FullName = "Organizer", Email = "owner@test.local", PasswordHash = "hash" };
        var first = new Event { Title = "First", Owner = owner, OwnerId = owner.Id, Location = "Venue", TotalSeats = 50 };
        var second = new Event { Title = "Second", Owner = owner, OwnerId = owner.Id, Location = "Venue" };
        var hidden = new Event { Title = "Hidden", Owner = owner, OwnerId = owner.Id, Location = "Venue" };
        Showtime Add(Event ev, int days, int price, bool onSale)
        {
            var show = new Showtime
            {
                Event = ev,
                EventId = ev.Id,
                AvailableSeats = 50,
                StartTime = DateTime.UtcNow.AddDays(days),
                EndTime = DateTime.UtcNow.AddDays(days).AddHours(2)
            };
            show.SeatCategories.Add(new SeatCategory { ShowtimeId = show.Id, Name = "Standard", Price = price });
            if (onSale) show.ChangeStatus(ShowtimeStatus.OnSale);
            db.Showtimes.Add(show); return show;
        }
        var primary = Add(first, 1, 100000, true); Add(first, 2, 200000, true);
        Add(first, 3, 1, false); Add(first, -3, 1, true);
        Add(second, 4, 0, true); Add(hidden, 5, 1, false);
        var seat = new Seat { ShowtimeId = primary.Id, SeatCategoryId = primary.SeatCategories.First().Id, Row = "A", SeatNumber = 1, Status = "SOLD" };
        db.Seats.Add(seat);
        db.Seats.Add(new Seat { ShowtimeId = primary.Id, SeatCategoryId = primary.SeatCategories.First().Id, Row = "A", SeatNumber = 2 });
        var order = new Order { ShowtimeId = primary.Id, UserId = owner.Id, Status = OrderStatus.Paid };
        db.Orders.Add(order);
        db.OrderItems.Add(new OrderItem { OrderId = order.Id, SeatId = seat.Id, Price = 100000 });
        await db.SaveChangesAsync();
        var cache = new TestDistributedCache();
        var controller = new PublicShowtimeController(db, cache, NullLogger<PublicShowtimeController>.Instance);
        async Task<CursorPagedResultDto<PublicEventSummaryDto>> Page(string? cursor) =>
            Assert.IsType<ApiResponse<CursorPagedResultDto<PublicEventSummaryDto>>>(
                Assert.IsType<OkObjectResult>(await controller.GetOnSaleEvents(cursor, 1)).Value).Data!;
        var a = await Page(null);
        Assert.Equal(first.Id, Assert.Single(a.Items).EventId);
        Assert.Equal(2, a.Items[0].ShowtimeCount);
        Assert.Equal(100000, a.Items[0].MinPrice);
        Assert.True(a.HasMore);
        var b = await Page(a.NextCursor);
        Assert.Equal(second.Id, Assert.Single(b.Items).EventId);
        Assert.False(b.HasMore);
        Assert.Equal(0, b.Items[0].MinPrice);
        Assert.IsType<BadRequestResult>(await controller.GetOnSaleEvents("invalid"));
        var detail = Assert.IsType<ApiResponse<PublicEventDetailDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetPublicEventDetail(first.Id)).Value).Data!;
        Assert.Equal(2, detail.Showtimes.Count);
        Assert.All(detail.Showtimes, s => { Assert.Equal("OnSale", s.Status); Assert.True(s.EndTime > DateTime.UtcNow); });
        Assert.All(cache.Store.Values, value => Assert.Equal(TimeSpan.FromSeconds(30), value.Options.AbsoluteExpirationRelativeToNow));
        var organizer = new EventController(db)
        {
            ControllerContext = new()
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, owner.Id.ToString())
            }, "test"))
                }
            }
        };
        var managedEvents = Assert.IsType<ApiResponse<List<EventResponseDto>>>(Assert.IsType<OkObjectResult>(await organizer.GetMyEvents()).Value).Data!;
        var managed = managedEvents.Single(e => e.Id == first.Id);
        Assert.Equal(2, managed.Showtimes.Sum(s => s.ActualSeatCount));
        Assert.Equal(1, managed.Showtimes.Sum(s => s.SoldSeatCount));
    }
}
