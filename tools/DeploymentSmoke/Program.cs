using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// Creates and drops only its own temporary database. Requires a local PostgreSQL CREATEDB user.
var connection = Environment.GetEnvironmentVariable("DEPLOYMENT_TEST_PG")
    ?? throw new InvalidOperationException("Set DEPLOYMENT_TEST_PG to a local PostgreSQL administrative connection.");
var name = "eventpulse_smoke_" + Guid.NewGuid().ToString("N");
await using var admin = new NpgsqlConnection(connection);
await admin.OpenAsync();
await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
try
{
    var target = new NpgsqlConnectionStringBuilder(connection) { Database = name };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Bootstrap:AdminEmail"] = "smoke@example.com",
        ["Bootstrap:AdminPassword"] = Guid.NewGuid().ToString("N")
    }).Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<AppDbContext>(options => options.UseNpgsql(target.ConnectionString));
    services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
    await using var provider = services.BuildServiceProvider();
    await DatabaseInitializer.InitializeAsync(provider, configuration);
    await DatabaseInitializer.InitializeAsync(provider, configuration);
    using var scope = provider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var user = await db.Users.Include(u => u.UserRoles).SingleAsync();
    if (await db.Roles.CountAsync() != 5 || !user.IsActive || user.UserRoles.Count != 1)
        throw new InvalidOperationException("Bootstrap validation failed.");
    if ((await db.Database.GetPendingMigrationsAsync()).Any()) throw new InvalidOperationException("Pending migrations remain.");
    Console.WriteLine("PASS: empty PostgreSQL migrations, active administrator, five roles, repeatable startup.");
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
    await drop.ExecuteNonQueryAsync();
}
