using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class EventControllerSeatCategoryPriceTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly EventController _controller;
        private readonly Guid _userId = Guid.NewGuid();

        public EventControllerSeatCategoryPriceTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim(ClaimTypes.Role, "Organizer")
            }, "TestAuthType"));

            _controller = new EventController(_context)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = principal }
                }
            };
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        [Fact]
        public async Task UpdatePrices_ShouldReturnBadRequestAndKeepPrice_WhenPriceIsNegative()
        {
            var (ev, showtime, category) = await SeedCategoryAsync(_userId, 120000);

            var result = await _controller.UpdateSeatCategoryPrices(
                ev.Id,
                showtime.Id,
                CreateRequest(category.Id, -1));

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(120000, (await _context.SeatCategories.FindAsync(category.Id))!.Price);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(50000)]
        public async Task UpdatePrices_ShouldSaveValidIntegerPrice(int price)
        {
            var (ev, showtime, category) = await SeedCategoryAsync(_userId, null);

            var result = await _controller.UpdateSeatCategoryPrices(
                ev.Id,
                showtime.Id,
                CreateRequest(category.Id, price));

            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<List<SeatCategoryPriceDto>>>(okResult.Value);
            Assert.Equal(price, Assert.Single(response.Data!).Price);
            Assert.Equal(price, (await _context.SeatCategories.FindAsync(category.Id))!.Price);
        }

        [Fact]
        public async Task UpdatePrices_ShouldKeepNull_WhenPriceIsNull()
        {
            var (ev, showtime, category) = await SeedCategoryAsync(_userId, null);

            var result = await _controller.UpdateSeatCategoryPrices(
                ev.Id,
                showtime.Id,
                CreateRequest(category.Id, null));

            Assert.IsType<OkObjectResult>(result);
            Assert.Null((await _context.SeatCategories.FindAsync(category.Id))!.Price);
        }

        [Fact]
        public async Task UpdatePrices_ShouldRejectEntireRequest_WhenCategoryBelongsToAnotherShowtime()
        {
            var (ev, showtime, category) = await SeedCategoryAsync(_userId, 100000);
            var otherShowtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(2),
                AvailableSeats = 10
            };
            var otherCategory = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = otherShowtime.Id,
                Name = "Standard",
                Price = 200000
            };
            otherShowtime.SeatCategories.Add(otherCategory);
            _context.Showtimes.Add(otherShowtime);
            await _context.SaveChangesAsync();

            var request = new UpdateSeatCategoryPricesDto
            {
                Categories = new List<UpdateSeatCategoryPriceItemDto>
                {
                    new() { SeatCategoryId = category.Id, Price = 150000 },
                    new() { SeatCategoryId = otherCategory.Id, Price = 250000 }
                }
            };

            var result = await _controller.UpdateSeatCategoryPrices(ev.Id, showtime.Id, request);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(100000, (await _context.SeatCategories.FindAsync(category.Id))!.Price);
            Assert.Equal(200000, (await _context.SeatCategories.FindAsync(otherCategory.Id))!.Price);
        }

        [Fact]
        public async Task UpdatePrices_ShouldReturnForbidden_WhenCurrentUserIsNotOwner()
        {
            var (ev, showtime, category) = await SeedCategoryAsync(Guid.NewGuid(), 100000);

            var result = await _controller.UpdateSeatCategoryPrices(
                ev.Id,
                showtime.Id,
                CreateRequest(category.Id, 50000));

            var forbidden = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
            Assert.Equal(100000, (await _context.SeatCategories.FindAsync(category.Id))!.Price);
        }

        [Fact]
        public async Task GetEventShowtimes_ShouldIncludeSeatCategoryPrices()
        {
            var (ev, _, category) = await SeedCategoryAsync(_userId, 50000);

            var result = await _controller.GetEventShowtimes(ev.Id);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ApiResponse<List<ShowtimeResponseDto>>>(okResult.Value);
            var categoryDto = Assert.Single(Assert.Single(response.Data!).SeatCategories);
            Assert.Equal(category.Id, categoryDto.Id);
            Assert.Equal("VIP", categoryDto.Name);
            Assert.Equal(50000, categoryDto.Price);
        }

        private async Task<(Event Event, Showtime Showtime, SeatCategory Category)> SeedCategoryAsync(
            Guid ownerId,
            int? price)
        {
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                Title = "Test Event",
                Location = "Hanoi",
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
                TotalSeats = 10
            };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = ev.StartTime,
                EndTime = ev.EndTime,
                AvailableSeats = 10
            };
            var category = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Name = "VIP",
                Price = price
            };

            showtime.SeatCategories.Add(category);
            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);
            await _context.SaveChangesAsync();

            return (ev, showtime, category);
        }

        private static UpdateSeatCategoryPricesDto CreateRequest(Guid categoryId, int? price)
        {
            return new UpdateSeatCategoryPricesDto
            {
                Categories = new List<UpdateSeatCategoryPriceItemDto>
                {
                    new() { SeatCategoryId = categoryId, Price = price }
                }
            };
        }
    }
}
