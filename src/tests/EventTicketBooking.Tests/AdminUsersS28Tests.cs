using System.Security.Claims;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventTicketBooking.Tests;

/// <summary>
/// Acceptance tests cho Story S-28 (Quản trị tạo tài khoản nhân viên và gán vai trò).
/// </summary>
public class AdminUsersS28Tests : IDisposable
{
    private readonly AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly Argon2PasswordHasher hasher = new();

    public void Dispose() => db.Dispose();

    private async Task SeedRolesAsync(params string[] roleNames)
    {
        foreach (var name in roleNames)
        {
            db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = name, Description = name });
        }
        await db.SaveChangesAsync();
    }

    private async Task<User> AddUserAsync(string username, string email, bool active = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            PasswordHash = "hash",
            IsActive = active
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private AdminUsersController CreateController(Guid? actorId = null)
    {
        var controller = new AdminUsersController(db, hasher);

        if (actorId.HasValue)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, actorId.Value.ToString()),
                new Claim(ClaimTypes.Name, "admin")
            };
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
                }
            };
        }
        else
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        return controller;
    }

    [Fact]
    public async Task CreateUser_WithRoles_HashesPasswordAndWritesRoleAudit()
    {
        await SeedRolesAsync("Staff", "Auditor");
        var admin = await AddUserAsync("admin", "admin@test.com");
        var controller = CreateController(admin.Id);

        var plainPassword = "Secret@123";
        var result = await controller.CreateUser(new CreateAdminUserDto
        {
            Username = "ticket_checker",
            Email = "checker@test.com",
            FullName = "Nhân viên soát vé",
            Password = plainPassword,
            Roles = new List<string> { "Staff", "Auditor" }
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);

        var user = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .SingleAsync(u => u.Username == "ticket_checker");

        // AC11: password được băm bằng cơ chế hiện tại (Argon2id) và không lưu plain text
        Assert.NotEqual(plainPassword, user.PasswordHash);
        Assert.StartsWith("$argon2id$", user.PasswordHash);
        Assert.True(hasher.Verify(plainPassword, user.PasswordHash));

        // AC3: gán role khi tạo
        Assert.Equal(2, user.UserRoles.Count);
        Assert.Contains(user.UserRoles, ur => ur.Role.Name == "Staff");
        Assert.Contains(user.UserRoles, ur => ur.Role.Name == "Auditor");

        // AC6: audit log ghi lại việc gán role đầu tiên
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("ROLE_CHANGED", audit.Action);
        Assert.Equal(admin.Id, audit.ActorUserId);
        Assert.Equal(user.Id, audit.TargetUserId);
        Assert.Contains("Staff", audit.NewRoles);
        Assert.True(audit.CreatedAt <= DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_IsRejected()
    {
        await SeedRolesAsync("Staff");
        var admin = await AddUserAsync("admin", "admin@test.com");
        await AddUserAsync("existing", "dup@test.com");
        var controller = CreateController(admin.Id);

        var result = await controller.CreateUser(new CreateAdminUserDto
        {
            Username = "another",
            Email = "dup@test.com",
            Password = "Secret@123",
            Roles = new List<string> { "Staff" }
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task CreateUser_UnknownRole_IsRejected()
    {
        await SeedRolesAsync("Staff");
        var admin = await AddUserAsync("admin", "admin@test.com");
        var controller = CreateController(admin.Id);

        var result = await controller.CreateUser(new CreateAdminUserDto
        {
            Username = "ghost",
            Email = "ghost@test.com",
            Password = "Secret@123",
            Roles = new List<string> { "SuperAdmin" }
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await db.Users.FirstOrDefaultAsync(u => u.Username == "ghost"));
    }

    [Fact]
    public async Task UpdateUserRoles_ChangesRoles_AndWritesActorAudit()
    {
        await SeedRolesAsync("Customer", "Staff", "Auditor");
        var admin = await AddUserAsync("admin", "admin@test.com");
        var target = await AddUserAsync("employee", "employee@test.com");

        db.UserRoles.Add(new UserRole
        {
            UserId = target.Id,
            RoleId = (await db.Roles.SingleAsync(r => r.Name == "Customer")).Id
        });
        await db.SaveChangesAsync();

        var controller = CreateController(admin.Id);
        var result = await controller.UpdateUserRoles(target.Id, new UpdateUserRolesDto
        {
            Roles = new List<string> { "Staff", "Auditor" }
        });

        Assert.IsType<OkObjectResult>(result);

        var updatedRoles = await db.UserRoles
            .Where(ur => ur.UserId == target.Id)
            .Select(ur => ur.Role.Name)
            .ToListAsync();
        Assert.Equal(2, updatedRoles.Count);
        Assert.Contains("Staff", updatedRoles);
        Assert.Contains("Auditor", updatedRoles);
        Assert.DoesNotContain("Customer", updatedRoles);

        // AC6: audit log đủ actor, target, role cũ, role mới, action, thời điểm
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("ROLE_CHANGED", audit.Action);
        Assert.Equal(admin.Id, audit.ActorUserId);
        Assert.Equal("admin", audit.ActorUsername);
        Assert.Equal(target.Id, audit.TargetUserId);
        Assert.Equal("Customer", audit.OldRoles);
        Assert.Equal("Auditor,Staff", audit.NewRoles);
        Assert.True(audit.CreatedAt <= DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task UpdateUserRoles_WithoutAuthenticatedActor_Returns401()
    {
        await SeedRolesAsync("Customer", "Staff");
        var target = await AddUserAsync("employee", "employee@test.com");
        db.UserRoles.Add(new UserRole
        {
            UserId = target.Id,
            RoleId = (await db.Roles.SingleAsync(r => r.Name == "Customer")).Id
        });
        await db.SaveChangesAsync();

        // Không có identity đã xác thực -> không được phép ghi audit với actor giả định
        var controller = CreateController(actorId: null);
        var result = await controller.UpdateUserRoles(target.Id, new UpdateUserRolesDto
        {
            Roles = new List<string> { "Staff" }
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(db.AuditLogs);

        var unchanged = await db.UserRoles.Where(ur => ur.UserId == target.Id)
            .Select(ur => ur.Role.Name).ToListAsync();
        Assert.Contains("Customer", unchanged);
    }

    [Fact]
    public void AdminUsersEndpoints_RequireAdminRole_AttributePresent()
    {
        // AC10: API quản trị được bảo vệ bằng Backend Authorization
        var attr = typeof(AdminUsersController)
            .GetCustomAttributes(typeof(RequireRoleAttribute), true)
            .Cast<RequireRoleAttribute>()
            .Single();

        Assert.Contains("Admin", attr.Roles);
    }

    [Fact]
    public async Task CreateUser_ResponseDto_NeverExposesPasswordHash()
    {
        await SeedRolesAsync("Staff");
        var admin = await AddUserAsync("admin", "admin@test.com");
        var controller = CreateController(admin.Id);

        var result = await controller.CreateUser(new CreateAdminUserDto
        {
            Username = "no_leak",
            Email = "noleak@test.com",
            Password = "Secret@123",
            Roles = new List<string> { "Staff" }
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var serialized = System.Text.Json.JsonSerializer.Serialize(created.Value);

        Assert.DoesNotContain("Secret@123", serialized);
        Assert.DoesNotContain("passwordHash", serialized, StringComparison.OrdinalIgnoreCase);

        var dtoProperties = typeof(AdminUserResponseDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Password", dtoProperties);
        Assert.DoesNotContain("PasswordHash", dtoProperties);
    }
}
