using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Fail startup if migrations fail instead of serving an unusable application.
        await db.Database.MigrateAsync();
        var names = new[] { "Admin", "Organizer", "Staff", "Customer", "Auditor" };
        var existing = await db.Roles.Select(r => r.Name).ToListAsync();
        db.Roles.AddRange(names.Except(existing).Select(name => new Role { Name = name }));
        await db.SaveChangesAsync();

        // Optional first administrator; never change an existing account or grant it extra roles.
        var email = configuration["Bootstrap:AdminEmail"]?.Trim().ToLowerInvariant();
        var password = configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || await db.Users.AnyAsync(u => u.Email == email)) return;
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new InvalidOperationException("Bootstrap administrator password must contain at least 12 characters.");
        var adminRole = await db.Roles.SingleAsync(r => r.Name == "Admin");
        var user = new User
        {
            Email = email, Username = email, FullName = "Administrator", IsActive = true,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password)
        };
        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id });
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }
}
