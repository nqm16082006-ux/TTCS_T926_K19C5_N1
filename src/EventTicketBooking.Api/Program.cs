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

// Cho phép Npgsql xử lý DateTime linh hoạt (cả Local và UTC)

var builder = WebApplication.CreateBuilder(args);

// Đảm bảo Configuration đọc các biến môi trường
builder.Configuration.AddEnvironmentVariables();
if (int.TryParse(builder.Configuration["PORT"], out var listenPort) && listenPort is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{listenPort}");

var publicUrl = builder.Configuration["App:PublicBaseUrl"] ?? builder.Configuration["RENDER_EXTERNAL_URL"];
if (!string.IsNullOrWhiteSpace(publicUrl))
{
    builder.Configuration["App:PublicBaseUrl"] = publicUrl.TrimEnd('/');
    builder.Configuration["Payment:PayOS:ReturnUrl"] ??= publicUrl.TrimEnd('/') + "/payment-result.html";
    builder.Configuration["Payment:PayOS:CancelUrl"] ??= publicUrl.TrimEnd('/') + "/payment-result.html";
}

// Chuỗi kết nối PostgreSQL (đọc từ biến môi trường hoặc configuration)
string? pgConnection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                       ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_HOST")) || string.IsNullOrEmpty(pgConnection))
{
    string host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
    string port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    string db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "event_ticket_db";
    string user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
    string pass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres_password_123";

    pgConnection = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = int.Parse(port),
        Database = db,
        Username = user,
        Password = pass
    }.ConnectionString;
}

// Keep PostgreSQL authoritative; an outage must not silently create another database.
var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(pgConnection);
dataSourceBuilder.MapEnum<EventTicketBooking.Api.Models.ShowtimeStatus>("showtime_status");
dataSourceBuilder.MapEnum<EventTicketBooking.Api.Models.OrderStatus>("order_status");
var dataSource = dataSourceBuilder.Build();
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
if (!builder.Environment.IsDevelopment() && System.Text.Encoding.UTF8.GetByteCount(JwtValidation.Secret(builder.Configuration)) < 32)
    throw new InvalidOperationException("Production JWT signing key must contain at least 32 bytes.");
_ = JwtValidation.Parameters(builder.Configuration);

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
    redisMultiplexer?.Dispose();
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
if (paymentProvider.Equals("Mock", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IPaymentGateway, EventTicketBooking.Api.Services.Implementations.Payment.DevelopmentMockPaymentGateway>();
else if (paymentProvider.Equals("PayOS", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<EventTicketBooking.Api.Services.Interfaces.IPaymentGateway, EventTicketBooking.Api.Services.Implementations.Payment.PayOSPaymentGateway>();
else
    throw new InvalidOperationException("Unsupported payment provider.");
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IPaymentService, EventTicketBooking.Api.Services.Implementations.PaymentService>();
builder.Services.AddHttpClient();


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

builder.Services.AddSignalR();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// T-52 (S-23): Dịch vụ & Job nền quét huỷ đơn quá hạn và nhả ghế trong một giao dịch
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IExpiredOrderCleanupService, EventTicketBooking.Api.Services.Implementations.ExpiredOrderCleanupService>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.ExpiredOrderCleanupWorker>();

//T-27 Job nền quét và nhả ghế quá hạn, chạy lặp lại được
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.SeatHoldCleanupWorker>();

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:AutoMigrate"))
    await DatabaseInitializer.InitializeAsync(app.Services, builder.Configuration);

// Render supplies the external HTTPS origin; do not derive email links from an arbitrary Host header.
if (Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicOrigin))
{
    app.Use(async (context, next) =>
    {
        context.Request.Scheme = publicOrigin.Scheme;
        context.Request.Host = new HostString(publicOrigin.Authority);
        await next();
    });
}
if (!builder.Environment.IsDevelopment() && paymentProvider.Equals("PayOS", StringComparison.OrdinalIgnoreCase))
{
    foreach (var key in new[] { "ClientId", "ApiKey", "ChecksumKey" })
        if (string.IsNullOrWhiteSpace(builder.Configuration[$"Payment:PayOS:{key}"]))
            throw new InvalidOperationException($"Production PayOS {key} must be configured.");
}

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
app.UseDefaultFiles();
app.UseStaticFiles();
var uploadsPath = builder.Configuration["Uploads:Path"];
if (!string.IsNullOrWhiteSpace(uploadsPath))
{
    Directory.CreateDirectory(Path.GetFullPath(uploadsPath));
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(uploadsPath)),
        RequestPath = "/uploads"
    });
}

app.UseAuthorization();
app.UseMiddleware<EventTicketBooking.Api.Middlewares.RoleAuthorizationMiddleware>();
app.MapControllers();
app.MapHub<EventTicketBooking.Api.Hubs.SeatStatusHub>("/hubs/seat-status");

app.Run();
