using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests;

/// <summary>
/// Automated tests for S-28 Follow-up:
/// Bổ sung AC: Nhân viên soát vé đăng nhập chỉ thấy màn hình quét vé.
/// </summary>
public class StaffScannerS28Tests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _configuration;

    public StaffScannerS28Tests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _passwordHasher = new Argon2PasswordHasher();

        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "Super_Secret_Jwt_Signing_Key_For_Staff_Scanner_Tests_2026!",
            ["Jwt:Issuer"] = "EventTicketBooking.Api",
            ["Jwt:Audience"] = "EventTicketBooking.Client"
        };
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        _tokenService = new TokenService(_configuration);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    /// <summary>
    /// Hàm mô phỏng chính xác logic frontend redirectByRole trong login.html
    /// </summary>
    public static string DetermineLandingRoute(IEnumerable<string>? roles)
    {
        var roleList = (roles ?? Enumerable.Empty<string>())
            .Select(r => (r ?? string.Empty).ToLowerInvariant())
            .ToList();

        if (roleList.Contains("admin"))
        {
            return "./admin-users.html";
        }
        else if (roleList.Contains("staff"))
        {
            return "./staff-scanner.html";
        }
        else if (roleList.Contains("organizer"))
        {
            return "./events.html";
        }
        else
        {
            return "./public-events.html";
        }
    }

    private async Task<User> CreateTestUserAsync(string username, string email, string password, string roleName, bool isActive = true)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            role = new Role
            {
                Id = Guid.NewGuid(),
                Name = roleName,
                Description = $"{roleName} role",
                CreatedAt = DateTime.UtcNow
            };
            _db.Roles.Add(role);
            await _db.SaveChangesAsync();
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            PasswordHash = _passwordHasher.Hash(password),
            IsActive = isActive,
            FullName = $"Test {roleName}",
            CreatedAt = DateTime.UtcNow
        };
        _db.Users.Add(user);
        _db.UserRoles.Add(new UserRole
        {
            UserId = user.Id,
            RoleId = role.Id,
            Role = role
        });
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Test1_StaffLoginSuccessfully_LandingRouteIsStaffScanner()
    {
        // 1. Arrange: Tạo tài khoản nhân viên vai trò Staff
        const string password = "StaffPassword@123";
        var staff = await CreateTestUserAsync("staff_checker", "staff@eventticket.com", password, "Staff", isActive: true);

        var authService = new AuthService(
            _db,
            _passwordHasher,
            _tokenService,
            NullLogger<AuthService>.Instance);

        // 2. Act: Staff đăng nhập
        var result = await authService.LoginAsync(new LoginRequestDto
        {
            Email = staff.Email,
            Password = password
        });

        // 3. Assert: Đăng nhập thành công, có vai trò Staff và điều hướng tới staff-scanner.html
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("Staff", result.Data.User.Roles);

        var landingRoute = DetermineLandingRoute(result.Data.User.Roles);
        Assert.Equal("./staff-scanner.html", landingRoute);
    }

    [Fact]
    public void Test2_StaffUI_ContainsScannerPlaceholder_AndNoAdminNavigation()
    {
        // Kiểm tra file staff-scanner.html có tồn tại và thỏa mãn AC
        var projectDir = AppContext.BaseDirectory;
        // Đi lên tìm thư mục wwwroot
        var dirInfo = new DirectoryInfo(projectDir);
        while (dirInfo != null && !Directory.Exists(Path.Combine(dirInfo.FullName, "src", "EventTicketBooking.Api", "wwwroot")))
        {
            dirInfo = dirInfo.Parent;
        }

        Assert.NotNull(dirInfo);
        var wwwrootDir = Path.Combine(dirInfo.FullName, "src", "EventTicketBooking.Api", "wwwroot");
        var scannerFilePath = Path.Combine(wwwrootDir, "staff-scanner.html");

        Assert.True(File.Exists(scannerFilePath), "staff-scanner.html must exist in wwwroot");

        var htmlContent = File.ReadAllText(scannerFilePath);

        // Chứa tiêu đề và placeholder quét vé
        Assert.Contains("Soát vé", htmlContent);
        Assert.Contains("Khu vực quét vé", htmlContent);
        Assert.Contains("Chức năng quét vé sẽ được triển khai trong S-29", htmlContent);

        // Không chứa link admin
        Assert.DoesNotContain("admin-users.html", htmlContent);
        Assert.DoesNotContain("Quản trị người dùng", htmlContent);
    }

    [Fact]
    public async Task Test3_StaffDirectAccessAdminApi_DeniedWith403Forbidden()
    {
        // Tạo sqlite in-memory db với pipeline đầy đủ
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "Super_Secret_Jwt_Signing_Key_For_Staff_Scanner_Tests_2026!",
            ["Jwt:Issuer"] = "EventTicketBooking.Api",
            ["Jwt:Audience"] = "EventTicketBooking.Client"
        });

        builder.Services.AddControllers().AddApplicationPart(typeof(AdminUsersController).Assembly);
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();

        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<RoleAuthorizationMiddleware>();
        app.MapControllers();

        var staffUser = new User
        {
            Username = "staff_direct",
            Email = "staff_direct@test.com",
            PasswordHash = "hash",
            IsActive = true
        };
        var staffRole = new Role { Name = "Staff" };
        staffUser.UserRoles.Add(new UserRole { UserId = staffUser.Id, RoleId = staffRole.Id, Role = staffRole });

        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
            dbContext.AddRange(staffUser, staffRole);
            await dbContext.SaveChangesAsync();
        }

        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var tokenService = new TokenService(builder.Configuration);
        var token = tokenService.GenerateAccessToken(staffUser, new[] { "Staff" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Thử truy cập trực tiếp API Admin: GET /api/admin/users
        var response = await client.GetAsync("/api/admin/users");

        // Kỳ vọng: 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task Test4_AdminLogin_DoesNotRedirectToStaffScanner()
    {
        const string password = "AdminPassword@123";
        var admin = await CreateTestUserAsync("admin_master", "admin@eventticket.com", password, "Admin", isActive: true);

        var authService = new AuthService(
            _db,
            _passwordHasher,
            _tokenService,
            NullLogger<AuthService>.Instance);

        var result = await authService.LoginAsync(new LoginRequestDto
        {
            Email = admin.Email,
            Password = password
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var landingRoute = DetermineLandingRoute(result.Data.User.Roles);
        Assert.Equal("./admin-users.html", landingRoute);
        Assert.NotEqual("./staff-scanner.html", landingRoute);
    }

    [Fact]
    public async Task Test5_CustomerLogin_DoesNotRedirectToStaffScanner()
    {
        const string password = "CustomerPassword@123";
        var customer = await CreateTestUserAsync("customer_buyer", "customer@eventticket.com", password, "Customer", isActive: true);

        var authService = new AuthService(
            _db,
            _passwordHasher,
            _tokenService,
            NullLogger<AuthService>.Instance);

        var result = await authService.LoginAsync(new LoginRequestDto
        {
            Email = customer.Email,
            Password = password
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var landingRoute = DetermineLandingRoute(result.Data.User.Roles);
        Assert.Equal("./public-events.html", landingRoute);
        Assert.NotEqual("./staff-scanner.html", landingRoute);
    }

    [Fact]
    public async Task Test6_InactiveStaff_LoginIsBlocked()
    {
        const string password = "StaffLockedPass@123";
        var lockedStaff = await CreateTestUserAsync("staff_locked", "staff_locked@eventticket.com", password, "Staff", isActive: false);

        var authService = new AuthService(
            _db,
            _passwordHasher,
            _tokenService,
            NullLogger<AuthService>.Instance);

        var result = await authService.LoginAsync(new LoginRequestDto
        {
            Email = lockedStaff.Email,
            Password = password
        });

        // Tài khoản Inactive bị chặn
        Assert.False(result.Success);
        Assert.True(result.IsLocked);
        Assert.Contains("khóa", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
