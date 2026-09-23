using EmailConfirmation.Data;
using EmailConfirmation.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
// Dùng InMemory để demo. Thay bằng UseSqlServer/UseNpgsql trong production.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("EmailConfirmationDb"));

// ── ASP.NET Core Identity ─────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // Bắt buộc xác nhận email trước khi đăng nhập (AC4)
    options.SignIn.RequireConfirmedEmail = true;

    // Cấu hình token xác nhận email (hợp lệ 1 ngày)
    options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;

    // Cấu hình mật khẩu (có thể chỉnh theo yêu cầu)
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();  // Bao gồm EmailConfirmationTokenProvider

// ── Dịch vụ Email Confirmation ────────────────────────────────────────────────
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IEmailConfirmationService, EmailConfirmationService>();

// ── Controllers & Swagger ────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Middleware Pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Seed dữ liệu test
    await SeedTestDataAsync(app);
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// ── Seed Test Data ────────────────────────────────────────────────────────────
static async Task SeedTestDataAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    // Tạo user test chưa xác nhận email
    if (await userManager.FindByEmailAsync("test@example.com") is null)
    {
        var user = new ApplicationUser
        {
            UserName = "test@example.com",
            Email = "test@example.com",
            FullName = "Người Dùng Test",
            EmailConfirmed = false  // AC4: Chưa xác nhận
        };
        await userManager.CreateAsync(user, "Test@123");
        Console.WriteLine($"✅ User test đã được tạo: Id={user.Id}");
        Console.WriteLine($"💡 Dùng userId={user.Id} để test endpoint /resend");
    }
}
