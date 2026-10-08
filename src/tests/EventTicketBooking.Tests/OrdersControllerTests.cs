using System.Security.Claims;
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

namespace EventTicketBooking.Tests;

public sealed class OrdersControllerTests
{
    [Fact]
    public async Task CreateOrderFromHolds_AllowsCurrentUsersHeldSeatWithInMemoryDatabase()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showtimeId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AppDbContext(options);
        var category = new SeatCategory
        {
            Id = categoryId,
            ShowtimeId = showtimeId,
            Name = "Standard",
            Price = 100_000
        };
        var seat = new Seat
        {
            Id = seatId,
            ShowtimeId = showtimeId,
            SeatCategoryId = categoryId,
            SeatCategory = category,
            Row = "A",
            SeatNumber = 1,
            Status = "HELD"
        };
        var showtime = new Showtime
        {
            Id = showtimeId,
            EventId = eventId,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            AvailableSeats = 1
        };
        showtime.ChangeStatus(ShowtimeStatus.OnSale);
        var ev = new Event
        {
            Id = eventId,
            Title = "Test event",
            Location = "Test venue",
            TotalSeats = 1,
            Showtimes = new List<Showtime> { showtime }
        };

        context.Seats.Add(seat);
        context.SeatCategories.Add(category);
        context.Showtimes.Add(showtime);
        context.Events.Add(ev);
        context.SeatHold.Add(new SeatHolds
        {
            SeatId = seatId,
            UserId = userId,
            Seat = seat,
            Status = "ACTIVE",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        await context.SaveChangesAsync();

        var controller = new OrdersController(context, NullLogger<OrdersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                    ], "Test"))
                }
            }
        };

        var result = await controller.CreateOrderFromHolds(showtimeId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<OrderDto>>(ok.Value);
        Assert.True(response.Success);
        var order = Assert.IsType<OrderDto>(response.Data);
        Assert.Equal(100_000, order.TotalAmount);
        Assert.Equal(seatId, Assert.Single(order.Items).SeatId);
    }
}
