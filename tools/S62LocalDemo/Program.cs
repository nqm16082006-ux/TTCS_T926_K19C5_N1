using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

var envFile = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp/s62-local/.env");
var values = File.ReadLines(envFile).Where(l => !l.StartsWith('#') && l.Contains('='))
    .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
var conn = new NpgsqlConnectionStringBuilder(values["ConnectionStrings__DefaultConnection"]);
if (conn.Host != "127.0.0.1" || conn.Port != 55432 || conn.Database != "s62_local")
    throw new InvalidOperationException("This tool only accepts the isolated s62_local database on 127.0.0.1:55432.");
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn.ConnectionString).Options);
if ((await db.Database.GetPendingMigrationsAsync()).Any()) throw new Exception("Start the local app and finish migrations first.");
var owner = await db.Users.SingleAsync(u => u.Email == "organizer@eventticket.com");
var admin = await db.Users.SingleAsync(u => u.Email == "admin@eventticket.com");
async Task<User> LocalUser(string email, string roleName)
{
    var existing = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (existing != null) return existing;
    var role = await db.Roles.SingleAsync(r => r.Name == roleName);
    var user = new User { Email = email, Username = email, FullName = "S62 local " + roleName,
        IsActive = true, PasswordHash = new Argon2PasswordHasher().Hash("S62Local@123456") };
    user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
    db.Users.Add(user); await db.SaveChangesAsync(); return user;
}
var stranger = await LocalUser("s62-other@example.invalid", "Organizer");
var staff = await LocalUser("s62-staff@example.invalid", "Staff");
const string title = "[LOCAL S62] Đối soát vào cửa";
var show = await db.Showtimes.Include(s => s.Event).SingleOrDefaultAsync(s => s.Event.Title == title);
if (show == null)
{
    var ev = new Event { Title = title, OwnerId = owner.Id, Location = "Dữ liệu mô phỏng trên máy local",
        StartTime = DateTime.UtcNow.AddDays(2), EndTime = DateTime.UtcNow.AddDays(2).AddHours(3), TotalSeats = 4 };
    show = new Showtime { Event = ev, Name = "Suất kiểm thử S62", StartTime = ev.StartTime, EndTime = ev.EndTime, AvailableSeats = 4 };
    var category = new SeatCategory { Showtime = show, Name = "Tiêu chuẩn", Price = 100000 };
    db.Showtimes.Add(show); db.SeatCategories.Add(category);
    var now = DateTimeOffset.UtcNow.AddMinutes(-40);
    for (var n = 1; n <= 4; n++)
    {
        var seat = new Seat { ShowtimeId = show.Id, SeatCategoryId = category.Id, Row = "A", SeatNumber = n };
        var order = new Order { ShowtimeId = show.Id, UserId = admin.Id, Status = OrderStatus.Paid, TotalAmount = 100000 };
        var item = new OrderItem { Order = order, Seat = seat, Price = 100000, IsCheckedIn = n <= 3,
            CheckInTime = n <= 3 ? now.AddMinutes(n) : null, CheckInGate = n <= 3 ? "Cửa A" : null };
        var ticket = new Ticket { OrderItem = item, TicketCode = $"S62-LOCAL-{n:000}" };
        db.Tickets.Add(ticket);
        if (n == 2) db.TicketReadmissions.Add(new TicketReadmission { OrderItemId = item.Id,
            StaffUserId = staff.Id, StaffName = staff.FullName!, Gate = "Cửa B", AdmittedAt = now.AddMinutes(20), Reason = "Ra ngoài\nvà vào lại, đã duyệt" });
        if (n == 3) db.OfflineCheckInRecords.AddRange(
            new OfflineCheckInRecord { OrderItemId = item.Id, ShowtimeId = show.Id, TicketCode = ticket.TicketCode,
                OfflineScanId = "s62-local-synced", GateName = "Cửa A", ScannedAtDevice = item.CheckInTime!.Value,
                SyncedByUserId = staff.Id, Status = "SYNCED" },
            new OfflineCheckInRecord { OrderItemId = item.Id, ShowtimeId = show.Id, TicketCode = ticket.TicketCode,
                OfflineScanId = "s62-local-conflict", GateName = "Cửa C", ScannedAtDevice = now.AddMinutes(25),
                SyncedByUserId = staff.Id, Status = "CONFLICT", IsConflict = true, ConflictReason = "DIFFERENT_GATE_CONFLICT" });
    }
    await db.SaveChangesAsync();
}
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JWT_KEY"] = values["JWT_KEY"] }).Build();
using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5013") };
string path = $"/api/shows/{show.Id}/checkins/";
void Auth(User user, string role) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new TokenService(config).GenerateAccessToken(user, new[] { role }));
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
Require((await client.GetAsync(path + "export-csv")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous must be denied");
Auth(stranger, "Organizer");
Require((await client.GetAsync(path + "export-csv")).StatusCode == HttpStatusCode.Forbidden, "Other owner must be denied");
Auth(staff, "Staff");
Require((await client.GetAsync(path + "export-csv")).StatusCode == HttpStatusCode.Forbidden, "Unassigned staff must be denied");
Auth(owner, "Organizer");
var preview = await client.GetAsync(path + "export-preview"); preview.EnsureSuccessStatusCode();
using var json = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
Require(json.RootElement.GetProperty("totalRows").GetInt32() == 4, "Expected 4 admitted visits");
Require(json.RootElement.GetProperty("uniqueTickets").GetInt32() == 3, "Expected 3 admitted tickets");
var audit = await client.GetAsync(path + "export-preview?includeScanLog=true&gateName=" + Uri.EscapeDataString("Cửa C"));
audit.EnsureSuccessStatusCode();
using var auditJson = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
Require(auditJson.RootElement.GetProperty("totalRows").GetInt32() == 1, "Conflict must be exported at Cửa C");
var csv = await client.GetAsync(path + "export-csv?includeScanLog=true"); csv.EnsureSuccessStatusCode();
var bytes = await csv.Content.ReadAsByteArrayAsync();
Require(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "Expected UTF8 BOM");
Require(csv.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Expected downloadable file");
await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(envFile)!, "s62-audit.csv"), bytes);
Require((await client.GetAsync($"/api/v1/checkin/showtimes/{show.Id}/export-csv")).IsSuccessStatusCode, "Legacy export route must work");
Auth(admin, "Admin");
Require((await client.GetAsync(path + "export-csv")).IsSuccessStatusCode, "Admin must export");
Console.WriteLine("PASS: real local PostgreSQL + HTTP authorization, owner/admin export, staff/stranger denial, admission counts, offline gate filter, UTF8 CSV and legacy route.");
Console.WriteLine($"Open http://localhost:5013/checkin-export.html?showId={show.Id}");
