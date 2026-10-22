using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Services;
using Microsoft.EntityFrameworkCore;
using DotNetEnv;
using Prometheus;

// Nạp file .env ở thư mục hiện tại hoặc các thư mục cha
var envSearchDir = new DirectoryInfo(Directory.GetCurrentDirectory());
while (envSearchDir != null)
{
    var envCandidate = Path.Combine(envSearchDir.FullName, ".env");
    if (File.Exists(envCandidate))
    {
        Env.Load(envCandidate);
        break;
    }
    envSearchDir = envSearchDir.Parent;
}

// Cho phép Npgsql xử lý DateTime linh hoạt (cả Local và UTC)

var builder = WebApplication.CreateBuilder(args);

// Đảm bảo Configuration đọc các biến môi trường
builder.Configuration.AddEnvironmentVariables();
var listenPort = int.TryParse(builder.Configuration["PORT"], out var configuredPort) &&
                 configuredPort is > 0 and <= 65535
    ? configuredPort
    : 5012;
var bindHost = builder.Configuration["BIND_HOST"] ?? builder.Configuration["HOST"] ?? "0.0.0.0";
var customUrls = builder.Configuration["ASPNETCORE_URLS"];
if (string.IsNullOrWhiteSpace(customUrls))
{
    builder.WebHost.UseUrls($"http://{bindHost}:{listenPort}");
}

var publicUrl = builder.Configuration["App:PublicBaseUrl"] ?? builder.Configuration["RENDER_EXTERNAL_URL"];
if (!string.IsNullOrWhiteSpace(publicUrl))
{
    builder.Configuration["App:PublicBaseUrl"] = publicUrl.TrimEnd('/');
    builder.Configuration["Payment:PayOS:ReturnUrl"] ??= publicUrl.TrimEnd('/') + "/payment-result.html";
    builder.Configuration["Payment:PayOS:CancelUrl"] ??= publicUrl.TrimEnd('/') + "/payment-result.html";
}

// Xác định provider database cho local dev. Nếu Postgres không sẵn sàng, hãy fallback về InMemory để app vẫn chạy
var selectedDbProvider = builder.Configuration["Database:Provider"] ?? "Postgres";
string? pgConnection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
var envHost = Environment.GetEnvironmentVariable("POSTGRES_HOST");

if (string.IsNullOrWhiteSpace(pgConnection) && !string.IsNullOrWhiteSpace(envHost))
{
    string port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    string db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "event_ticket_db";
    string user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
    string pass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres_password_123";

    pgConnection = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = envHost,
        Port = int.Parse(port),
        Database = db,
        Username = user,
        Password = pass
    }.ConnectionString;
}
else if (string.IsNullOrWhiteSpace(pgConnection))
{
    pgConnection = builder.Configuration.GetConnectionString("DefaultConnection");
}
if (string.Equals(selectedDbProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("EventTicketBooking"));
}
else
{
    try
    {
        using var testConnection = new Npgsql.NpgsqlConnection(pgConnection);
        testConnection.Open();
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(pgConnection);
        dataSourceBuilder.MapEnum<EventTicketBooking.Api.Models.ShowtimeStatus>("showtime_status");
        dataSourceBuilder.MapEnum<EventTicketBooking.Api.Models.OrderStatus>("order_status");
        var dataSource = dataSourceBuilder.Build();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
    }
    catch (Exception ex)
    {
        var allowFallback = builder.Configuration.GetValue<bool>("Database:AllowInMemoryFallback");
        if (allowFallback)
        {
            Console.WriteLine($"[INFO] Không thể kết nối PostgreSQL ({ex.Message}). Fallback sang InMemoryDatabase cho môi trường local.");
            builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("EventTicketBooking"));
        }
        else
        {
            throw new InvalidOperationException($"CRITICAL DATABASE ERROR: Failed to connect to PostgreSQL ({ex.Message}). Startup aborted because in-memory fallback is disabled.", ex);
        }
    }
}

if (!builder.Environment.IsDevelopment() && System.Text.Encoding.UTF8.GetByteCount(JwtValidation.Secret(builder.Configuration)) < 32)
    throw new InvalidOperationException("Production JWT signing key must contain at least 32 bytes.");
_ = JwtValidation.Parameters(builder.Configuration);

builder.Services.AddScoped<SeatImportService>();

// Chuỗi kết nối Redis (đọc từ biến môi trường hoặc configuration)
string? redisConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Redis")
                          ?? builder.Configuration.GetConnectionString("Redis")
                          ?? "127.0.0.1:6379";

