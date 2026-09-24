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
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Lỗi xảy ra trong quá trình seed dữ liệu ban đầu.");
            }
        }
    }
}
