using System;
using Npgsql;
class Program {
    static void Main() {
        var connString = "Host=localhost;Port=5432;Database=event_ticket_db;Username=postgres;Password=postgres_password_123";
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        var cmds = new[] {
            "UPDATE \"Showtimes\" SET \"Status\" = 'OnSale' WHERE \"Status\" = 'on_sale';",
            "UPDATE \"Showtimes\" SET \"Status\" = 'Draft' WHERE \"Status\" = 'draft';",
            "UPDATE \"Showtimes\" SET \"Status\" = 'Closed' WHERE \"Status\" = 'closed';",
            "UPDATE \"orders\" SET \"Status\" = 'Pending' WHERE \"Status\" = 'pending';",
            "UPDATE \"orders\" SET \"Status\" = 'Completed' WHERE \"Status\" = 'completed';",
            "UPDATE \"orders\" SET \"Status\" = 'Cancelled' WHERE \"Status\" = 'cancelled';",
            "UPDATE \"orders\" SET \"Status\" = 'Expired' WHERE \"Status\" = 'expired';",
            "UPDATE \"seat_holds\" SET \"Status\" = 'ACTIVE' WHERE \"Status\" = 'active';",
            "UPDATE \"seat_holds\" SET \"Status\" = 'RELEASED' WHERE \"Status\" = 'released';",
            "UPDATE \"seat_holds\" SET \"Status\" = 'COMPLETED' WHERE \"Status\" = 'completed';"
        };
        foreach(var sql in cmds) {
            using var cmd = new NpgsqlCommand(sql, conn);
            int rows = cmd.ExecuteNonQuery();
            Console.WriteLine(sql.Substring(0, Math.Min(40, sql.Length)) + " -> " + rows + " rows updated.");
        }
    }
}
