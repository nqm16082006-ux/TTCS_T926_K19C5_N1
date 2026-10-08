using System.Security.Cryptography;
using System.Text;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

DotNetEnv.Env.Load(".env");
var target = Environment.GetEnvironmentVariable("MIGRATION_COPY_DATABASE") ?? throw new Exception("Missing test database name");
if (!target.StartsWith("eventpulse_copytest_") || target.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
    throw new Exception("Refusing a non-test database name");
var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection"));
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_HOST")))
{
    settings.Host = Environment.GetEnvironmentVariable("POSTGRES_HOST")!;
    settings.Port = int.Parse(Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432");
    settings.Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "event_ticket_db";
    settings.Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
    settings.Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres_password_123";
}
if (settings.Database == target) throw new Exception("Refusing source database");
settings.Database = target;
var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
builder.MapEnum<ShowtimeStatus>("showtime_status"); builder.MapEnum<OrderStatus>("order_status");
await using var source = builder.Build();
var before = await Fingerprints(source);
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(source).Options);
var historyBefore = (await db.Database.GetAppliedMigrationsAsync()).ToHashSet();
var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
Console.WriteLine("COPY pending: " + string.Join(", ", pending));
if (pending.Length != 1 || pending[0] != "20261006120000_AlignLegacyOrderStatusAndImageUrl")
    throw new Exception("Unexpected migration state; stopping before apply");
await db.Database.MigrateAsync();
await db.Database.MigrateAsync();
var after = await Fingerprints(source);
if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
    throw new Exception("Data fingerprints differ after normalizing status representation");
foreach (var table in before.Keys) Console.WriteLine("PASS data fingerprint: " + table);
var historyAfter = (await db.Database.GetAppliedMigrationsAsync()).ToHashSet();
if (!historyBefore.IsSubsetOf(historyAfter) || historyAfter.Count != historyBefore.Count + 1)
    throw new Exception("Unexpected history change");
await using (var command = source.CreateCommand("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND data_type='text' AND ((table_name='Events' AND column_name='ImageUrl') OR (table_name='orders' AND column_name='Status'))"))
    if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 2) throw new Exception("Schema types not normalized");
await using (var command = source.CreateCommand("SELECT indexdef FROM pg_indexes WHERE schemaname='public' AND indexname='IX_orders_UserId_ShowtimeId_Pending'"))
{
    var index = (string?)await command.ExecuteScalarAsync();
    if (index is null || !index.Contains("UNIQUE") || !index.Contains("'Pending'::text")) throw new Exception("Pending unique index invalid");
}
// Query through EF to verify canonical statuses can be read after upgrade.
var orders = await db.Orders.AsNoTracking().ToListAsync();
Console.WriteLine($"PASS: copied database upgrade; {orders.Count} orders readable; all table data preserved (status casing normalized); old history preserved; unique Pending index; repeat execution");
Console.WriteLine("No writes to original database. Copy retained: " + target);

static async Task<Dictionary<string, string>> Fingerprints(NpgsqlDataSource source)
{
    var tables = new List<string>();
    await using (var command = source.CreateCommand("SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename"))
    {
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
    }
    var result = new Dictionary<string, string>();
    foreach (var table in tables)
    {
        var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
        var expression = table == "orders"
            ? "(to_jsonb(t) - 'Status') || jsonb_build_object('Status', lower(replace(t.\"Status\"::text, '_', '')))"
            : "to_jsonb(t)";
        await using var command = source.CreateCommand($"SELECT ({expression})::text FROM public.{quoted} t ORDER BY 1");
        await using var reader = await command.ExecuteReaderAsync();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (await reader.ReadAsync()) hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + "\n"));
        result[table] = Convert.ToHexString(hash.GetHashAndReset());
    }
    return result;
}
