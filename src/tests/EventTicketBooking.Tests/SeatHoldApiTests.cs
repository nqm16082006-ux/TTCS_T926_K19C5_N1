using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class SeatHoldApiTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Guid _userId = Guid.NewGuid();

        public SeatHoldApiTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private ShowtimeSeatsController CreateController(Guid? currentUserId = null)
        {
            var logger = NullLogger<ShowtimeSeatsController>.Instance;
            var serviceLogger = NullLogger<SeatHoldService>.Instance;

            var seatHoldService = new SeatHoldService(_context, serviceLogger, EventTicketBooking.Tests.Mocks.FakeHubContext.Create(), redis: null);
            var importService = new SeatImportService(_context);

            var controller = new ShowtimeSeatsController(importService, seatHoldService, logger);

            var claims = new List<Claim>();
            if (currentUserId.HasValue)
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, currentUserId.Value.ToString()));
            }

            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = claimsPrincipal }
            };

            return controller;
        }

        private async Task<(Guid eventId, Guid showtimeId, List<Seat> seats)> SeedShowtimeWithSeatsAsync(int seatCount = 2)
        {
            var eventId = Guid.NewGuid();
            var showtimeId = Guid.NewGuid();

            _context.Events.Add(new Event
            {
                Id = eventId,
                OwnerId = Guid.NewGuid(),
                Title = "Test Concert",
                Location = "Hanoi Opera House",
                TotalSeats = seatCount
            });

            var showtime = new Showtime
            {
                Id = showtimeId,
                EventId = eventId,
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
                AvailableSeats = seatCount
            };
            showtime.ChangeStatus(ShowtimeStatus.OnSale);
            _context.Showtimes.Add(showtime);

            var category = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtimeId,
                Name = "Standard",
                Price = 200000
            };
            _context.SeatCategories.Add(category);

            var seats = new List<Seat>();
            for (int i = 0; i < seatCount; i++)
            {
                var seat = new Seat
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtimeId,
                    SeatCategoryId = category.Id,
                    Row = "A",
                    SeatNumber = i + 1
                };
                seats.Add(seat);
            }

            _context.Seats.AddRange(seats);
            await _context.SaveChangesAsync();

            return (eventId, showtimeId, seats);
        }

        [Fact]
        public async Task Test1_HoldOneAvailableSeat_ReturnsSuccessAndExpiresAt()
        {
            var seed = await SeedShowtimeWithSeatsAsync(1);
            var controller = CreateController(_userId);

            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed.seats[0].Id }
            };

            var actionResult = await controller.HoldSeats(seed.showtimeId, request, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Single(response.Data!.SeatIds);
            Assert.Equal(seed.seats[0].Id, response.Data.SeatIds[0]);

            var expectedExpiresAt = DateTime.UtcNow.AddSeconds(600);
            Assert.True((response.Data.ExpiresAt - expectedExpiresAt).Duration() < TimeSpan.FromSeconds(5));

            var holdInDb = await _context.SeatHold.FirstOrDefaultAsync(sh => sh.SeatId == seed.seats[0].Id);
            Assert.NotNull(holdInDb);
            Assert.Equal("ACTIVE", holdInDb!.Status);
            Assert.Equal(_userId, holdInDb.UserId);
        }

        [Fact]
        public async Task Test2_HoldTwoAvailableSeats_ReturnsSuccessWithSameExpiresAt()
        {
            var seed = await SeedShowtimeWithSeatsAsync(2);
            var controller = CreateController(_userId);

            var request = new HoldSeatsRequestDto
            {
                SeatIds = seed.seats.Select(s => s.Id).ToList()
            };

            var actionResult = await controller.HoldSeats(seed.showtimeId, request, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Equal(2, response.Data!.SeatIds.Count);

            var holdsInDb = await _context.SeatHold.Where(sh => request.SeatIds.Contains(sh.SeatId)).ToListAsync();
            Assert.Equal(2, holdsInDb.Count);
            Assert.Equal(holdsInDb[0].ExpiresAt, holdsInDb[1].ExpiresAt);
        }

        [Fact]
        public async Task Test3_SeatAAvailable_SeatBAlreadyHeldByOtherUser_Returns409_And_SeatANotHeld()
        {
            var seed = await SeedShowtimeWithSeatsAsync(2);
            var otherUserId = Guid.NewGuid();

            // Pre-hold seat B by another user
            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed.seats[1].Id,
                UserId = otherUserId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });
            await _context.SaveChangesAsync();

            var controller = CreateController(_userId);

            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed.seats[0].Id, seed.seats[1].Id }
            };

            var actionResult = await controller.HoldSeats(seed.showtimeId, request, default);

            var conflictResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);

            var response = Assert.IsType<ApiResponse<object>>(conflictResult.Value);
            Assert.False(response.Success);

            // Verify Seat A was NOT held (All-Or-Nothing)
            var seatAHold = await _context.SeatHold.FirstOrDefaultAsync(sh => sh.SeatId == seed.seats[0].Id && sh.UserId == _userId);
            Assert.Null(seatAHold);
        }

        [Fact]
        public async Task Test4_AllSeatsAlreadyHeld_Returns409()
        {
            var seed = await SeedShowtimeWithSeatsAsync(1);
            var otherUserId = Guid.NewGuid();

            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed.seats[0].Id,
                UserId = otherUserId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });
            await _context.SaveChangesAsync();

            var controller = CreateController(_userId);

            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed.seats[0].Id }
            };

            var actionResult = await controller.HoldSeats(seed.showtimeId, request, default);

            var conflictResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status409Conflict, conflictResult.StatusCode);
        }

        [Fact]
        public async Task Test5_DuplicateSeatIds_Returns400()
        {
            var seed = await SeedShowtimeWithSeatsAsync(1);
            var controller = CreateController(_userId);

            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed.seats[0].Id, seed.seats[0].Id }
            };

            var actionResult = await controller.HoldSeats(seed.showtimeId, request, default);

            var badRequestResult = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);

            Assert.False(response.Success);
            Assert.Contains("trùng lặp", response.Message);
        }

        [Fact]
        public async Task Test6_SeatBelongsToDifferentShowtime_Returns400()
        {
            var seed1 = await SeedShowtimeWithSeatsAsync(1);
            var seed2 = await SeedShowtimeWithSeatsAsync(1);

            var controller = CreateController(_userId);

            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed2.seats[0].Id } // Seat from showtime 2
            };

            // Post to showtime 1
            var actionResult = await controller.HoldSeats(seed1.showtimeId, request, default);

            var badRequestResult = Assert.IsType<BadRequestObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);

            Assert.False(response.Success);
        }

        [Fact]
        public async Task Test7_UnauthenticatedUser_Returns401()
        {
            var seed = await SeedShowtimeWithSeatsAsync(1);
            var controller = CreateController(currentUserId: null); // No user authenticated

            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed.seats[0].Id }
            };

            var actionResult = await controller.HoldSeats(seed.showtimeId, request, default);

            var unauthorizedResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthorizedResult.StatusCode);
        }

        [Fact]
        public async Task Test8_ExpiredHoldInDb_AllowsNewHold()
        {
            var seed = await SeedShowtimeWithSeatsAsync(1);
            var otherUserId = Guid.NewGuid();

            // Insert expired hold (ExpiresAt in the past)
            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed.seats[0].Id,
                UserId = otherUserId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow.AddMinutes(-20),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-10) // Expired 10 mins ago
            });
            await _context.SaveChangesAsync();

            var controller = CreateController(_userId);

            var request = new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed.seats[0].Id }
            };

            var actionResult = await controller.HoldSeats(seed.showtimeId, request, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Single(response.Data!.SeatIds);
        }

        #region Task T-32 Tests

        [Fact]
        public async Task T32_Case1_UserHasOneActiveHold_ReturnsSeatAndValidExpiresAt()
        {
            var seed = await SeedShowtimeWithSeatsAsync(1);
            var controller = CreateController(_userId);

            // Giữ 1 ghế
            var holdResult = await controller.HoldSeats(seed.showtimeId, new HoldSeatsRequestDto
            {
                SeatIds = new List<Guid> { seed.seats[0].Id }
            }, default);
            Assert.IsType<OkObjectResult>(holdResult);

            // Gọi API T-32 lấy ghế đang giữ của user
            var actionResult = await controller.GetMyHeldSeats(seed.showtimeId, null, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.NotNull(response.Data);
            Assert.Equal(seed.showtimeId, response.Data!.ShowtimeId);
            Assert.Single(response.Data.SeatIds);
            Assert.Equal(seed.seats[0].Id, response.Data.SeatIds[0]);
            Assert.True(response.Data.ExpiresAt > DateTime.UtcNow);
            Assert.NotNull(response.Data.Holds);
            Assert.Single(response.Data.Holds!);
            Assert.Equal(seed.seats[0].Id, response.Data.Holds![0].SeatId);
        }

        [Fact]
        public async Task T32_Case2_UserHasMultipleActiveHolds_ReturnsAllSeats()
        {
            var seed = await SeedShowtimeWithSeatsAsync(3);
            var controller = CreateController(_userId);

            // Giữ cả 3 ghế
            var holdResult = await controller.HoldSeats(seed.showtimeId, new HoldSeatsRequestDto
            {
                SeatIds = seed.seats.Select(s => s.Id).ToList()
            }, default);
            Assert.IsType<OkObjectResult>(holdResult);

            // Gọi API T-32
            var actionResult = await controller.GetMyHeldSeats(seed.showtimeId, null, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Equal(3, response.Data!.SeatIds.Count);
            foreach (var seat in seed.seats)
            {
                Assert.Contains(seat.Id, response.Data.SeatIds);
            }
        }

        [Fact]
        public async Task T32_Case3_ExpiredHold_NotReturned()
        {
            var seed = await SeedShowtimeWithSeatsAsync(2);
            var controller = CreateController(_userId);

            // Ghế 1: Giữ chỗ đã hết hạn (ExpiresAt trong quá khứ)
            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed.seats[0].Id,
                UserId = _userId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow.AddMinutes(-20),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
            });

            // Ghế 2: Giữ chỗ còn hiệu lực (ExpiresAt tương lai)
            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed.seats[1].Id,
                UserId = _userId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });
            await _context.SaveChangesAsync();

            // Gọi API T-32
            var actionResult = await controller.GetMyHeldSeats(seed.showtimeId, null, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Single(response.Data!.SeatIds);
            Assert.Equal(seed.seats[1].Id, response.Data.SeatIds[0]); // Chỉ trả về ghế 2 còn hạn
        }

        [Fact]
        public async Task T32_Case4_HoldBelongsToOtherUser_NotReturned()
        {
            var seed = await SeedShowtimeWithSeatsAsync(2);
            var otherUserId = Guid.NewGuid();

            // User khác giữ ghế 0
            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed.seats[0].Id,
                UserId = otherUserId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });

            // Current user giữ ghế 1
            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed.seats[1].Id,
                UserId = _userId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });
            await _context.SaveChangesAsync();

            var controller = CreateController(_userId);

            // Gọi API T-32
            var actionResult = await controller.GetMyHeldSeats(seed.showtimeId, null, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Single(response.Data!.SeatIds);
            Assert.Equal(seed.seats[1].Id, response.Data.SeatIds[0]); // Chỉ trả về ghế của current user
            Assert.DoesNotContain(seed.seats[0].Id, response.Data.SeatIds);
        }

        [Fact]
        public async Task T32_Case5_HoldBelongsToDifferentShowtime_NotReturned()
        {
            var seed1 = await SeedShowtimeWithSeatsAsync(1);
            var seed2 = await SeedShowtimeWithSeatsAsync(1);

            // User giữ ghế ở cả 2 showtime
            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed1.seats[0].Id,
                UserId = _userId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });

            _context.SeatHold.Add(new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = seed2.seats[0].Id,
                UserId = _userId,
                Status = "ACTIVE",
                HeldAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            });
            await _context.SaveChangesAsync();

            var controller = CreateController(_userId);

            // Gọi API T-32 cho showtime 1
            var actionResult = await controller.GetMyHeldSeats(seed1.showtimeId, null, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.Single(response.Data!.SeatIds);
            Assert.Equal(seed1.seats[0].Id, response.Data.SeatIds[0]);
            Assert.DoesNotContain(seed2.seats[0].Id, response.Data.SeatIds);
        }

        [Fact]
        public async Task T32_Case6_UserHasNoHolds_ReturnsSuccessWithEmptyList()
        {
            var seed = await SeedShowtimeWithSeatsAsync(2);
            var controller = CreateController(_userId);

            // User không giữ bất kỳ ghế nào
            var actionResult = await controller.GetMyHeldSeats(seed.showtimeId, null, default);

            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(okResult.Value);

            Assert.True(response.Success);
            Assert.NotNull(response.Data);
            Assert.Empty(response.Data!.SeatIds);
            Assert.Empty(response.Data.Holds!);
        }

        [Fact]
        public async Task T32_Case7_UnauthenticatedUser_Returns401()
        {
            var seed = await SeedShowtimeWithSeatsAsync(1);
            var controller = CreateController(currentUserId: null); // Chưa đăng nhập

            var actionResult = await controller.GetMyHeldSeats(seed.showtimeId, null, default);

            var unauthorizedResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthorizedResult.StatusCode);

            var response = Assert.IsType<ApiResponse<object>>(unauthorizedResult.Value);
            Assert.False(response.Success);
        }

        [Fact]
        public async Task T32_NonExistentShowtime_Returns404NotFound()
        {
            var controller = CreateController(_userId);
            var nonExistentShowtimeId = Guid.NewGuid();

            var actionResult = await controller.GetMyHeldSeats(nonExistentShowtimeId, null, default);

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<object>>(notFoundResult.Value);
            Assert.False(response.Success);
        }

        #endregion
    }
}
