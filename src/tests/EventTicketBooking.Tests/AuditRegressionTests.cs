using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests;

public class AuditRegressionTests : IDisposable
{
    private readonly AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Key"] = "Audit_Test_Key_With_At_Least_32_Bytes_123456789",
        ["Jwt:Issuer"] = "EventTicketBooking.Api",
        ["Jwt:Audience"] = "EventTicketBooking.Client"
    }).Build();

    public void Dispose() => db.Dispose();

    private async Task<User> AddUser(bool active = true)
    {
        var user = new User { Email = "audit@example.com", Username = "audit", PasswordHash = "hash", IsActive = active };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private string Token(User user, string mode)
    {
        var signingKey = mode == "forged" ? "Attacker_Key_With_At_Least_32_Bytes_987654321" : JwtValidation.Secret(configuration);
        var jwt = new JwtSecurityToken(
            issuer: "EventTicketBooking.Api", audience: mode == "audience" ? "attacker" : "EventTicketBooking.Client",
            claims: new[] { new Claim("sub", user.Id.ToString()), new Claim("role", "Admin") },
            expires: mode == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddHours(1),
            signingCredentials: mode == "unsigned" ? null : new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    [InlineData("audience")]
    [InlineData("malformed")]
    public async Task ProtectedEndpoint_RejectsInvalidToken(string mode)
    {
        var user = await AddUser();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers.Authorization = "Bearer " + (mode == "malformed" ? "invalid" : Token(user, mode));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequireRoleAttribute()), "protected"));
        var reached = false;
        await new RoleAuthorizationMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context, db, configuration);
        Assert.False(reached);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedEndpoint_RejectsInactiveOrDeletedUser(bool deleted)
    {
        var user = await AddUser(false);
        var token = Token(user, "valid");
        if (deleted) { db.Users.Remove(user); await db.SaveChangesAsync(); }
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers.Authorization = "Bearer " + token;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequireRoleAttribute()), "protected"));
        await new RoleAuthorizationMiddleware(_ => throw new Exception("Authorization bypass")).InvokeAsync(context, db, configuration);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_RejectsRevokedRoleInValidToken()
    {
        var user = await AddUser();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers.Authorization = "Bearer " + Token(user, "valid");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequireRoleAttribute("Admin")), "admin"));
        await new RoleAuthorizationMiddleware(_ => throw new Exception("Revoked role accepted")).InvokeAsync(context, db, configuration);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task EmailConfirmedEndpoint_AllowsActiveUserWithoutIsActiveClaim()
    {
        var user = await AddUser();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers.Authorization = "Bearer " + Token(user, "valid");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequireEmailConfirmedAttribute()), "confirmed"));
        var reached = false;
        await new RoleAuthorizationMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context, db, configuration);
        Assert.True(reached);
    }

    private AuthController AuthController() => new(Mock.Of<IAuthService>(), db, Mock.Of<IPasswordHasher>(),
        Mock.Of<IEmailService>(), new TokenService(configuration), configuration, Mock.Of<IHttpClientFactory>(), NullLogger<AuthController>.Instance);

    [Fact]
    public async Task VerifyEmail_ActiveAccountWithArbitraryCode_NeverIssuesAccessToken()
    {
        var user = await AddUser();
        var response = Assert.IsType<OkObjectResult>(await AuthController().VerifyEmail(user.Email, "arbitrary"));
        Assert.Null(response.Value!.GetType().GetProperty("accessToken"));
    }

    [Fact]
    public async Task Register_LockedAccount_DoesNotReplacePasswordOrVerificationCode()
    {
        var user = await AddUser(false);
        var response = Assert.IsType<ObjectResult>(await AuthController().Register(new RegisterRequestDto
        { Email = user.Email, Password = "newpassword", FullName = "attacker" }));
        Assert.Equal(423, response.StatusCode);
        Assert.Equal("hash", user.PasswordHash);
        Assert.Null(user.VerificationCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResendVerification_LockedAccount_CannotReactivate(bool otp)
    {
        var user = await AddUser(false);
        var controller = AuthController();
        var result = otp ? await controller.ResendOtp(new ResendOtpDto { Email = user.Email })
            : await controller.ResendConfirmationLink(new ResendOtpDto { Email = user.Email });
        Assert.Equal(423, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(user.VerificationCode);
    }

    [Fact]
    public void SeatImport_NullEntry_IsValidationError()
    {
        Assert.Single(SeatMapValidator.Validate(new List<SeatImportItemDto> { null! }));
    }

    private async Task<(Showtime Show, Seat Seat, User User)> SeatGraph(bool onSale = true)
    {
        var user = await AddUser();
        var ev = new Event { OwnerId = user.Id, Title = "Audit", Location = "Venue", TotalSeats = 1 };
        var show = new Showtime { EventId = ev.Id, AvailableSeats = 1, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1) };
        var category = new SeatCategory { ShowtimeId = show.Id, Name = "Standard", Price = 100000 };
        show.SeatCategories.Add(category);
        if (onSale) show.ChangeStatus(ShowtimeStatus.OnSale);
        var seat = new Seat { ShowtimeId = show.Id, SeatCategoryId = category.Id, Row = "A", SeatNumber = 1 };
        db.AddRange(ev, show, seat);
        await db.SaveChangesAsync();
        return (show, seat, user);
    }

    [Fact]
    public async Task Hold_SoldSeat_IsConflictWithoutCreatingHold()
    {
        var (show, seat, user) = await SeatGraph();
        seat.Status = "SOLD";
        await db.SaveChangesAsync();
        var result = await new SeatHoldService(db, NullLogger<SeatHoldService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create()).HoldSeatsAsync(show.Id, new() { seat.Id }, user.Id);
        Assert.Equal(HoldSeatsResultStatus.Conflict, result.Status);
        Assert.Empty(db.SeatHold);
    }

    [Fact]
    public async Task Hold_DraftShowtime_IsRejected()
    {
        var (show, seat, user) = await SeatGraph(false);
        var result = await new SeatHoldService(db, NullLogger<SeatHoldService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create()).HoldSeatsAsync(show.Id, new() { seat.Id }, user.Id);
        Assert.Equal(HoldSeatsResultStatus.InvalidRequest, result.Status);
        Assert.Empty(db.SeatHold);
    }

    [Fact]
    public async Task Hold_ReplayedRequest_ReturnsExistingHoldWithoutExtension()
    {
        var (show, seat, user) = await SeatGraph();
        var service = new SeatHoldService(db, NullLogger<SeatHoldService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create());
        var first = await service.HoldSeatsAsync(show.Id, new() { seat.Id }, user.Id);
        var second = await service.HoldSeatsAsync(show.Id, new() { seat.Id }, user.Id);
        Assert.Equal(HoldSeatsResultStatus.Success, second.Status);
        Assert.Single(db.SeatHold);
        Assert.Equal(first.Data!.ExpiresAt, second.Data!.ExpiresAt);
    }

    [Fact]
    public async Task CancelHold_SeatInDifferentShowtime_DoesNotRelease()
    {
        var (show, seat, user) = await SeatGraph();
        var other = new Showtime { EventId = show.EventId };
        db.Showtimes.Add(other);
        await db.SaveChangesAsync();
        var service = new SeatHoldService(db, NullLogger<SeatHoldService>.Instance, EventTicketBooking.Tests.Mocks.FakeHubContext.Create());
        await service.HoldSeatsAsync(show.Id, new() { seat.Id }, user.Id);
        var result = await service.CancelSeatHoldAsync(other.Id, seat.Id, user.Id);
        Assert.Equal(HoldSeatsResultStatus.NotFound, result.Status);
        Assert.Single(db.SeatHold);
    }
}
