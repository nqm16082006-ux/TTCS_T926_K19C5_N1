using System;
using Npgsql;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var connString = "Host=localhost;Port=5432;Database=event_ticket_db;Username=postgres;Password=postgres_password_123";
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT \"Id\" FROM \"Showtimes\" LIMIT 1;", conn);
        var result = await cmd.ExecuteScalarAsync();
        Console.WriteLine("SHOWTIME_ID_FOUND: " + result);
    }
}
