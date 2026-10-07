using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

DotNetEnv.Env.Load(".env");
var original = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? throw new InvalidOperationException("Missing connection string");
var settings = new NpgsqlConnectionStringBuilder(original);
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_HOST")))
{
    settings.Host = Environment.GetEnvironmentVariable("POSTGRES_HOST")!;
    settings.Port = int.Parse(Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432");
    settings.Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "event_ticket_db";
    settings.Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
    settings.Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres_password_123";
}
// The existing database connection is used only to create uniquely named test databases.
// No migrations, data writes, connection termination or drops target it.
await using var admin = new NpgsqlConnection(settings.ConnectionString);
await admin.OpenAsync();
var suffix = Guid.NewGuid().ToString("N");
foreach (var scenario in new[] { "fresh", "legacy" })
{
    var name = $"eventpulse_validation_{scenario}_{suffix}";
    Console.WriteLine($"TEST DATABASE (retained): {name}");
    await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
        await create.ExecuteNonQueryAsync();
    var target = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = name };
    var sourceBuilder = new NpgsqlDataSourceBuilder(target.ConnectionString);
    sourceBuilder.MapEnum<ShowtimeStatus>("showtime_status");
    sourceBuilder.MapEnum<OrderStatus>("order_status");
    await using var source = sourceBuilder.Build();
    await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(source).Options);
    var migrator = db.GetService<IMigrator>();
    if (scenario == "fresh")
    {
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        await CheckSchema(source);
        if ((await db.Database.GetPendingMigrationsAsync()).Any()) throw new Exception("Pending migrations remain");
        Console.WriteLine("PASS: full migration chain on empty PostgreSQL; repeat execution; target column types");
        continue;
    }
    await migrator.MigrateAsync("20261004200000_NormalizeShowtimeStatusStorage");
    var userId = Guid.NewGuid(); var eventId = Guid.NewGuid(); var showtimeId = Guid.NewGuid();
    db.Users.Add(new User { Id = userId, Username = "migration-test", Email = "migration-test@example.invalid", PasswordHash = "test-only" });
    var ev = new Event { Id = eventId, OwnerId = userId, Title = "Migration test", Location = "Test", TotalSeats = 10, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddDays(1), ImageUrl = "https://example.invalid/unchanged.png" };
    db.Events.Add(ev);
    db.Showtimes.Add(new Showtime { Id = showtimeId, EventId = eventId, AvailableSeats = 10, StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddDays(1) });
    foreach (var status in new[] { OrderStatus.Paid, OrderStatus.Expired, OrderStatus.Cancelled, OrderStatus.NeedsAttention, OrderStatus.Pending })
        db.Orders.Add(new Order { Id = Guid.NewGuid(), UserId = userId, ShowtimeId = showtimeId, Status = status, TotalAmount = 123456, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    await db.Database.ExecuteSqlRawAsync("""
        DROP INDEX "IX_orders_UserId_ShowtimeId_Pending";
        ALTER TABLE orders ALTER COLUMN "Status" DROP DEFAULT;
        CREATE TYPE order_status AS ENUM ('pending','paid','cancelled','expired','needs_attention','Paid');
        """);
    await db.Database.ExecuteSqlRawAsync("""
        ALTER TABLE orders ALTER COLUMN "Status" TYPE order_status USING
            (CASE "Status" WHEN 'Paid' THEN 'Paid' WHEN 'NeedsAttention' THEN 'needs_attention'
            ELSE lower("Status") END)::order_status;
        CREATE UNIQUE INDEX "IX_orders_UserId_ShowtimeId_Pending" ON orders ("UserId","ShowtimeId") WHERE "Status"='pending'::order_status;
        ALTER TABLE "Events" ALTER COLUMN "ImageUrl" TYPE varchar(1000);
        INSERT INTO "__EFMigrationsHistory" VALUES ('20261001060648_AddEventImageUrl','9.0.2');
        """);
    await migrator.MigrateAsync();
    await CheckSchema(source);
    var orders = await db.Orders.AsNoTracking().ToListAsync();
    if (orders.Count != 5 || orders.Any(o => o.TotalAmount != 123456) || orders.Select(o => o.Status).Distinct().Count() != 5)
        throw new Exception("Order values not preserved");
    if (await db.Events.Where(e => e.Id == eventId).Select(e => e.ImageUrl).SingleAsync() != ev.ImageUrl)
        throw new Exception("Image URL changed");
    await using (var command = source.CreateCommand("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261001060648_AddEventImageUrl'"))
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1) throw new Exception("Legacy history changed");
    try
    {
        await using var duplicate = source.CreateCommand("INSERT INTO orders SELECT gen_random_uuid(), \"UserId\", \"ShowtimeId\", \"Status\", \"TotalAmount\", \"ExpiresAt\", \"CreatedAt\", \"UpdatedAt\" FROM orders WHERE \"Status\"='Pending'");
        await duplicate.ExecuteNonQueryAsync();
        throw new Exception("Duplicate pending order was accepted");
    }
    catch (PostgresException ex) when (ex.SqlState == "23505") { }
    await migrator.MigrateAsync();
    Console.WriteLine("PASS: simulated legacy enum/varchar upgrade; order values, URL, history preserved; unique Pending index enforced; repeat execution");
}
Console.WriteLine("Existing working database was not migrated or modified. Test databases retained; no database dropped.");

static async Task CheckSchema(NpgsqlDataSource source)
{
    await using var cmd = source.CreateCommand("SELECT table_name,column_name,data_type FROM information_schema.columns WHERE (table_name='orders' AND column_name='Status') OR (table_name='Events' AND column_name='ImageUrl')");
    await using var reader = await cmd.ExecuteReaderAsync();
    var count = 0;
    while (await reader.ReadAsync()) { count++; if (reader.GetString(2) != "text") throw new Exception("Unexpected target type"); }
    if (count != 2) throw new Exception("Target columns missing");
}
