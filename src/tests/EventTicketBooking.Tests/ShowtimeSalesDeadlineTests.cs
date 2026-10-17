using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace EventTicketBooking.Tests;

public class ShowtimeSalesDeadlineTests
{
    [Fact]
    public void Deadline_DefaultsToStart_InclusiveBoundary_AndCannotExceedEnd()
    {
        var now = DateTime.UtcNow;
        var show = new Showtime { StartTime = now, EndTime = now.AddHours(2) };
        Assert.True(show.IsSaleExpired(now));
        show.SalesEndTime = now.AddHours(1);
        Assert.False(show.IsSaleExpired(now));
        Assert.True(show.IsSaleExpired(show.SalesEndTime.Value));
        var dto = new CreateShowtimeDto { StartTime = now, EndTime = now.AddHours(2), SalesEndTime = now.AddHours(3) };
        Assert.Contains(dto.Validate(new ValidationContext(dto)), error => error.MemberNames.Contains("SalesEndTime"));
    }

    [Fact]
    public async Task ExpiredDeadline_BlocksHoldAndOrder_EvenBeforeWorkerClosesStatus()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var show = new Showtime { StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2), SalesEndTime = DateTime.UtcNow.AddMinutes(-1), AvailableSeats = 1 };
        show.ChangeStatus(ShowtimeStatus.OnSale);
        var ev = new Event { Title = "Test", Location = "Venue" };
        show.Event = ev;
        show.EventId = ev.Id;
        db.Events.Add(ev);
        db.Showtimes.Add(show);
        await db.SaveChangesAsync();
        var user = Guid.NewGuid();
        var held = await new SeatHoldService(db, NullLogger<SeatHoldService>.Instance).HoldSeatsAsync(show.Id, new List<Guid> { Guid.NewGuid() }, user);
        Assert.Equal(EventTicketBooking.Api.Services.Interfaces.HoldSeatsResultStatus.InvalidRequest, held.Status);
        Assert.Contains("hết thời gian bán vé", held.Message);
        var orders = new OrdersController(db, NullLogger<OrdersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.ToString()) }, "test")) } }
        };
        var result = Assert.IsType<ConflictObjectResult>(await orders.CreateOrderFromHolds(show.Id));
        Assert.Contains("hết thời gian bán vé", Assert.IsType<ApiResponse<object>>(result.Value).Message);
        Assert.Equal(ShowtimeStatus.OnSale, show.Status);
        Assert.Empty(db.Orders);
    }
}
