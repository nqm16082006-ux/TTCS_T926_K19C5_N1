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

public class SeatingMapImageTests
{
    [Fact]
    public async Task OwnerCanSaveAndClearImage_OtherOrganizerCannotChangeIt()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        db.Users.Add(new User { Id = owner, Username = "organizer", Email = "organizer@test.local", PasswordHash = "test", FullName = "Organizer" });
        var ev = new Event { Id = Guid.NewGuid(), OwnerId = owner, Title = "Test", Location = "Test" };
        var showtime = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, Event = ev };
        db.Showtimes.Add(showtime); await db.SaveChangesAsync();
        EventController Controller(Guid user, string role = "Organizer") => new(db)
        {
            ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                    new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Role, role)
                }, "test"))
                }
            }
        };
        var controller = Controller(owner);
        Assert.IsType<OkObjectResult>(await controller.UpdateSeatingMapImage(ev.Id, showtime.Id, new() { ImageUrl = "https://example.com/map.png" }));
        Assert.Equal("https://example.com/map.png", showtime.SeatingMapImageUrl);
        var shows = Assert.IsType<ApiResponse<List<ShowtimeResponseDto>>>(
            Assert.IsType<OkObjectResult>(await controller.GetEventShowtimes(ev.Id)).Value);
        Assert.Equal(showtime.SeatingMapImageUrl, Assert.Single(shows.Data!).SeatingMapImageUrl);
        var stranger = Controller(Guid.NewGuid());
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await stranger.UpdateSeatingMapImage(ev.Id, showtime.Id, new() { ImageUrl = null })).StatusCode);
        foreach (var url in new[] { "javascript:alert(1)", "data:image/svg+xml,test", "//evil.test/map.png", "/uploads/events/../secret", new string('x', 2049) })
            Assert.IsType<BadRequestObjectResult>(await controller.UpdateSeatingMapImage(ev.Id, showtime.Id, new() { ImageUrl = url }));
        Assert.Equal("https://example.com/map.png", showtime.SeatingMapImageUrl);
        Assert.IsType<OkObjectResult>(await Controller(Guid.NewGuid(), "Admin").UpdateSeatingMapImage(ev.Id, showtime.Id, new() { ImageUrl = "/uploads/events/map.png" }));
        var cache = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        var publicController = new PublicShowtimeController(db, cache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PublicShowtimeController>.Instance);
        var publicEvent = Assert.IsType<ApiResponse<PublicEventDetailDto>>(
            Assert.IsType<OkObjectResult>(await publicController.GetPublicEventDetail(ev.Id)).Value);
        Assert.Equal("/uploads/events/map.png", Assert.Single(publicEvent.Data!.Showtimes).SeatingMapImageUrl);
        Assert.IsType<NotFoundResult>(await controller.UpdateSeatingMapImage(Guid.NewGuid(), showtime.Id, new()));
        Assert.IsType<OkObjectResult>(await controller.UpdateSeatingMapImage(ev.Id, showtime.Id, new() { ImageUrl = null }));
        Assert.Null(showtime.SeatingMapImageUrl);
    }
}
