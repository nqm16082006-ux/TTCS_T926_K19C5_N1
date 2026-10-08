using System.Security.Claims;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventTicketBooking.Tests;

public class EventControllerSalesTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Guid _organizerId = Guid.NewGuid();

    public EventControllerSalesTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task GetSalesByShowtime_ReportsPaidRevenueAndOnlyCurrentHoldsForOrganizerEvents()
    {
        var startTime = DateTime.UtcNow.AddDays(1);
        var ev = CreateEvent(_organizerId, "Sự kiện của tôi", startTime);
        var showtime = CreateShowtime(ev, startTime, 7);
        var vip = CreateCategory(showtime, "VIP", 200000);
        var standard = CreateCategory(showtime, "Standard", 100000);

        var seats = Enumerable.Range(1, 7)
            .Select(number => new Seat
            {
                ShowtimeId = showtime.Id,
                SeatCategoryId = number == 2 || number == 5 || number == 6 || number == 7
                    ? standard.Id
                    : vip.Id,
                Row = "A",
                SeatNumber = number,
                Status = number <= 2 || number == 4 ? "SOLD" : number == 3 ? "HELD" : "AVAILABLE"
            })
            .ToArray();
        var paidOrder = CreateOrder(showtime, OrderStatus.Paid);
        var pendingOrder = CreateOrder(showtime, OrderStatus.Pending);
        var cancelledOrder = CreateOrder(showtime, OrderStatus.Cancelled);
        var expiredOrder = CreateOrder(showtime, OrderStatus.Expired);

        _context.Events.Add(ev);
        _context.Seats.AddRange(seats);
        _context.Orders.AddRange(paidOrder, pendingOrder, cancelledOrder, expiredOrder);
        _context.OrderItems.AddRange(
            new OrderItem { OrderId = paidOrder.Id, SeatId = seats[0].Id, Price = 185000 },
            new OrderItem { OrderId = paidOrder.Id, SeatId = seats[1].Id, Price = 90000 },
            new OrderItem { OrderId = paidOrder.Id, SeatId = seats[3].Id, Price = 175000 },
            new OrderItem { OrderId = pendingOrder.Id, SeatId = seats[4].Id, Price = 100000 },
            new OrderItem { OrderId = cancelledOrder.Id, SeatId = seats[5].Id, Price = 100000 },
            new OrderItem { OrderId = expiredOrder.Id, SeatId = seats[6].Id, Price = 100000 });
        _context.SeatHold.AddRange(
            new SeatHolds
            {
                SeatId = seats[2].Id,
                UserId = Guid.NewGuid(),
                Status = "ACTIVE",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            },
            new SeatHolds
            {
                SeatId = seats[3].Id,
                UserId = Guid.NewGuid(),
                Status = "ACTIVE",
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
            },
            new SeatHolds
            {
                SeatId = seats[4].Id,
                UserId = Guid.NewGuid(),
                Status = "RELEASED",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        var otherEvent = CreateEvent(Guid.NewGuid(), "Sự kiện của người khác", startTime);
        CreateShowtime(otherEvent, startTime, 0);
        _context.Events.Add(otherEvent);
        await _context.SaveChangesAsync();

        var controller = CreateController(_organizerId, "Organizer");
        var actionResult = await controller.GetSalesByShowtime();

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.Equal("no-store", controller.Response.Headers["Cache-Control"].ToString());
        var response = Assert.IsType<ApiResponse<ShowtimeSalesReportDto>>(okResult.Value);
        Assert.True(response.Success);
        var data = Assert.IsType<ShowtimeSalesReportDto>(response.Data);
        Assert.NotEqual(default, data.GeneratedAt);
        Assert.Equal(DateTimeKind.Utc, data.GeneratedAt.Kind);

        var report = Assert.Single(data.Showtimes);
        Assert.Equal(showtime.Id, report.ShowtimeId);
        Assert.Equal("Sự kiện của tôi", report.EventTitle);
        Assert.Equal(startTime, report.StartTime);
        Assert.Equal(startTime.AddHours(2), report.EndTime);
        Assert.Equal(3, report.TicketSoldCount);
        Assert.Equal(1, report.HeldSeatCount);
        Assert.Equal(3, report.AvailableSeatCount);
        Assert.Equal(3, report.AvailableTickets);
        Assert.Equal(3, report.RemainingTickets);

        var vipSales = Assert.Single(report.RevenueByCategory, category => category.Name == "VIP");
        Assert.Equal(vip.Id, vipSales.SeatCategoryId);
        Assert.Equal(2, vipSales.TicketSoldCount);
        Assert.Equal(0, vipSales.AvailableQuantity);
        Assert.Equal(0, vipSales.RemainingQuantity);
        Assert.Equal(360000, vipSales.Revenue);

        var standardSales = Assert.Single(report.RevenueByCategory, category => category.Name == "Standard");
        Assert.Equal(standard.Id, standardSales.SeatCategoryId);
        Assert.Equal(1, standardSales.TicketSoldCount);
        Assert.Equal(3, standardSales.AvailableQuantity);
        Assert.Equal(3, standardSales.RemainingQuantity);
        Assert.Equal(90000, standardSales.Revenue);
    }

    [Fact]
    public async Task GetEventShowtimes_ReportsRemainingTicketsByCategoryAndShowtime()
    {
        var startTime = DateTime.UtcNow.AddDays(1);
        var ev = CreateEvent(_organizerId, "Tồn vé theo hạng", startTime);
        var showtime = CreateShowtime(ev, startTime, 5);
        var vip = CreateCategory(showtime, "VIP", 200000);
        var standard = CreateCategory(showtime, "Standard", 100000);
        var seats = new[]
        {
            new Seat { ShowtimeId = showtime.Id, SeatCategoryId = vip.Id, Row = "A", SeatNumber = 1 },
            new Seat { ShowtimeId = showtime.Id, SeatCategoryId = vip.Id, Row = "A", SeatNumber = 2 },
            new Seat { ShowtimeId = showtime.Id, SeatCategoryId = vip.Id, Row = "A", SeatNumber = 3 },
            new Seat { ShowtimeId = showtime.Id, SeatCategoryId = standard.Id, Row = "B", SeatNumber = 1 },
            new Seat { ShowtimeId = showtime.Id, SeatCategoryId = standard.Id, Row = "B", SeatNumber = 2 }
        };
        var paidOrder = CreateOrder(showtime, OrderStatus.Paid);
        _context.Events.Add(ev);
        _context.Seats.AddRange(seats);
        _context.Orders.Add(paidOrder);
        _context.OrderItems.AddRange(
            new OrderItem { OrderId = paidOrder.Id, SeatId = seats[0].Id, Price = 200000 },
            new OrderItem { OrderId = paidOrder.Id, SeatId = seats[3].Id, Price = 100000 });
        _context.SeatHold.Add(new SeatHolds
        {
            SeatId = seats[1].Id,
            UserId = Guid.NewGuid(),
            Status = "ACTIVE",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        await _context.SaveChangesAsync();

        var actionResult = await CreateController(_organizerId, "Organizer").GetEventShowtimes(ev.Id);

        var response = Assert.IsType<ApiResponse<List<ShowtimeResponseDto>>>(
            Assert.IsType<OkObjectResult>(actionResult).Value);
        var result = Assert.Single(response.Data!);
        Assert.Equal(2, result.SoldSeatCount);
        Assert.Equal(1, result.HeldSeatCount);
        Assert.Equal(2, result.AvailableTickets);
        Assert.Equal(2, result.RemainingTickets);

        var vipResult = Assert.Single(result.SeatCategories, category => category.Id == vip.Id);
        Assert.Equal(3, vipResult.TotalQuantity);
        Assert.Equal(1, vipResult.HeldQuantity);
        Assert.Equal(1, vipResult.AvailableQuantity);
        Assert.Equal(1, vipResult.RemainingQuantity);

        var standardResult = Assert.Single(result.SeatCategories, category => category.Id == standard.Id);
        Assert.Equal(2, standardResult.TotalQuantity);
        Assert.Equal(0, standardResult.HeldQuantity);
        Assert.Equal(1, standardResult.AvailableQuantity);
        Assert.Equal(1, standardResult.RemainingQuantity);
    }

    [Fact]
    public async Task GetSalesByShowtime_AdminCanSeeAllOwnersShowtimesInStartTimeOrder()
    {
        var now = DateTime.UtcNow;
        var laterEvent = CreateEvent(Guid.NewGuid(), "Suất muộn", now);
        var laterShowtime = CreateShowtime(laterEvent, now.AddHours(2), 0);
        var earlierEvent = CreateEvent(Guid.NewGuid(), "Suất sớm", now);
        var earlierShowtime = CreateShowtime(earlierEvent, now, 0);
        _context.Events.AddRange(laterEvent, earlierEvent);
        await _context.SaveChangesAsync();

        var actionResult = await CreateController(Guid.NewGuid(), "Admin").GetSalesByShowtime();

        var response = Assert.IsType<ApiResponse<ShowtimeSalesReportDto>>(
            Assert.IsType<OkObjectResult>(actionResult).Value);
        var data = Assert.IsType<ShowtimeSalesReportDto>(response.Data);
        Assert.Collection(
            data.Showtimes,
            showtime =>
            {
                Assert.Equal(earlierShowtime.Id, showtime.ShowtimeId);
                Assert.Equal("Suất sớm", showtime.EventTitle);
                Assert.Empty(showtime.RevenueByCategory);
                Assert.Equal(0, showtime.TicketSoldCount);
                Assert.Equal(0, showtime.HeldSeatCount);
                Assert.Equal(0, showtime.AvailableSeatCount);
            },
            showtime =>
            {
                Assert.Equal(laterShowtime.Id, showtime.ShowtimeId);
                Assert.Equal("Suất muộn", showtime.EventTitle);
            });
    }

    [Fact]
    public async Task GetSalesByShowtime_AdminRoleInDatabaseCanSeeOtherOwnersShowtimes()
    {
        var admin = new User
        {
            Username = "sales-admin",
            Email = "sales-admin@example.test",
            PasswordHash = "test-hash"
        };
        var adminRole = new Role { Name = "Admin" };
        _context.Users.Add(admin);
        _context.Roles.Add(adminRole);
        _context.UserRoles.Add(new UserRole
        {
            UserId = admin.Id,
            RoleId = adminRole.Id
        });

        var ev = CreateEvent(Guid.NewGuid(), "Sự kiện quản trị", DateTime.UtcNow);
        var showtime = CreateShowtime(ev, ev.StartTime, 0);
        _context.Events.Add(ev);
        await _context.SaveChangesAsync();

        var actionResult = await CreateController(admin.Id, "Organizer").GetSalesByShowtime();

        var response = Assert.IsType<ApiResponse<ShowtimeSalesReportDto>>(
            Assert.IsType<OkObjectResult>(actionResult).Value);
        var report = Assert.Single(Assert.IsType<ShowtimeSalesReportDto>(response.Data).Showtimes);
        Assert.Equal(showtime.Id, report.ShowtimeId);
        Assert.Equal("Sự kiện quản trị", report.EventTitle);
    }

    [Fact]
    public async Task GetSalesByShowtime_WithoutUserId_ReturnsUnauthorized()
    {
        var controller = new EventController(_context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity())
                }
            }
        };

        var actionResult = await controller.GetSalesByShowtime();

        var result = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task GetSalesByShowtime_OrganizerWithNoEventsGetsEmptyReport()
    {
        var actionResult = await CreateController(_organizerId, "Organizer").GetSalesByShowtime();

        var response = Assert.IsType<ApiResponse<ShowtimeSalesReportDto>>(
            Assert.IsType<OkObjectResult>(actionResult).Value);
        var data = Assert.IsType<ShowtimeSalesReportDto>(response.Data);
        Assert.NotEqual(default, data.GeneratedAt);
        Assert.Empty(data.Showtimes);
    }

    private EventController CreateController(Guid userId, string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "TestAuthType"));

        return new EventController(_context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    private static Event CreateEvent(Guid ownerId, string title, DateTime startTime)
    {
        return new Event
        {
            OwnerId = ownerId,
            Title = title,
            Location = "Hà Nội",
            StartTime = startTime,
            EndTime = startTime.AddHours(2)
        };
    }

    private static Showtime CreateShowtime(Event ev, DateTime startTime, int seatCount)
    {
        var showtime = new Showtime
        {
            EventId = ev.Id,
            StartTime = startTime,
            EndTime = startTime.AddHours(2),
            AvailableSeats = seatCount
        };
        ev.Showtimes.Add(showtime);
        return showtime;
    }

    private static SeatCategory CreateCategory(Showtime showtime, string name, int price)
    {
        var category = new SeatCategory
        {
            ShowtimeId = showtime.Id,
            Name = name,
            Price = price
        };
        showtime.SeatCategories.Add(category);
        return category;
    }

    private static Order CreateOrder(Showtime showtime, OrderStatus status)
    {
        return new Order
        {
            UserId = Guid.NewGuid(),
            ShowtimeId = showtime.Id,
            Status = status,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
    }
}
