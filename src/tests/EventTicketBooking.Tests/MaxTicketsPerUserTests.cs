using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class MaxTicketsPerUserTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Guid _organizerId = Guid.NewGuid();
        private readonly Guid _buyerId = Guid.NewGuid();

        public MaxTicketsPerUserTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new AppDbContext(options);

            // Seed organizer user
            _context.Users.Add(new User
            {
                Id = _organizerId,
                Username = "organizer_test",
                Email = "organizer@example.com",
                PasswordHash = "hash",
                FullName = "Ban Tổ Chức Test",
                IsActive = true
            });
            _context.SaveChanges();
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private EventController CreateEventController(Guid userId, string role = "Organizer")
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var principal = new ClaimsPrincipal(identity);
            var httpContext = new DefaultHttpContext { User = principal };

            return new EventController(_context)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        }

        private OrdersController CreateOrdersController(Guid userId)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, "Customer")
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var principal = new ClaimsPrincipal(identity);
            var httpContext = new DefaultHttpContext { User = principal };

            var mockQueue = new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>();
            return new OrdersController(_context, NullLogger<OrdersController>.Instance, mockQueue.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        }

        [Fact]
        public async Task CreateEvent_WithCustomMaxTicketsPerUser_ShouldPersistAndPropagateToShowtime()
        {
            // Arrange
            var controller = CreateEventController(_organizerId);
            var dto = new CreateEventDto
            {
                Title = "Concert Acoustic Night",
                Location = "Hanoi Opera House",
                StartTime = DateTime.UtcNow.AddDays(7),
                EndTime = DateTime.UtcNow.AddDays(7).AddHours(3),
                TotalSeats = 200,
                MaxTicketsPerUser = 4
            };

            // Act
            var result = await controller.CreateEvent(dto) as ObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
            var apiResponse = Assert.IsType<ApiResponse<EventResponseDto>>(result.Value);
            Assert.True(apiResponse.Success);
            Assert.Equal(4, apiResponse.Data.MaxTicketsPerUser);

            var createdEvent = await _context.Events.Include(e => e.Showtimes).FirstAsync(e => e.Id == apiResponse.Data.Id);
            Assert.Equal(4, createdEvent.MaxTicketsPerUser);
            Assert.Single(createdEvent.Showtimes);
            Assert.Equal(4, createdEvent.Showtimes.First().MaxTicketsPerUser);
        }

        [Fact]
        public async Task CreateEvent_WithoutMaxTicketsPerUser_ShouldDefaultTo10()
        {
            // Arrange
            var controller = CreateEventController(_organizerId);
            var dto = new CreateEventDto
            {
                Title = "Classical Symphony",
                Location = "Saigon Opera House",
                StartTime = DateTime.UtcNow.AddDays(10),
                EndTime = DateTime.UtcNow.AddDays(10).AddHours(3),
                TotalSeats = 500,
                MaxTicketsPerUser = null
            };

            // Act
            var result = await controller.CreateEvent(dto) as ObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
            var apiResponse = Assert.IsType<ApiResponse<EventResponseDto>>(result.Value);
            Assert.Equal(10, apiResponse.Data.MaxTicketsPerUser);
        }

        [Fact]
        public async Task UpdateEvent_ShouldUpdateMaxTicketsPerUserAndShowtimes()
        {
            // Arrange
            var controller = CreateEventController(_organizerId);
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _organizerId,
                Title = "Rock Fest",
                Location = "My Dinh Stadium",
                StartTime = DateTime.UtcNow.AddDays(5),
                EndTime = DateTime.UtcNow.AddDays(5).AddHours(4),
                TotalSeats = 1000,
                MaxTicketsPerUser = 10
            };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = ev.StartTime,
                EndTime = ev.EndTime,
                AvailableSeats = 1000,
                MaxTicketsPerUser = 10
            };
            ev.Showtimes.Add(showtime);
            _context.Events.Add(ev);
            await _context.SaveChangesAsync();

            var updateDto = new UpdateEventDto
            {
                Title = "Rock Fest Updated",
                Location = "My Dinh Stadium",
                StartTime = ev.StartTime,
                EndTime = ev.EndTime,
                TotalSeats = 1000,
                MaxTicketsPerUser = 3
            };

            // Act
            var result = await controller.UpdateEvent(ev.Id, updateDto) as OkObjectResult;

            // Assert
            Assert.NotNull(result);
            var apiResponse = Assert.IsType<ApiResponse<EventResponseDto>>(result.Value);
            Assert.Equal(3, apiResponse.Data.MaxTicketsPerUser);

            var updatedShowtime = await _context.Showtimes.FirstAsync(s => s.Id == showtime.Id);
            Assert.Equal(3, updatedShowtime.MaxTicketsPerUser);
        }

        [Fact]
        public async Task HoldSeats_ExceedingMaxTicketsPerUser_ShouldReturnInvalidResult()
        {
            // Arrange
            var (showtime, seats) = await SeedShowtimeWithSeatsAsync(maxTickets: 3, seatCount: 5);
            var seatHoldService = new SeatHoldService(_context, NullLogger<SeatHoldService>.Instance, new Mock<IHubContext<SeatStatusHub>>().Object);

            var requestedSeatIds = new List<Guid> { seats[0].Id, seats[1].Id, seats[2].Id, seats[3].Id }; // 4 seats > limit 3

            // Act
            var result = await seatHoldService.HoldSeatsAsync(showtime.Id, requestedSeatIds, _buyerId);

            // Assert
            Assert.Equal(HoldSeatsResultStatus.InvalidRequest, result.Status);
            Assert.Contains("Tối đa 3 vé", result.Message);
        }

        [Fact]
        public async Task HoldSeats_WithinMaxTicketsPerUser_ShouldSucceed()
        {
            // Arrange
            var (showtime, seats) = await SeedShowtimeWithSeatsAsync(maxTickets: 3, seatCount: 5);
            var seatHoldService = new SeatHoldService(_context, NullLogger<SeatHoldService>.Instance, new Mock<IHubContext<SeatStatusHub>>().Object);

            var requestedSeatIds = new List<Guid> { seats[0].Id, seats[1].Id, seats[2].Id }; // 3 seats == limit 3

            // Act
            var result = await seatHoldService.HoldSeatsAsync(showtime.Id, requestedSeatIds, _buyerId);

            // Assert
            Assert.Equal(HoldSeatsResultStatus.Success, result.Status);
            Assert.NotNull(result.Data);
            Assert.Equal(3, result.Data.SeatIds.Count);
            Assert.Equal(3, result.Data.MaxTicketsPerUser);
            Assert.Equal(0, result.Data.PurchasedCount);
            Assert.Equal(3, result.Data.ActiveHoldCount);
            Assert.Equal(0, result.Data.RemainingAllowance);
        }

        [Fact]
        public async Task HoldSeats_AccumulatedAcrossExistingHolds_ShouldEnforceLimit()
        {
            // Arrange
            var (showtime, seats) = await SeedShowtimeWithSeatsAsync(maxTickets: 3, seatCount: 5);
            var seatHoldService = new SeatHoldService(_context, NullLogger<SeatHoldService>.Instance, new Mock<IHubContext<SeatStatusHub>>().Object);

            // First hold 2 seats
            var firstHold = await seatHoldService.HoldSeatsAsync(showtime.Id, new List<Guid> { seats[0].Id, seats[1].Id }, _buyerId);
            Assert.Equal(HoldSeatsResultStatus.Success, firstHold.Status);

            // Now try to hold 2 more seats (total would be 4 > 3)
            var secondHold = await seatHoldService.HoldSeatsAsync(showtime.Id, new List<Guid> { seats[2].Id, seats[3].Id }, _buyerId);

            // Assert
            Assert.Equal(HoldSeatsResultStatus.InvalidRequest, secondHold.Status);
            Assert.Contains("Tối đa 3 vé", secondHold.Message);
        }

        [Fact]
        public async Task HoldSeats_ReholdingSameSeats_ShouldNotDoubleCount()
        {
            // Arrange
            var (showtime, seats) = await SeedShowtimeWithSeatsAsync(maxTickets: 3, seatCount: 5);
            var seatHoldService = new SeatHoldService(_context, NullLogger<SeatHoldService>.Instance, new Mock<IHubContext<SeatStatusHub>>().Object);

            // Hold 2 seats
            var initial = await seatHoldService.HoldSeatsAsync(showtime.Id, new List<Guid> { seats[0].Id, seats[1].Id }, _buyerId);
            Assert.Equal(HoldSeatsResultStatus.Success, initial.Status);

            // Re-hold the exact same 2 seats
            var rehold = await seatHoldService.HoldSeatsAsync(showtime.Id, new List<Guid> { seats[0].Id, seats[1].Id }, _buyerId);

            // Assert
            Assert.Equal(HoldSeatsResultStatus.Success, rehold.Status);
        }

        [Fact]
        public async Task HoldSeats_AccumulatedAcrossPurchasedOrders_ShouldEnforceLimit()
        {
            // Arrange
            var (showtime, seats) = await SeedShowtimeWithSeatsAsync(maxTickets: 3, seatCount: 5);
            var seatHoldService = new SeatHoldService(_context, NullLogger<SeatHoldService>.Instance, new Mock<IHubContext<SeatStatusHub>>().Object);

            // Seed an existing paid order with 2 tickets for this buyer
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = _buyerId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                CreatedAt = DateTimeOffset.UtcNow
            };
            order.OrderItems.Add(new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, SeatId = seats[0].Id, Price = 100000 });
            order.OrderItems.Add(new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, SeatId = seats[1].Id, Price = 100000 });
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            // Buyer already has 2 purchased tickets. Trying to hold 2 more (total 4 > 3) should fail
            var failHold = await seatHoldService.HoldSeatsAsync(showtime.Id, new List<Guid> { seats[2].Id, seats[3].Id }, _buyerId);
            Assert.Equal(HoldSeatsResultStatus.InvalidRequest, failHold.Status);
            Assert.Contains("Tối đa 3 vé", failHold.Message);

            // Trying to hold 1 more (total 3 <= 3) should succeed
            var successHold = await seatHoldService.HoldSeatsAsync(showtime.Id, new List<Guid> { seats[2].Id }, _buyerId);
            Assert.Equal(HoldSeatsResultStatus.Success, successHold.Status);
        }

        [Fact]
        public async Task OrdersController_CreateOrderFromHolds_ExceedingLimit_ShouldReturnBadRequest()
        {
            // Arrange
            var (showtime, seats) = await SeedShowtimeWithSeatsAsync(maxTickets: 2, seatCount: 5);
            var ordersController = CreateOrdersController(_buyerId);

            // Seed active hold of 3 seats (manually simulated or via expired config)
            var hold1 = new SeatHolds { Id = Guid.NewGuid(), SeatId = seats[0].Id, UserId = _buyerId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
            var hold2 = new SeatHolds { Id = Guid.NewGuid(), SeatId = seats[1].Id, UserId = _buyerId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
            var hold3 = new SeatHolds { Id = Guid.NewGuid(), SeatId = seats[2].Id, UserId = _buyerId, Status = "ACTIVE", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
            _context.SeatHold.AddRange(hold1, hold2, hold3);
            await _context.SaveChangesAsync();

            // Act
            var result = await ordersController.CreateOrderFromHolds(showtime.Id) as BadRequestObjectResult;

            // Assert
            Assert.NotNull(result);
            var apiResponse = Assert.IsType<ApiResponse<object>>(result.Value);
            Assert.False(apiResponse.Success);
            Assert.Contains("Tối đa 2 vé", apiResponse.Message);
        }

        [Fact]
        public async Task PublicShowtime_ShouldReturnConfiguredMaxTicketsPerUser()
        {
            // Arrange
            var (showtime, _) = await SeedShowtimeWithSeatsAsync(maxTickets: 6, seatCount: 2);
            var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var controller = new PublicShowtimeController(_context, cache, NullLogger<PublicShowtimeController>.Instance);

            // Act: test GET /api/public/events/{eventId}/showtimes/{showtimeId}
            var result = await controller.GetPublicShowtimeDetail(showtime.EventId, showtime.Id) as OkObjectResult;

            // Assert
            Assert.NotNull(result);
            var response = Assert.IsType<ApiResponse<PublicShowtimeDto>>(result.Value);
            Assert.True(response.Success);
            Assert.Equal(6, response.Data.MaxTicketsPerUser);

            // Act: test GET /api/public/showtimes/{showtimeId}
            var resultDirect = await controller.GetPublicShowtimeDetail(null, showtime.Id) as OkObjectResult;
            Assert.NotNull(resultDirect);
            var responseDirect = Assert.IsType<ApiResponse<PublicShowtimeDto>>(resultDirect.Value);
            Assert.True(responseDirect.Success);
            Assert.Equal(6, responseDirect.Data.MaxTicketsPerUser);

            // Act: test GET /api/public/events/{eventId}
            var eventResult = await controller.GetPublicEventDetail(showtime.EventId) as OkObjectResult;
            Assert.NotNull(eventResult);
            var eventResponse = Assert.IsType<ApiResponse<PublicEventDetailDto>>(eventResult.Value);
            Assert.True(eventResponse.Success);
            Assert.Equal(6, eventResponse.Data.MaxTicketsPerUser);
            Assert.Equal(6, eventResponse.Data.Showtimes[0].MaxTicketsPerUser);
        }

        [Fact]
        public async Task GetUserActiveHolds_ShouldReturnMaxTicketsAndPurchasedCount()
        {
            // Arrange
            var (showtime, seats) = await SeedShowtimeWithSeatsAsync(maxTickets: 4, seatCount: 5);
            var seatHoldService = new SeatHoldService(_context, NullLogger<SeatHoldService>.Instance, new Mock<IHubContext<SeatStatusHub>>().Object);

            // Seed 1 paid ticket
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = _buyerId,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Paid,
                CreatedAt = DateTimeOffset.UtcNow
            };
            order.OrderItems.Add(new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, SeatId = seats[0].Id, Price = 100000 });
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            // Hold 1 seat
            await seatHoldService.HoldSeatsAsync(showtime.Id, new List<Guid> { seats[1].Id }, _buyerId);

            // Act
            var holdsResult = await seatHoldService.GetUserActiveHoldsAsync(showtime.Id, _buyerId);

            // Assert
            Assert.Equal(HoldSeatsResultStatus.Success, holdsResult.Status);
            Assert.NotNull(holdsResult.Data);
            Assert.Equal(4, holdsResult.Data.MaxTicketsPerUser);
            Assert.Equal(1, holdsResult.Data.PurchasedCount);
            Assert.Equal(1, holdsResult.Data.ActiveHoldCount);
            Assert.Equal(2, holdsResult.Data.RemainingAllowance);
        }

        private async Task<(Showtime Showtime, List<Seat> Seats)> SeedShowtimeWithSeatsAsync(int maxTickets, int seatCount)
        {
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _organizerId,
                Title = "Live Show 2026",
                Location = "National Convention Center",
                StartTime = DateTime.UtcNow.AddDays(10),
                EndTime = DateTime.UtcNow.AddDays(10).AddHours(2),
                TotalSeats = seatCount,
                MaxTicketsPerUser = maxTickets
            };
            _context.Events.Add(ev);

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = ev.StartTime,
                EndTime = ev.EndTime,
                AvailableSeats = seatCount,
                MaxTicketsPerUser = maxTickets
            };
            // Set OnSale
            showtime.ChangeStatus(ShowtimeStatus.OnSale);
            _context.Showtimes.Add(showtime);

            var category = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Name = "VIP",
                Price = 500000
            };
            _context.SeatCategories.Add(category);

            var seats = new List<Seat>();
            for (int i = 1; i <= seatCount; i++)
            {
                var seat = new Seat
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtime.Id,
                    SeatCategoryId = category.Id,
                    Row = "A",
                    SeatNumber = i,
                    Status = "AVAILABLE",
                    SeatCategory = category
                };
                seats.Add(seat);
                _context.Seats.Add(seat);
            }

            await _context.SaveChangesAsync();
            return (showtime, seats);
        }
    }
}