StackExchange.Redis.ConfigurationOptions redisOptions;
try
{
    redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnection);
}
catch (Exception ex)
{
    Console.WriteLine($"[FATAL] Cú pháp chuỗi kết nối Redis không hợp lệ: '{redisConnection}'. Lỗi: {ex.Message}");
    throw;
}

redisOptions.AbortOnConnectFail = false;
redisOptions.ConnectTimeout = 5000;
redisOptions.SyncTimeout = 5000;
redisOptions.ConnectRetry = 5;

// Khởi tạo ConnectionMultiplexer duy nhất với AbortOnConnectFail = false.
// StackExchange.Redis sẽ tự động reconnect trong background khi Redis online trở lại.
StackExchange.Redis.IConnectionMultiplexer redisMultiplexer = StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions);

// Đăng ký instance ConnectionMultiplexer thật làm Singleton trong DI.
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redisMultiplexer);

// Cấu hình IDistributedCache: Nếu Redis online -> Dùng Redis; Nếu Redis offline -> Dùng MemoryCache để tránh timeout 14s
if (redisMultiplexer.IsConnected)
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "EventTicket_";
    });
}
else
{
    Console.WriteLine("[INFO] Redis không khả dụng lúc khởi động. Tự động sử dụng Memory Cache cho IDistributedCache để đảm bảo tốc độ phản hồi tức thì.");
    builder.Services.AddDistributedMemoryCache();
}

// Đăng ký dịch vụ băm mật khẩu Argon2id (TTKN-20)
builder.Services.AddSingleton<EventTicketBooking.Api.Services.Interfaces.IPasswordHasher, EventTicketBooking.Api.Services.Implementations.Argon2PasswordHasher>();

// Cấu hình bảo mật xác thực (Story S-02)
builder.Services.Configure<EventTicketBooking.Api.Options.AuthSecurityOptions>(options =>
{
    builder.Configuration.GetSection(EventTicketBooking.Api.Options.AuthSecurityOptions.SectionName).Bind(options);
    if (builder.Environment.IsDevelopment())
    {
        // Trong môi trường Local Development, cho phép tự động fallback khi Redis chưa khởi động
        options.FailClosedOnRedisDown = false;
    }
});

// Đăng ký dịch vụ Token và Authentication (TTKN-25)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ITokenService, EventTicketBooking.Api.Services.Implementations.TokenService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IAuthService, EventTicketBooking.Api.Services.Implementations.AuthService>();

// Đăng ký dịch vụ giới hạn số lần gửi lại mã OTP đăng ký (tối đa 5 lần/giờ cho 1 email)
builder.Services.Configure<EventTicketBooking.Api.Options.RegistrationOtpRateLimitOptions>(
    builder.Configuration.GetSection(EventTicketBooking.Api.Options.RegistrationOtpRateLimitOptions.SectionName));
builder.Services.AddSingleton<EventTicketBooking.Api.Services.Interfaces.IRegistrationOtpRateLimiter, EventTicketBooking.Api.Services.Implementations.RegistrationOtpRateLimiter>();

// Đăng ký dịch vụ Email (T-08)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IEmailService, EventTicketBooking.Api.Services.Implementations.EmailService>();

// Đăng ký dịch vụ Giữ ghế (T-23 / S-10)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ISeatHoldService, EventTicketBooking.Api.Services.Implementations.SeatHoldService>();

// Đăng ký dịch vụ Nhật ký thao tác đơn hàng và vé (Story S-49)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IOrderAuditService, EventTicketBooking.Api.Services.Implementations.OrderAuditService>();

// Đăng ký dịch vụ Xoá và Ẩn danh dữ liệu cá nhân (Story S-53)
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IDataDeletionService, EventTicketBooking.Api.Services.Implementations.DataDeletionService>();

// Đăng ký dịch vụ Giám sát và Cảnh báo (Story S-43 No-Grafana Shared PostgreSQL)
builder.Services.Configure<EventTicketBooking.Api.Options.MonitoringOptions>(
    builder.Configuration.GetSection(EventTicketBooking.Api.Options.MonitoringOptions.SectionName));
