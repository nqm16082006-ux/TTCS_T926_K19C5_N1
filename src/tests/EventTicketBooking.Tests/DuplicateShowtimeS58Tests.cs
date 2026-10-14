using System.Security.Claims;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventTicketBooking.Tests;

public class DuplicateShowtimeS58Tests
{
    private static EventController Controller(AppDbContext db, Guid? user, string role = "Organizer") => new(db)
    {
        ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = user.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Value.ToString()), new Claim(ClaimTypes.Role, role) }, "test"))
            : new ClaimsPrincipal()
            }
        }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_CopiesOnlySelectedShowtime_ResetsSeatsAndPreservesSource(bool relational)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (relational) options.UseSqlite(connection); else options.UseInMemoryDatabase(Guid.NewGuid().ToString());
        await using var db = new AppDbContext(options.Options);
        await db.Database.EnsureCreatedAsync();
        var owner = new User { Username = "owner", Email = "owner@test.local", PasswordHash = "hash", IsActive = true };
        var ev = new Event { Title = "Concert", Location = "Venue", Owner = owner, OwnerId = owner.Id };
        var source = new Showtime
        {
            Event = ev,
            EventId = ev.Id,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            AvailableSeats = 2,
            MaxTicketsPerUser = 6,
            SeatingMapImageUrl = "/uploads/events/map.png"
        };
        var vip = new SeatCategory { Showtime = source, ShowtimeId = source.Id, Name = "VIP", Price = 100000 };
        var standard = new SeatCategory { Showtime = source, ShowtimeId = source.Id, Name = "Standard", Price = 50000 };
        source.SeatCategories.Add(vip); source.SeatCategories.Add(standard);
        source.ChangeStatus(ShowtimeStatus.OnSale);
        var sold = new Seat { ShowtimeId = source.Id, SeatCategoryId = vip.Id, Row = "A", SeatNumber = 1, Status = "SOLD" };
        var held = new Seat { ShowtimeId = source.Id, SeatCategoryId = standard.Id, Row = "B", SeatNumber = 1, Status = "HELD" };
        var order = new Order { ShowtimeId = source.Id, UserId = owner.Id, Status = OrderStatus.Paid };
        db.Showtimes.Add(source);
        db.Showtimes.Add(new Showtime { Event = ev, EventId = ev.Id, StartTime = source.StartTime.AddDays(5), EndTime = source.EndTime.AddDays(5) });
        db.Seats.AddRange(sold, held); db.Orders.Add(order);
        db.OrderItems.Add(new OrderItem { OrderId = order.Id, SeatId = sold.Id, IsCheckedIn = true, CheckInGate = "A", CheckInTime = DateTimeOffset.UtcNow });
        db.SeatHold.Add(new SeatHolds { SeatId = held.Id, UserId = owner.Id, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();
        var request = new DuplicateShowtimeDto { Name = "  Đêm Chung kết  ", StartTime = source.StartTime.AddDays(1), EndTime = source.EndTime.AddDays(1) };
        var controller = Controller(db, owner.Id);
        var created = Assert.IsType<ObjectResult>(await controller.DuplicateShowtime(ev.Id, source.Id, request));
        Assert.Equal(201, created.StatusCode);
        var result = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(created.Value).Data!;
        Assert.Equal(ev.Id, result.EventId);
        Assert.Equal("Đêm Chung kết", result.Name);
        Assert.Equal("Đêm Chung kết", (await db.Showtimes.AsNoTracking().SingleAsync(s => s.Id == result.Id)).Name);
        Assert.NotEqual(source.Id, result.Id);
        Assert.Equal(ShowtimeStatus.Draft, result.Status);
        Assert.Equal(request.StartTime, result.StartTime);
        Assert.Equal(request.EndTime, result.EndTime);
        Assert.Equal(2, result.AvailableSeats);
        Assert.Equal(6, result.MaxTicketsPerUser);
        Assert.Equal(source.SeatingMapImageUrl, result.SeatingMapImageUrl);
        Assert.Equal(new[] { 50000, 100000 }, result.SeatCategories.Select(c => c.Price!.Value).Order().ToArray());
        var copied = await db.Seats.AsNoTracking().Where(s => s.ShowtimeId == result.Id).ToListAsync();
        Assert.Equal(2, copied.Count);
        Assert.All(copied, s => { Assert.Equal("AVAILABLE", s.Status); Assert.NotEqual(sold.Id, s.Id); Assert.NotEqual(held.Id, s.Id); });
        Assert.All(result.SeatCategories, c => { Assert.NotEqual(vip.Id, c.Id); Assert.NotEqual(standard.Id, c.Id); });
        Assert.Equal(1, await db.Events.CountAsync());
        Assert.Equal(3, await db.Showtimes.CountAsync());
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.OrderItems.CountAsync());
        Assert.Equal(1, await db.SeatHold.CountAsync());
        Assert.Equal("SOLD", (await db.Seats.AsNoTracking().SingleAsync(s => s.Id == sold.Id)).Status);
        Assert.Equal(ShowtimeStatus.OnSale, (await db.Showtimes.AsNoTracking().SingleAsync(s => s.Id == source.Id)).Status);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Controller(db, Guid.NewGuid()).DuplicateShowtime(ev.Id, source.Id, request)).StatusCode);
        Assert.IsType<UnauthorizedObjectResult>(await Controller(db, null).DuplicateShowtime(ev.Id, source.Id, request));
        Assert.IsType<NotFoundObjectResult>(await controller.DuplicateShowtime(Guid.NewGuid(), source.Id, request));
        Assert.IsType<BadRequestObjectResult>(await controller.DuplicateShowtime(ev.Id, source.Id, new() { StartTime = source.StartTime, EndTime = source.EndTime }));
        Assert.IsType<BadRequestObjectResult>(await controller.DuplicateShowtime(ev.Id, source.Id, new() { StartTime = source.EndTime, EndTime = source.StartTime }));
        Assert.IsType<BadRequestObjectResult>(await controller.DuplicateShowtime(ev.Id, source.Id, new()));
        Assert.Equal(3, await db.Showtimes.CountAsync());
        Assert.Equal(201, Assert.IsType<ObjectResult>(await Controller(db, Guid.NewGuid(), "Admin").DuplicateShowtime(ev.Id, source.Id, request)).StatusCode);
        var named = Assert.IsType<ObjectResult>(await controller.CreateShowtime(ev.Id, new CreateShowtimeDto
        {
            Name = "  Đêm Chung khảo  ", StartTime = source.StartTime.AddDays(10),
            EndTime = source.EndTime.AddDays(10), AvailableSeats = 2
        }));
        var namedDto = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(named.Value).Data!;
        Assert.Equal("Đêm Chung khảo", namedDto.Name);
        var edited = Assert.IsType<OkObjectResult>(await controller.UpdateShowtime(ev.Id, namedDto.Id, new UpdateShowtimeDto
        {
            Name = "Đêm Chung kết", StartTime = namedDto.StartTime, EndTime = namedDto.EndTime
        }));
        Assert.Equal("Đêm Chung kết", Assert.IsType<ApiResponse<ShowtimeResponseDto>>(edited.Value).Data!.Name);
        Assert.Equal("Đêm Chung kết", (await db.Showtimes.AsNoTracking().SingleAsync(s => s.Id == namedDto.Id)).Name);
    }

    [Fact]
    public async Task Duplicate_EmptyDraft_DoesNotInventSeatsOrPrices()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        var ev = new Event { OwnerId = owner, Title = "Empty", Location = "Venue" };
        var source = new Showtime { Event = ev, EventId = ev.Id, AvailableSeats = 4000, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2) };
        source.SeatCategories.Add(new SeatCategory { ShowtimeId = source.Id, Name = "Chưa có giá", Price = null });
        db.Showtimes.Add(source); await db.SaveChangesAsync();
        var result = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(Assert.IsType<ObjectResult>(await Controller(db, owner).DuplicateShowtime(ev.Id, source.Id,
            new() { StartTime = source.StartTime.AddDays(1), EndTime = source.EndTime.AddDays(1) })).Value).Data!;
        Assert.Equal(0, result.AvailableSeats);
        Assert.Null(Assert.Single(result.SeatCategories).Price);
        Assert.False(result.CanOpenSale);
    }
}
