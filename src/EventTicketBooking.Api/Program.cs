using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Services;
using Microsoft.EntityFrameworkCore;
using DotNetEnv;

// Nạp file .env ở thư mục gốc hoặc thư mục hiện tại nếu có
if (File.Exists(".env"))
{
    Env.Load(".env");
}
else if (File.Exists("../../.env"))
{
    Env.Load("../../.env");
}

var builder = WebApplication.CreateBuilder(args);

// Đảm bảo Configuration đọc các biến môi trường
builder.Configuration.AddEnvironmentVariables();

// Chuỗi kết nối PostgreSQL (đọc từ biến môi trường hoặc configuration)
string? pgConnection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                       ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrEmpty(pgConnection))
{
    string host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
    string port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    string db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "event_ticket_db";
    string user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
    string pass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres_password_123";

    pgConnection = $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
}

// Chuỗi kết nối Redis (đọc từ biến môi trường hoặc configuration)
string? redisConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Redis")
                          ?? builder.Configuration.GetConnectionString("Redis")
                          ?? "localhost:6379";

// Register EF Core DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(pgConnection));

builder.Services.AddScoped<SeatImportService>();

// Register Redis Distributed Cache
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = "EventTicket_";
});

// Đăng ký kết nối Redis Multiplexer cho StackExchange.Redis (TTKN-25)
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection));

// Đăng ký dịch vụ băm mật khẩu Argon2id (TTKN-20)
builder.Services.AddSingleton<EventTicketBooking.Api.Services.Interfaces.IPasswordHasher, EventTicketBooking.Api.Services.Implementations.Argon2PasswordHasher>();

// Đăng ký dịch vụ Token và Authentication (TTKN-25)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ITokenService, EventTicketBooking.Api.Services.Implementations.TokenService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IAuthService, EventTicketBooking.Api.Services.Implementations.AuthService>();

// Đăng ký dịch vụ Email (T-08)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IEmailService, EventTicketBooking.Api.Services.Implementations.EmailService>();

// Add Controllers & Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//T-27 Job nền quét và nhả ghế quá hạn, chạy lặp lại được
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.SeatHoldCleanupWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Tự động seed 5 roles và 2 tài khoản demo khi khởi động ở môi trường Dev (TTKN-20)
    await DataSeeder.SeedAsync(app.Services);
}

// Bật phục vụ static files cho wwwroot (login.html)
app.UseStaticFiles();

app.UseAuthorization();
app.UseMiddleware<EventTicketBooking.Api.Middlewares.RoleAuthorizationMiddleware>();
app.MapControllers();

app.Run();