builder.Services.AddSingleton<EventTicketBooking.Api.Monitoring.ITelemetryChannel, EventTicketBooking.Api.Monitoring.TelemetryChannel>();
builder.Services.AddSingleton<EventTicketBooking.Api.Metrics.IAppMetrics>(sp =>
{
    var channel = sp.GetRequiredService<EventTicketBooking.Api.Monitoring.ITelemetryChannel>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    var prometheus = new EventTicketBooking.Api.Metrics.PrometheusAppMetrics();
    return new EventTicketBooking.Api.Monitoring.DatabaseAppMetrics(channel, env, prometheus);
});
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IMonitoringService, EventTicketBooking.Api.Services.Implementations.MonitoringService>();
builder.Services.AddHostedService<EventTicketBooking.Api.Monitoring.TelemetryBatchWriterHostedService>();
builder.Services.AddHostedService<EventTicketBooking.Api.Monitoring.MonitoringAlertEvaluationHostedService>();
builder.Services.AddHttpClient<EventTicketBooking.Api.Services.Interfaces.IPrometheusMetricsService, EventTicketBooking.Api.Services.Implementations.PrometheusMetricsService>();

// T-44 (S-19): Đọc cấu hình cổng thanh toán từ biến môi trường hoặc appsettings
var paymentProvider = builder.Configuration["PaymentSettings:Provider"]
                     ?? builder.Configuration["PAYMENT_PROVIDER"]
                     ?? "Mock";

// Story S-45: chính sách huỷ vé & hoàn tiền (giá trị demo 24 giờ / 90% — chờ Product duyệt)
builder.Services.Configure<EventTicketBooking.Api.Options.RefundPolicyOptions>(
    builder.Configuration.GetSection(EventTicketBooking.Api.Options.RefundPolicyOptions.SectionName));

// Story S-45: cấu hình mô phỏng kết quả hoàn tiền của Mock gateway ("Succeeded" hoặc "Failed")
builder.Services.Configure<EventTicketBooking.Api.Options.MockPaymentOptions>(
    builder.Configuration.GetSection(EventTicketBooking.Api.Options.MockPaymentOptions.SectionName));

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
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IRefundService, EventTicketBooking.Api.Services.Implementations.RefundService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IReconciliationService, EventTicketBooking.Api.Services.Implementations.ReconciliationService>();

// Đăng ký cấu hình và dịch vụ ký số mã QR vé điện tử (Story S-26)
builder.Services.Configure<EventTicketBooking.Api.Options.TicketQrOptions>(builder.Configuration.GetSection(EventTicketBooking.Api.Options.TicketQrOptions.SectionName));
builder.Services.AddSingleton<EventTicketBooking.Api.Services.Interfaces.IQrSignatureService, EventTicketBooking.Api.Services.Implementations.QrSignatureService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ITicketService, EventTicketBooking.Api.Services.Implementations.TicketService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ICheckinExportService, EventTicketBooking.Api.Services.Implementations.CheckinExportService>();
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ISeatMapPdfExportService, EventTicketBooking.Api.Services.Implementations.SeatMapPdfExportService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ICheckinStatisticsService, EventTicketBooking.Api.Services.Implementations.CheckinStatisticsService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IDiscountCodeService, EventTicketBooking.Api.Services.Implementations.DiscountCodeService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.ITicketTransferService, EventTicketBooking.Api.Services.Implementations.TicketTransferService>();
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

builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<EventTicketBooking.Api.Options.TicketRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(EventTicketBooking.Api.Options.TicketRateLimitOptions.SectionName))
    .Validate(o => o.Policies.ContainsKey("seat-hold") && o.Policies.ContainsKey("order-create")
        && o.Policies.Values.All(r => r.AccountLimit > 0 && r.IpLimit > 0 && r.WindowSeconds > 0),
        "Rate-limit policies must have positive limits and window durations.")
    .Validate(o => !o.AllowInMemoryFallback || builder.Environment.IsDevelopment(),
        "In-memory rate limiting is only supported in Development.")
    .ValidateOnStart();
builder.Services.AddSingleton<ITicketRateLimiter, TicketRateLimiter>();
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor;
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// T-52 (S-23): Dịch vụ & Job nền quét huỷ đơn quá hạn và nhả ghế trong một giao dịch
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IExpiredOrderCleanupService, EventTicketBooking.Api.Services.Implementations.ExpiredOrderCleanupService>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.ExpiredOrderCleanupWorker>();

// S-27 Task 2: Background queue & worker for ticket emails
builder.Services.AddSingleton<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue, EventTicketBooking.Api.BackgroundServices.TicketEmailQueue>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.TicketEmailWorker>();

//T-27 Job nền quét và nhả ghế quá hạn, chạy lặp lại được
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.SeatHoldCleanupWorker>();

