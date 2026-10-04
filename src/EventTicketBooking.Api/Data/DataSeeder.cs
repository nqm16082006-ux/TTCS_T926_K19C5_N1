using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Data
{
    /// <summary>
    /// Class hỗ trợ khởi tạo dữ liệu ban đầu cho hệ thống (Data Seeder):
    /// - 5 Vai trò: Admin, Organizer, Staff, Customer, Auditor
    /// - 2 Tài khoản demo: Admin và Organizer (Mật khẩu được băm bằng Argon2id)
    /// </summary>
    public static class DataSeeder
    {
        public static async Task SeedAsync(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var logger = scope.ServiceProvider.GetService<ILogger<AppDbContext>>();

            try
            {
                await context.Database.MigrateAsync();

                if (context.Database.IsNpgsql())
                {
                    var conn = (Npgsql.NpgsqlConnection)context.Database.GetDbConnection();
                    if (conn.State != System.Data.ConnectionState.Open)
                        await conn.OpenAsync();
                    await conn.ReloadTypesAsync();

                    try {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            CREATE OR REPLACE FUNCTION text_to_showtime_status(text) RETURNS showtime_status AS $$
                            SELECT CASE LOWER(REPLACE($1, ' ', ''))
                                WHEN 'draft' THEN 'draft'::showtime_status
                                WHEN 'onsale' THEN 'on_sale'::showtime_status
                                WHEN 'closed' THEN 'closed'::showtime_status
                                ELSE LOWER($1)::showtime_status
                            END;
                            $$ LANGUAGE SQL IMMUTABLE;

                            CREATE OR REPLACE FUNCTION text_to_order_status(text) RETURNS order_status AS $$
                            SELECT CASE LOWER(REPLACE($1, ' ', ''))
                                WHEN 'pending' THEN 'pending'::order_status
                                WHEN 'paid' THEN 'paid'::order_status
                                WHEN 'cancelled' THEN 'cancelled'::order_status
                                WHEN 'expired' THEN 'expired'::order_status
                                WHEN 'needsattention' THEN 'needs_attention'::order_status
                                ELSE LOWER($1)::order_status
                            END;
                            $$ LANGUAGE SQL IMMUTABLE;

                            DO $$ BEGIN
                                IF NOT EXISTS (SELECT 1 FROM pg_cast WHERE castsource = 'text'::regtype AND casttarget = 'showtime_status'::regtype) THEN
                                    CREATE CAST (text AS showtime_status) WITH FUNCTION text_to_showtime_status(text) AS IMPLICIT;
                                    CREATE CAST (character varying AS showtime_status) WITH FUNCTION text_to_showtime_status(text) AS IMPLICIT;
                                END IF;
                                IF NOT EXISTS (SELECT 1 FROM pg_cast WHERE castsource = 'text'::regtype AND casttarget = 'order_status'::regtype) THEN
                                    CREATE CAST (text AS order_status) WITH FUNCTION text_to_order_status(text) AS IMPLICIT;
                                    CREATE CAST (character varying AS order_status) WITH FUNCTION text_to_order_status(text) AS IMPLICIT;
                                END IF;
                            END $$;";
                        await cmd.ExecuteNonQueryAsync();
                    } catch (Exception ex) {
                        logger?.LogError(ex, "Error creating implicit cast!");
                    }

                    var enumAlterStatements = new[]
                    {
                        "ALTER TYPE showtime_status ADD VALUE IF NOT EXISTS 'Draft';",
                        "ALTER TYPE showtime_status ADD VALUE IF NOT EXISTS 'OnSale';",
                        "ALTER TYPE showtime_status ADD VALUE IF NOT EXISTS 'Closed';",
                        "ALTER TYPE order_status ADD VALUE IF NOT EXISTS 'Pending';",
                        "ALTER TYPE order_status ADD VALUE IF NOT EXISTS 'Paid';",
                        "ALTER TYPE order_status ADD VALUE IF NOT EXISTS 'Cancelled';",
                        "ALTER TYPE order_status ADD VALUE IF NOT EXISTS 'Expired';",
                        "ALTER TYPE order_status ADD VALUE IF NOT EXISTS 'NeedsAttention';"
                    };

                    foreach (var stmt in enumAlterStatements)
                    {
                        try
                        {
                            using var alterCmd = conn.CreateCommand();
                            alterCmd.CommandText = stmt;
                            await alterCmd.ExecuteNonQueryAsync();
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Could not alter enum value: {Stmt}", stmt);
                        }
                    }
                    await conn.ReloadTypesAsync();
                }

                // 1. Kiểm tra nếu bảng Roles chưa có dữ liệu thì seed 5 roles
                if (!await context.Roles.AnyAsync())
                {
                    var roles = new List<Role>
                    {
                        new Role { Id = Guid.NewGuid(), Name = "Admin", Description = "Quản trị viên toàn hệ thống", CreatedAt = DateTime.UtcNow },
                        new Role { Id = Guid.NewGuid(), Name = "Organizer", Description = "Ban tổ chức sự kiện", CreatedAt = DateTime.UtcNow },
                        new Role { Id = Guid.NewGuid(), Name = "Staff", Description = "Nhân viên soát vé và vận hành sự kiện", CreatedAt = DateTime.UtcNow },
                        new Role { Id = Guid.NewGuid(), Name = "Customer", Description = "Khách hàng và người tham dự", CreatedAt = DateTime.UtcNow },
                        new Role { Id = Guid.NewGuid(), Name = "Auditor", Description = "Kiểm toán viên và quản lý tài chính", CreatedAt = DateTime.UtcNow }
                    };

                    await context.Roles.AddRangeAsync(roles);
                    await context.SaveChangesAsync();
                }

                // 2. Kiểm tra nếu bảng Users chưa có dữ liệu thì seed 2 tài khoản demo
                if (!await context.Users.AnyAsync())
                {
                    var adminRole = await context.Roles.FirstAsync(r => r.Name == "Admin");
                    var organizerRole = await context.Roles.FirstAsync(r => r.Name == "Organizer");

                    var adminUser = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = "admin",
                        Email = "admin@eventticket.com",
                        PasswordHash = passwordHasher.Hash("Admin@123456"),
                        FullName = "System Administrator",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    var organizerUser = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = "organizer",
                        Email = "organizer@eventticket.com",
                        PasswordHash = passwordHasher.Hash("Organizer@123456"),
                        FullName = "Event Organizer",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await context.Users.AddRangeAsync(adminUser, organizerUser);
                    await context.SaveChangesAsync();

                    // Gán quan hệ tương ứng vào bảng trung gian UserRoles
                    var userRoles = new List<UserRole>
                    {
                        new UserRole
                        {
                            UserId = adminUser.Id,
                            RoleId = adminRole.Id,
                            AssignedAt = DateTime.UtcNow
                        },
                        new UserRole
                        {
                            UserId = organizerUser.Id,
                            RoleId = organizerRole.Id,
                            AssignedAt = DateTime.UtcNow
                        }
                    };

                    await context.UserRoles.AddRangeAsync(userRoles);
                    await context.SaveChangesAsync();
                }

                // 3. Kiểm tra nếu bảng Events chưa có dữ liệu thì seed 2 sự kiện công khai mẫu đang mở bán
                if (!await context.Events.AnyAsync())
                {
                    var organizerUser = await context.Users.FirstAsync(u => u.Username == "organizer");

                    var event1 = new Event
                    {
                        Id = Guid.NewGuid(),
                        OwnerId = organizerUser.Id,
                        Title = "Live Concert Anh Trai Say Hi 2026",
                        Description = "Đêm nhạc quy tụ dàn ca sĩ hàng đầu với hệ thống âm thanh, ánh sáng chuẩn quốc tế.",
                        Location = "Sân vận động Quốc gia Mỹ Đình, Hà Nội",
                        ImageUrl = "https://images.unsplash.com/photo-1540039155733-5bb30b53aa14?w=1200&q=80",
                        StartTime = DateTime.UtcNow.AddDays(7),
                        EndTime = DateTime.UtcNow.AddDays(7).AddHours(4),
                        TotalSeats = 50,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    var showtime1 = new Showtime
                    {
                        Id = Guid.NewGuid(),
                        EventId = event1.Id,
                        StartTime = event1.StartTime,
                        EndTime = event1.EndTime,
                        AvailableSeats = 50
                    };
                    showtime1.ChangeStatus(ShowtimeStatus.OnSale);

                    var catStandard = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime1.Id, Name = "Standard / Vé Thường", Price = 300000 };
                    var catVip = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime1.Id, Name = "VIP / Vé Cao Cấp", Price = 1200000 };
                    var catVvip = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime1.Id, Name = "VVIP / Vé Đặc Biệt", Price = 2500000 };

                    showtime1.SeatCategories.Add(catStandard);
                    showtime1.SeatCategories.Add(catVip);
                    showtime1.SeatCategories.Add(catVvip);

                    event1.Showtimes.Add(showtime1);

                    var event2 = new Event
                    {
                        Id = Guid.NewGuid(),
                        OwnerId = organizerUser.Id,
                        Title = "Festival Âm Nhạc Mùa Hè 2026",
                        Description = "Lễ hội âm nhạc mùa hè cuồng nhiệt với nhiều nghệ sĩ Indie và Rock bùng nổ.",
                        Location = "Phố đi bộ Nguyễn Huệ, Quận 1, TP. Hồ Chí Minh",
                        ImageUrl = "https://images.unsplash.com/photo-1470225620780-dba8ba36b745?w=1200&q=80",
                        StartTime = DateTime.UtcNow.AddDays(14),
                        EndTime = DateTime.UtcNow.AddDays(14).AddHours(5),
                        TotalSeats = 50,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    var showtime2 = new Showtime
                    {
                        Id = Guid.NewGuid(),
                        EventId = event2.Id,
                        StartTime = event2.StartTime,
                        EndTime = event2.EndTime,
                        AvailableSeats = 50
                    };
                    showtime2.ChangeStatus(ShowtimeStatus.OnSale);

                    var catFanZone = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime2.Id, Name = "Vé Fan Zone", Price = 800000 };
                    var catPhoThong = new SeatCategory { Id = Guid.NewGuid(), ShowtimeId = showtime2.Id, Name = "Vé Phổ Thông", Price = 200000 };

                    showtime2.SeatCategories.Add(catPhoThong);
                    showtime2.SeatCategories.Add(catFanZone);

                    event2.Showtimes.Add(showtime2);

                    await context.Events.AddRangeAsync(event1, event2);
                    await context.SaveChangesAsync();

                    // Seed ghế cho showtime1 (Hàng A, B, C, D, E x 10 ghế = 50 ghế)
                    var seatsList = new List<Seat>();
                    string[] rows = new[] { "A", "B", "C", "D", "E" };
                    foreach (var r in rows)
                    {
                        for (int num = 1; num <= 10; num++)
                        {
                            var cat = (r == "A" || r == "B") ? catVvip : (r == "C" ? catVip : catStandard);
                            seatsList.Add(new Seat
                            {
                                Id = Guid.NewGuid(),
                                ShowtimeId = showtime1.Id,
                                SeatCategoryId = cat.Id,
                                Row = r,
                                SeatNumber = num
                            });
                        }
                    }

                    // Seed ghế cho showtime2
                    foreach (var r in rows)
                    {
                        for (int num = 1; num <= 10; num++)
                        {
                            var cat = (r == "A" || r == "B") ? catFanZone : catPhoThong;
                            seatsList.Add(new Seat
                            {
                                Id = Guid.NewGuid(),
                                ShowtimeId = showtime2.Id,
                                SeatCategoryId = cat.Id,
                                Row = r,
                                SeatNumber = num
                            });
                        }
                    }

                    await context.Seats.AddRangeAsync(seatsList);
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Lỗi xảy ra trong quá trình seed dữ liệu ban đầu.");
            }
        }
    }
}
