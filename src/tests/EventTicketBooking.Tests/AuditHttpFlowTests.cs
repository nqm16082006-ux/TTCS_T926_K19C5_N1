using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventTicketBooking.Tests;

public class AuditHttpFlowTests
{
    [Fact]
    public async Task HttpFlow_ImportPriceSaleHoldOrderPaymentSold_EnforcesPermissionsAndDatabaseState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "Audit_Http_Test_Secret_With_At_Least_32_Bytes_12345",
            ["PaymentSettings:Provider"] = "Mock"
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(EventController).Assembly);
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddScoped<SeatImportService>();
        builder.Services.AddScoped<ISeatHoldService, SeatHoldService>();
        builder.Services.AddScoped<IPaymentService, PaymentService>();
        builder.Services.AddScoped<IPaymentGateway, DevelopmentMockPaymentGateway>();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>(new Moq.Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>().Object);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<RoleAuthorizationMiddleware>();
        app.MapControllers();
        var owner = new User { Username = "owner", Email = "owner@audit.test", PasswordHash = "hash", IsActive = true };
        var customer = new User { Username = "customer", Email = "customer@audit.test", PasswordHash = "hash", IsActive = true };
        var stranger = new User { Username = "stranger", Email = "stranger@audit.test", PasswordHash = "hash", IsActive = true };
        var organizerRole = new Role { Name = "Organizer" };
        owner.UserRoles.Add(new UserRole { UserId = owner.Id, RoleId = organizerRole.Id, Role = organizerRole });
        stranger.UserRoles.Add(new UserRole { UserId = stranger.Id, RoleId = organizerRole.Id, Role = organizerRole });
        var ev = new Event { OwnerId = owner.Id, Title = "Audit concert", Location = "Venue", TotalSeats = 1 };
        var show = new Showtime { EventId = ev.Id, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1), AvailableSeats = 1 };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.AddRange(owner, customer, stranger, ev, show);
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var tokenService = new TokenService(builder.Configuration);
        void Authenticate(User user) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user, Array.Empty<string>()));
        async Task<HttpResponseMessage> Import(string content, string action = "import")
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", "seats.json");
            return await client.PostAsync($"/api/showtimes/{show.Id}/seats/{action}", form);
        }
        var json = "[{\"row\":\"A\",\"seatNumber\":1,\"category\":\"Standard\"}]";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Import(json)).StatusCode);
        Authenticate(customer);
        Assert.Equal(HttpStatusCode.Forbidden, (await Import(json)).StatusCode);
        Authenticate(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await Import(json)).StatusCode);
        Authenticate(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import("[null]", "preview")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import("{")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Import(json)).StatusCode);
        Guid categoryId;
        Guid seatId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var category = await db.SeatCategories.SingleAsync();
            Assert.Null(category.Price);
            categoryId = category.Id;
            seatId = (await db.Seats.SingleAsync()).Id;
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Import(json)).StatusCode);
        var saleUrl = $"/api/events/{ev.Id}/showtimes/{show.Id}/open-sale";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(saleUrl, null)).StatusCode);
        var priceUrl = $"/api/events/{ev.Id}/showtimes/{show.Id}/seat-categories/prices";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(priceUrl, new { categories = new[] { new { seatCategoryId = categoryId, price = 100000 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(saleUrl, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Import("[{\"row\":\"B\",\"seatNumber\":1,\"category\":\"Other\"}]")).StatusCode);
        Authenticate(customer);
        var holdUrl = $"/api/showtimes/{show.Id}/seats/hold";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(holdUrl, new { seatIds = new[] { seatId } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(holdUrl, new { seatIds = new[] { seatId } })).StatusCode);
        var available = await client.GetFromJsonAsync<JsonElement>($"/api/showtimes/{show.Id}/seats/available");
        Assert.Equal(0, available.GetProperty("data").GetArrayLength());
        var created = await client.PostAsync($"/api/v1/orders/showtimes/{show.Id}", null);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var orderId = order.GetProperty("id").GetGuid();
        Assert.Equal(100000, order.GetProperty("totalAmount").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/showtimes/{show.Id}/seats/{seatId}/hold")).StatusCode);
        var payment = await client.PostAsync($"/api/v1/payments/orders/{orderId}", null);
        Assert.Equal(HttpStatusCode.OK, payment.StatusCode);
        var paymentJson = await payment.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("/mock-payment.html?orderId=", paymentJson.GetProperty("data").GetProperty("paymentUrl").GetString());
        Authenticate(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/orders/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/payments/orders/{orderId}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/mock-payment/callback", new { orderId, status = "SUCCESS" })).StatusCode);
        Authenticate(customer);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/mock-payment/callback", new { orderId, status = "SUCCESS" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(holdUrl, new { seatIds = new[] { seatId } })).StatusCode);
        Authenticate(owner);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/events/{ev.Id}")).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(OrderStatus.Paid, (await db.Orders.SingleAsync()).Status);
            Assert.Equal("SOLD", (await db.Seats.SingleAsync()).Status);
            Assert.Equal("CONVERTED", (await db.SeatHold.SingleAsync()).Status);
            Assert.Equal("PAID", (await db.PaymentTransactions.SingleAsync()).Status);
        }
        Authenticate(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/events/{ev.Id}/duplicate", null)).StatusCode);
        Authenticate(owner);
        var duplicated = await client.PostAsync($"/api/events/{ev.Id}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, duplicated.StatusCode);
        var copyId = (await duplicated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();
        Assert.NotEqual(ev.Id, copyId);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var copy = await db.Events.Include(e => e.Showtimes).ThenInclude(s => s.SeatCategories).SingleAsync(e => e.Id == copyId);
            Assert.EndsWith(" (Bản sao)", copy.Title);
            Assert.Equal(owner.Id, copy.OwnerId);
            var copiedShow = Assert.Single(copy.Showtimes);
            Assert.Equal(ShowtimeStatus.Draft, copiedShow.Status);
            Assert.NotEqual(show.Id, copiedShow.Id);
            var copiedCategory = Assert.Single(copiedShow.SeatCategories);
            Assert.NotEqual(categoryId, copiedCategory.Id);
            Assert.Equal(100000, copiedCategory.Price);
            var copiedSeat = await db.Seats.SingleAsync(s => s.ShowtimeId == copiedShow.Id);
            Assert.NotEqual(seatId, copiedSeat.Id);
            Assert.Equal(copiedCategory.Id, copiedSeat.SeatCategoryId);
            Assert.Equal("AVAILABLE", copiedSeat.Status);
            Assert.False(await db.Orders.AnyAsync(o => o.ShowtimeId == copiedShow.Id));
            Assert.False(await db.SeatHold.AnyAsync(h => h.SeatId == copiedSeat.Id));
            Assert.Equal("SOLD", (await db.Seats.SingleAsync(s => s.Id == seatId)).Status);
        }
        await app.StopAsync();
    }
}