// S-40: Dịch vụ hàng đợi phòng chờ khi lượng truy cập vượt ngưỡng
builder.Services.Configure<EventTicketBooking.Api.Options.WaitingRoomOptions>(builder.Configuration.GetSection(EventTicketBooking.Api.Options.WaitingRoomOptions.SectionName));
builder.Services.AddSingleton<EventTicketBooking.Api.Services.Interfaces.IWaitingRoomService, EventTicketBooking.Api.Services.Implementations.WaitingRoomService>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.WaitingRoomCleanupWorker>();

// S-48: Dịch vụ hoàn tiền, retry tự động và thông báo cho kế toán
builder.Services.Configure<EventTicketBooking.Api.Options.RefundOptions>(builder.Configuration.GetSection(EventTicketBooking.Api.Options.RefundOptions.SectionName));
builder.Services.Configure<EventTicketBooking.Api.Options.AccountingOptions>(builder.Configuration.GetSection(EventTicketBooking.Api.Options.AccountingOptions.SectionName));
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IAccountingNotificationService, EventTicketBooking.Api.Services.Implementations.AccountingNotificationService>();
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IRefundService, EventTicketBooking.Api.Services.Implementations.RefundService>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.RefundProcessingWorker>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.ShowtimeSaleExpiryWorker>();

// Story S-61: Dịch vụ & Job nền nhắc người mua trước 24 giờ diễn qua email và cập nhật khi đổi giờ
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IShowtimeReminderService, EventTicketBooking.Api.Services.Implementations.ShowtimeReminderService>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.ShowtimeReminderWorker>();

// Story S-59: Dịch vụ & Job nền danh sách chờ khi suất diễn hết vé
builder.Services.AddScoped<EventTicketBooking.Api.Services.Interfaces.IWaitlistService, EventTicketBooking.Api.Services.Implementations.WaitlistService>();
builder.Services.AddHostedService<EventTicketBooking.Api.BackgroundServices.WaitlistNotificationWorker>();

var app = builder.Build();
app.UseForwardedHeaders();

var configuredTermsVersion = builder.Configuration["TermsPolicy:CurrentVersion"];
if (!string.IsNullOrWhiteSpace(configuredTermsVersion))
{
    EventTicketBooking.Api.Services.TermsPolicy.CurrentVersion = configuredTermsVersion;
}

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

    // S-55 & S-43: Kiểm tra cấu hình AutoSeed, AutoMigrate và SeedDemoData trước khi gọi DataSeeder
    var autoSeed = app.Configuration.GetValue<bool?>("Database:AutoSeed") ?? false;
    var autoMigrate = app.Configuration.GetValue<bool?>("Database:AutoMigrate") ?? false;
    var seedDemoData = app.Configuration.GetValue<bool>("Database:SeedDemoData");

    if (autoSeed || autoMigrate || seedDemoData)
    {
        await DataSeeder.SeedAsync(app.Services, app.Configuration);
    }
}

// Bật phục vụ static files cho wwwroot (login.html)
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            ctx.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate, max-age=0";
            ctx.Context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.Pragma] = "no-cache";
            ctx.Context.Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.Expires] = "0";
            ctx.Context.Response.Headers["Clear-Site-Data"] = "\"cache\"";
        }
    }
});
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

app.UseRouting();
app.UseMiddleware<EventTicketBooking.Api.Middlewares.RequestTelemetryMiddleware>();

// S-43: Bảo vệ endpoint /metrics bằng Bearer token trước khi MapMetrics xử lý
app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
    {
        var configuredToken = EventTicketBooking.Api.Metrics.MetricsTokenResolver.ResolveScrapeToken(app.Configuration);

        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var providedToken = authHeader[bearerPrefix.Length..].Trim();
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedToken);
        var configuredBytes = System.Text.Encoding.UTF8.GetBytes(configuredToken);

        if (providedBytes.Length != configuredBytes.Length ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    await next();
});

// S-43: Thu thập HTTP metrics
app.UseHttpMetrics();

app.UseAuthorization();
app.UseMiddleware<EventTicketBooking.Api.Middlewares.RoleAuthorizationMiddleware>();
app.UseMiddleware<EventTicketBooking.Api.Middlewares.TicketRateLimitMiddleware>();
app.MapControllers();
app.MapMetrics("/metrics");
app.MapHub<EventTicketBooking.Api.Hubs.SeatStatusHub>("/hubs/seat-status");
app.MapHub<EventTicketBooking.Api.Hubs.SystemAlertHub>("/hubs/system-alerts");
app.MapHub<EventTicketBooking.Api.Hubs.CheckinLiveHub>("/hubs/checkin-live");

app.Run();
