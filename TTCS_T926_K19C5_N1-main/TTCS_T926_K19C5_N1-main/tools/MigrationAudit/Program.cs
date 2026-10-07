using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

// Standalone inspection: does not execute the API entry point, seeder or migrations.
if (!File.Exists(".env"))
    throw new InvalidOperationException("Run from the repository root with its local .env file.");
DotNetEnv.Env.Load(".env");
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_HOST")))
{
    connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = Environment.GetEnvironmentVariable("POSTGRES_HOST")!,
        Port = int.Parse(Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432"),
        Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "event_ticket_db",
        Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres",
        Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres_password_123"
    }.ConnectionString;
}
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("No PostgreSQL connection configured. No changes performed.");

var builder = new NpgsqlDataSourceBuilder(connectionString);
builder.MapEnum<ShowtimeStatus>("showtime_status");
builder.MapEnum<OrderStatus>("order_status");
await using var source = builder.Build();
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(source).Options);
await db.Database.OpenConnectionAsync();
await using var transaction = await db.Database.BeginTransactionAsync();
await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

var known = db.Database.GetMigrations().ToHashSet();
var applied = (await db.Database.GetAppliedMigrationsAsync()).ToHashSet();
var pending = known.Except(applied).Order().ToArray();
var unknown = applied.Except(known).Order().ToArray();
Console.WriteLine($"Source migrations: {known.Count}; applied: {applied.Count}; pending: {pending.Length}");
foreach (var id in pending) Console.WriteLine($"PENDING: {id}");
foreach (var id in unknown) Console.WriteLine($"HISTORY_ONLY: {id} (investigate; do not delete history)");
Console.WriteLine($"Model differs from migration snapshot: {db.Database.HasPendingModelChanges()}");

var columns = new Dictionary<(string Schema, string Table, string Column), (string Type, bool Nullable)>();
await using (var command = source.CreateCommand("""
    SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull
    FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p')
    AND n.nspname NOT IN ('pg_catalog', 'information_schema')
    """))
{
    // Separate connection executes only this literal SELECT.
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        columns[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = (reader.GetString(3), reader.GetBoolean(4));
}
var differences = new List<string>();
var checkedColumns = new HashSet<(string, string, string)>();
foreach (var entity in db.Model.GetEntityTypes())
{
    var table = entity.GetTableName();
    if (table is null) continue;
    var schema = entity.GetSchema() ?? "public";
    var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
    foreach (var property in entity.GetProperties())
    {
        var column = property.GetColumnName(store);
        if (column is null || !checkedColumns.Add((schema, table, column))) continue;
        var label = $"{schema}.{table}.{column}";
        if (!columns.TryGetValue((schema, table, column), out var actual))
        {
            differences.Add($"MISSING: {label}");
            continue;
        }
        var expected = property.GetColumnType() ?? property.GetRelationalTypeMapping().StoreType;
        if (expected != actual.Type)
            differences.Add($"TYPE: {label}; model={expected}; database={actual.Type}");
        if (property.IsColumnNullable(store) != actual.Nullable)
            differences.Add($"NULLABILITY: {label}; model={property.IsColumnNullable(store)}; database={actual.Nullable}");
    }
}
Console.WriteLine($"Checked mapped columns: {checkedColumns.Count}; differences: {differences.Count}");
foreach (var difference in differences) Console.WriteLine(difference);
foreach (var column in columns.Where(c => c.Key.Table == "seat_holds" && c.Value.Type.StartsWith("timestamp")))
    Console.WriteLine($"HOLD_TIME: {column.Key.Column} = {column.Value.Type}");
Console.WriteLine("Scope: column existence/type/nullability and migration IDs. Does not prove index, constraint, data timezone or fresh/upgrade migration execution equivalence.");
if (args.Contains("--script"))
{
    var path = Path.GetFullPath("migration-review.sql");
    var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    await File.WriteAllTextAsync(path, sql);
    Console.WriteLine($"Generated review-only SQL: {path}; NOT executed.");
}
await transaction.RollbackAsync();
Environment.ExitCode = pending.Length > 0 || unknown.Length > 0 || differences.Count > 0 || db.Database.HasPendingModelChanges() ? 1 : 0;
