using System;
using System.Threading.Tasks;
using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Data
{
    public static class DbSeeder
    {
        public static async Task SeedAsync(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetService<ILogger<AppDbContext>>();

            try
            {
                // 1. Kiểm tra và khởi tạo vai trò (Roles) mẫu nếu chưa có
                var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "Admin");
                if (adminRole == null)
                {
                    adminRole = new Role
                    {
                        Id = Guid.NewGuid(),
                        Name = "Admin",
                        Description = "Quản trị viên hệ thống",
                        CreatedAt = DateTime.UtcNow
                    };
                    await context.Roles.AddAsync(adminRole);
                }

                var userRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "User");
                if (userRole == null)
                {
                    userRole = new Role
                    {
                        Id = Guid.NewGuid(),
                        Name = "User",
                        Description = "Người dùng thông thường",
                        CreatedAt = DateTime.UtcNow
                    };
                    await context.Roles.AddAsync(userRole);
                }

                await context.SaveChangesAsync();

                // 2. Kiểm tra và khởi tạo tài khoản Admin mẫu
                var adminUser = await context.Users.FirstOrDefaultAsync(u => u.Email == "admin@eventticket.com");
                if (adminUser == null)
                {
                    adminUser = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = "admin",
                        Email = "admin@eventticket.com",
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin@123456"),
                        FullName = "System Administrator",
                        RoleId = adminRole.Id,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await context.Users.AddAsync(adminUser);
                }

                // 3. Kiểm tra và khởi tạo tài khoản User mẫu
                var normalUser = await context.Users.FirstOrDefaultAsync(u => u.Email == "user@eventticket.com");
                if (normalUser == null)
                {
                    normalUser = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = "user",
                        Email = "user@eventticket.com",
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword("User@123456"),
                        FullName = "Regular User",
                        RoleId = userRole.Id,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await context.Users.AddAsync(normalUser);
                }

                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Lỗi xảy ra trong quá trình seed dữ liệu mẫu.");
            }
        }
    }
}
