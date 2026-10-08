using System;
using Npgsql;
class Program {
    static void Main() {
        var connString = "Host=localhost;Port=5432;Database=event_ticket_db;Username=postgres;Password=postgres_password_123";
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        var sql = "SELECT \"Id\", \"StartTime\", \"Status\" FROM \"Showtimes\" LIMIT 5;";
        using var cmd = new NpgsqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while(reader.Read()) {
            Console.WriteLine(reader[0] + " - " + reader[1] + " - " + reader[2]);
        }
    }
}
