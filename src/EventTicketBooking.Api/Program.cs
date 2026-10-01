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

// Kiểm tra khả năng kết nối PostgreSQL
bool isPgAvailable = false;
try
{
    using var testConn = new Npgsql.NpgsqlConnection(pgConnection);
    testConn.Open();
    isPgAvailable = true;
}
catch
{
    isPgAvailable = false;
}

if (isPgAvailable)
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(pgConnection));
}
else
{
    Console.WriteLine("[INFO] PostgreSQL không kết nối được. Tự động khởi chạy với SQLite cục bộ.");
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite("Data Source=eventticket_dev.db"));
}

builder.Services.AddScoped<SeatImportService>();

// Chuỗi kết nối Redis (đọc từ biến môi trường hoặc configuration)
string? redisConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Redis")
                          ?? builder.Configuration.GetConnectionString("Redis")
                          ?? "localhost:6379";

bool isRedisAvailable = false;
StackExchange.Redis.IConnectionMultiplexer? redisMultiplexer = null;
try
{
    var redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnection);
    redisOptions.ConnectTimeout = 800;
    redisOptions.SyncTimeout = 800;
    redisOptions.AbortOnConnectFail = false;
    redisMultiplexer = StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions);
    isRedisAvailable = redisMultiplexer.IsConnected;
}
catch
{
    isRedisAvailable = false;
}

if (isRedisAvailable && redisMultiplexer != null)
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redisMultiplexer);
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "EventTicket_";
    });
}
else
{
    Console.WriteLine("[INFO] Redis không kết nối được. Tự động sử dụng Memory Cache.");
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp => null!);
    builder.Services.AddDistributedMemoryCache();
}

// Đăng ký dịch vụ băm mật khẩu Argon2id (TTKN-20)
builder.Services.AddSingleton<EventTicketBooking.Api.Services.Interfaces.IPasswordHasher, EventTicketBooking.Api.Services.Implementations.Argon2PasswordHasher>();

// Đăng ký dịch vụ Token và Authentication (TTKN-25)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ITokenService, EventTicketBooking.Api.Services.Implementations.TokenService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IAuthService, EventTicketBooking.Api.Services.Implementations.AuthService>();

// Đăng ký dịch vụ Email (T-08)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IEmailService, EventTicketBooking.Api.Services.Implementations.EmailService>();

// Đăng ký dịch vụ Giữ ghế (T-23 / S-10)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ISeatHoldService, EventTicketBooking.Api.Services.Implementations.SeatHoldService>();

// T-44 (S-19): Đọc cấu hình cổng thanh toán từ biến môi trường hoặc appsettings
var paymentProvider = builder.Configuration["PaymentSettings:Provider"]
                     ?? builder.Configuration["PAYMENT_PROVIDER"]
                     ?? "Mock";

// Nếu môi trường là Production mà cấu hình dùng cổng giả lập (Mock) -> Từ chối khởi động
if (builder.Environment.IsProduction() && paymentProvider.Equals("Mock", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: Mock Payment Gateway ('Mock') is strictly prohibited in Production environment!");
}

// Đăng ký Cổng thanh toán và Dịch vụ Thanh toán (Task T-40)
builder.Services.Configure<EventTicketBooking.Api.Options.PayOSOptions>(builder.Configuration.GetSection(EventTicketBooking.Api.Options.PayOSOptions.SectionName));
builder.Services.AddHttpClient<EventTicketBooking.Api.Services.Interfaces.IPaymentGateway, EventTicketBooking.Api.Services.Implementations.Payment.PayOSPaymentGateway>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IPaymentService, EventTicketBooking.Api.Services.Implementations.PaymentService>();


// Add Controllers, CORS & Swagger
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// T-52 (S-23): Dịch vụ & Job nền quét huỷ đơn quá hạn và nhả ghế trong một giao dịch
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IExpiredOrderCleanupService, EventTicketBooking.Api.Services.Implementations.ExpiredOrderCleanupService>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.ExpiredOrderCleanupWorker>();

//T-27 Job nền quét và nhả ghế quá hạn, chạy lặp lại được
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.SeatHoldCleanupWorker>();

var app = builder.Build();

app.UseCors("AllowAll");

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
