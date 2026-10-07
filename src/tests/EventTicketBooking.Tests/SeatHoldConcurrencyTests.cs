using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Kịch bản kiểm thử T-31 (S-13): Bắn 200 yêu cầu giữ ghế đồng thời vào 100 ghế.
    /// Yêu cầu AC:
    /// - Đúng 100 yêu cầu thành công (200 OK)
    /// - Đúng 100 yêu cầu bị từ chối (409 Conflict kèm danh sách ghế tranh chấp)
    /// - Chạy lặp lại 10 lần vẫn luôn ra kết quả 100 thành công.
    /// - Không có lỗi 500 nảy sinh.
    /// </summary>
    public class SeatHoldConcurrencyTests
    {
        private readonly ITestOutputHelper _output;

        public SeatHoldConcurrencyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private AppDbContext CreateSqliteDbContext(string dbPath)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            return new AppDbContext(options);
        }

        private ShowtimeSeatsController CreateController(AppDbContext context, Guid userId)
        {
            var logger = NullLogger<ShowtimeSeatsController>.Instance;
            var serviceLogger = NullLogger<SeatHoldService>.Instance;

            var seatHoldService = new SeatHoldService(context, serviceLogger, redis: null);
            var importService = new SeatImportService(context);

            var controller = new ShowtimeSeatsController(importService, seatHoldService, logger);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString())
            };

            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var claimsPrincipal = new ClaimsPrincipal(identity);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = claimsPrincipal }
            };

            return controller;
        }

        [Fact]
        public async Task Test31_Fire200ConcurrentRequestsFor100Seats_MustYieldExactly100SuccessesAnd100Conflicts_Repeated10Times()
        {
            const int seatCount = 100;
            const int totalRequests = 200;
            const int iterationCount = 10;

            for (int iteration = 1; iteration <= iterationCount; iteration++)
            {
                var dbPath = Path.Combine(Path.GetTempPath(), $"concurrency_t31_{Guid.NewGuid():N}.db");

                try
                {
                    // 1. Khởi tạo DB Schema với SQLite
                    using (var setupContext = CreateSqliteDbContext(dbPath))
                    {
                        await setupContext.Database.EnsureDeletedAsync();
                        await setupContext.Database.EnsureCreatedAsync();

                        var eventId = Guid.NewGuid();
                        var showtimeId = Guid.NewGuid();

                        var ownerUser = new User
                        {
                            Id = Guid.NewGuid(),
                            Username = $"owner_t31_{iteration}",
                            Email = $"owner_t31_{iteration}@example.com",
                            PasswordHash = "hash",
                            FullName = "Owner User",
                            IsActive = true
                        };
                        setupContext.Users.Add(ownerUser);

                        var ev = new Event
                        {
                            Id = eventId,
                            OwnerId = ownerUser.Id,
                            Title = "Concurrent Test Event",
                            Location = "Stadium",
                            TotalSeats = seatCount
                        };
                        setupContext.Events.Add(ev);

                        var showtime = new Showtime
                        {
                            Id = showtimeId,
                            EventId = eventId,
                            StartTime = DateTime.UtcNow.AddDays(1),
                            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
                            AvailableSeats = seatCount
                        };
                        showtime.ChangeStatus(ShowtimeStatus.OnSale);
                        setupContext.Showtimes.Add(showtime);

                        var category = new SeatCategory
                        {
                            Id = Guid.NewGuid(),
                            ShowtimeId = showtimeId,
                            Name = "VIP",
                            Price = 500000
                        };
                        setupContext.SeatCategories.Add(category);

                        var users = new List<User>();
                        for (int i = 0; i < totalRequests; i++)
                        {
                            users.Add(new User
                            {
                                Id = Guid.NewGuid(),
                                Username = $"user_t31_{i}",
                                Email = $"user_t31_{i}@example.com",
                                PasswordHash = "hash",
                                FullName = $"Test User {i}",
                                IsActive = true
                            });
                        }
                        setupContext.Users.AddRange(users);

                        var seats = new List<Seat>();
                        for (int i = 0; i < seatCount; i++)
                        {
                            seats.Add(new Seat
                            {
                                Id = Guid.NewGuid(),
                                ShowtimeId = showtimeId,
                                SeatCategoryId = category.Id,
                                Row = "ROW-" + (i / 10 + 1),
                                SeatNumber = (i % 10 + 1)
                            });
                        }
                        setupContext.Seats.AddRange(seats);
                        await setupContext.SaveChangesAsync();
                    }

                    // 2. Chuẩn bị 200 requests (mỗi ghế có 2 requests đồng thời tranh chấp)
                    Guid targetShowtimeId;
                    List<Guid> seatIdsList;
                    List<Guid> userIdsList;
                    using (var readContext = CreateSqliteDbContext(dbPath))
                    {
                        var st = await readContext.Showtimes.FirstAsync();
                        targetShowtimeId = st.Id;
                        seatIdsList = await readContext.Seats.Select(s => s.Id).ToListAsync();
                        userIdsList = await readContext.Users.Where(u => u.Username.StartsWith("user_t31_")).Select(u => u.Id).ToListAsync();
                    }

                    Assert.Equal(seatCount, seatIdsList.Count);
                    Assert.Equal(totalRequests, userIdsList.Count);

                    // 200 requests targeting the 100 seats (seat 0 has req 0 & req 100, etc.)
                    var requestPayloads = new List<(Guid userId, Guid seatId)>();
                    for (int i = 0; i < totalRequests; i++)
                    {
                        var seatId = seatIdsList[i % seatCount];
                        var userId = userIdsList[i];
                        requestPayloads.Add((userId, seatId));
                    }

                    var results = new ConcurrentBag<IActionResult>();

                    // 3. Thực thi 200 requests song song (Concurrency Target)
                    var tasks = requestPayloads.Select(async req =>
                    {
                        using var threadContext = CreateSqliteDbContext(dbPath);
                        var controller = CreateController(threadContext, req.userId);
                        var response = await controller.HoldSeats(targetShowtimeId, new HoldSeatsRequestDto
                        {
                            SeatIds = new List<Guid> { req.seatId }
                        }, default);

                        results.Add(response);
                    });

                    await Task.WhenAll(tasks);

                    // 4. Kiểm tra kết quả phản hồi của 200 requests
                    int successCount = 0;
                    int conflictCount = 0;
                    int error500Count = 0;

                    foreach (var res in results)
                    {
                        if (res is OkObjectResult ok)
                        {
                            var apiResp = Assert.IsType<ApiResponse<HoldSeatsResponseDto>>(ok.Value);
                            if (apiResp.Success) successCount++;
                        }
                        else if (res is ObjectResult objRes)
                        {
                            if (objRes.StatusCode == StatusCodes.Status409Conflict)
                            {
                                conflictCount++;
                            }
                            else if (objRes.StatusCode == StatusCodes.Status500InternalServerError)
                            {
                                error500Count++;
                            }
                        }
                    }

                    _output.WriteLine($"[T-31 Concurrency Iteration {iteration}/10] Total Requests: {totalRequests} | Successes (200): {successCount} | Conflicts (409): {conflictCount} | 500 Errors: {error500Count}");

                    Assert.Equal(0, error500Count);
                    Assert.Equal(seatCount, successCount);
                    Assert.Equal(totalRequests - seatCount, conflictCount);

                    // 5. Kiểm tra trực tiếp trong DB: Phải có đúng 100 bản ghi ACTIVE
                    using (var verifyContext = CreateSqliteDbContext(dbPath))
                    {
                        var activeHolds = await verifyContext.SeatHold
                            .Where(sh => sh.Status == "ACTIVE")
                            .ToListAsync();

                        Assert.Equal(seatCount, activeHolds.Count);
                        Assert.Equal(seatCount, activeHolds.Select(sh => sh.SeatId).Distinct().Count());
                    }
                }
                finally
                {
                    if (File.Exists(dbPath))
                    {
                        try { File.Delete(dbPath); } catch { }
                    }
                }
            }
        }
    }
}
