using System.Security.Claims;
using System.Text.Json;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EventTicketBooking.Tests;

public class TicketReadmissionS31Tests
{
    private static TicketCheckInController Controller(AppDbContext db, Guid staffId, string role = "Staff") => new(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketCheckInController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier, staffId.ToString()), new Claim(ClaimTypes.Role, role)
            }, "test"))
            }
        }
    };

    private static async Task<(OrderItem Ticket, User Staff)> Seed(AppDbContext db, bool checkedIn)
    {
        var staff = new User { Username = "staff-" + Guid.NewGuid(), Email = Guid.NewGuid() + "@test.invalid", FullName = "Nhân viên A", IsActive = true, PasswordHash = "test" };
        var ev = new Event { Owner = staff, Title = "S31 Test", Location = "Test", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2) };
        var show = new Showtime { Event = ev, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(2) };
        var category = new SeatCategory { Showtime = show, Name = "VIP", Price = 100 };
        var seat = new Seat { Showtime = show, SeatCategory = category, Row = "A", SeatNumber = 1 };
        var order = new Order { User = staff, Showtime = show, Status = OrderStatus.Paid, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var ticket = new OrderItem
        {
            Order = order,
            Seat = seat,
            Price = 100,
            IsCheckedIn = checkedIn,
            CheckInGate = checkedIn ? "A" : null,
            CheckInTime = checkedIn ? DateTimeOffset.UtcNow.AddMinutes(-5) : null
        };
        db.Add(ticket);
        await db.SaveChangesAsync();
        return (ticket, staff);
    }

    [Fact]
    public async Task Duplicate_ReturnsOriginalTimeAndGate_AndReadmissionPreservesThem()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (ticket, staff) = await Seed(db, true);
        var originalTime = ticket.CheckInTime;
        var controller = Controller(db, staff.Id);
        var request = new TicketScanRequestDto { TicketId = ticket.Id, SelectedShowtimeId = ticket.Order.ShowtimeId, GateName = "B" };
        var duplicate = Assert.IsType<BadRequestObjectResult>(await controller.ScanTicket(request));
        var json = JsonSerializer.SerializeToElement(duplicate.Value);
        Assert.Equal("ALREADY_CHECKED_IN", json.GetProperty("Reason").GetString());
        Assert.Equal("A", json.GetProperty("PreviousGate").GetString());
        Assert.Equal(originalTime, json.GetProperty("PreviousCheckInTime").GetDateTimeOffset());
        request.AllowReadmission = true;
        request.RequestId = Guid.NewGuid();
        Assert.IsType<BadRequestObjectResult>(await controller.ScanTicket(request));
        request.ReadmissionReason = "Đã xác nhận chủ vé quay lại";
        Assert.IsType<OkObjectResult>(await controller.ScanTicket(request));
        var admission = Assert.Single(await db.TicketReadmissions.ToListAsync());
        Assert.Equal(staff.Id, admission.StaffUserId);
        Assert.Equal(staff.FullName, admission.StaffName);
        Assert.Equal("B", admission.Gate);
        Assert.Equal(request.ReadmissionReason, admission.Reason);
        Assert.Equal(originalTime, ticket.CheckInTime);
        Assert.Equal("A", ticket.CheckInGate);
        Assert.IsType<ConflictObjectResult>(await controller.ScanTicket(request));
        Assert.Single(await db.TicketReadmissions.ToListAsync());
    }

    [Theory]
    [InlineData("Customer", true, OrderStatus.Paid)]
    [InlineData("Staff", false, OrderStatus.Paid)]
    [InlineData("Staff", true, OrderStatus.Cancelled)]
    public async Task Readmission_RejectsUnauthorizedUnscannedOrCancelled(string role, bool checkedIn, OrderStatus status)
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (ticket, staff) = await Seed(db, checkedIn);
        ticket.Order.Status = status;
        await db.SaveChangesAsync();
        var result = await Controller(db, staff.Id, role).ScanTicket(new TicketScanRequestDto
        {
            TicketId = ticket.Id,
            SelectedShowtimeId = ticket.Order.ShowtimeId,
            GateName = "B",
            AllowReadmission = true,
            ReadmissionReason = "Confirmed",
            RequestId = Guid.NewGuid()
        });
        Assert.False(result is OkObjectResult);
        Assert.Empty(await db.TicketReadmissions.ToListAsync());
    }

    [PostgresS31Fact]
    public async Task PostgreSql_ConcurrentScans_AllowExactlyOne_AndKeepOriginalGate()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("S31_POSTGRES_CONNECTION"))
        {
            Database = "s31_test_" + Guid.NewGuid().ToString("N")
        };
        var barrier = new SimultaneousScanInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).AddInterceptors(barrier).Options;
        await using var db = new AppDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var (ticket, staff) = await Seed(db, false);
            async Task<IActionResult> Scan(string gate)
            {
                await using var separate = new AppDbContext(options);
                return await Controller(separate, staff.Id).ScanTicket(new TicketScanRequestDto
                {
                    TicketId = ticket.Id,
                    SelectedShowtimeId = ticket.Order.ShowtimeId,
                    GateName = gate
                });
            }
            var results = await Task.WhenAll(Scan("A"), Scan("B"));
            Assert.Single(results.OfType<OkObjectResult>());
            var rejection = Assert.Single(results.OfType<BadRequestObjectResult>());
            var rejectedJson = JsonSerializer.SerializeToElement(rejection.Value);
            Assert.Equal("ALREADY_CHECKED_IN", rejectedJson.GetProperty("Reason").GetString());
            await db.Entry(ticket).ReloadAsync();
            Assert.True(ticket.IsCheckedIn);
            Assert.Equal(ticket.CheckInGate, rejectedJson.GetProperty("PreviousGate").GetString());
            Assert.Equal(ticket.CheckInTime, rejectedJson.GetProperty("PreviousCheckInTime").GetDateTimeOffset());
            var payload = new TicketScanRequestDto
            {
                TicketId = ticket.Id,
                SelectedShowtimeId = ticket.Order.ShowtimeId,
                GateName = "C",
                AllowReadmission = true,
                RequestId = Guid.NewGuid(),
                ReadmissionReason = "Confirmed owner"
            };
            async Task<IActionResult> Readmit()
            {
                await using var separate = new AppDbContext(options);
                return await Controller(separate, staff.Id).ScanTicket(payload);
            }
            var readmissions = await Task.WhenAll(Readmit(), Readmit());
            Assert.Single(readmissions.OfType<OkObjectResult>());
            Assert.Single(readmissions.OfType<ConflictObjectResult>());
            Assert.Single(await db.TicketReadmissions.ToListAsync());
        }
        finally { /* Keep the isolated test database for inspection; never delete a database here. */ }
    }
}

public sealed class PostgresS31FactAttribute : FactAttribute
{
    public PostgresS31FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("S31_POSTGRES_CONNECTION")))
            Skip = "Set S31_POSTGRES_CONNECTION to run against an isolated PostgreSQL test database.";
    }
}


// Force both scanners past the initial read before either UPDATE reaches PostgreSQL.
public sealed class SimultaneousScanInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    private int _arrived;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith("UPDATE order_items", StringComparison.OrdinalIgnoreCase))
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }
}
