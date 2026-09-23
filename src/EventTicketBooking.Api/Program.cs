using System.Text;
using DotNetEnv;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

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

// Register Redis Distributed Cache
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = "EventTicket_";
});

// Đăng ký các dịch vụ xác thực & Token (Task TTKN-25)
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();

// Cấu hình JWT Bearer Authentication
var jwtKey = builder.Configuration["Jwt:Key"]
             ?? Environment.GetEnvironmentVariable("JWT_KEY")
             ?? "EventTicketBooking_Super_Secret_Key_For_Jwt_Security_2026_!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
               ?? Environment.GetEnvironmentVariable("JWT_ISSUER")
               ?? "EventTicketBooking.Api";
var jwtAudience = builder.Configuration["Jwt:Audience"]
                 ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE")
                 ?? "EventTicketBooking.Client";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});

// Add Controllers & Swagger kèm cấu hình Bearer Auth
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "EventTicketBooking.Api",
        Version = "v1",
        Description = "API Backend Bán vé sự kiện có sơ đồ ghế (.NET 9)"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Nhập JWT Bearer token vào ô bên dưới (không cần gõ tiền tố 'Bearer '):"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Register Custom Services
builder.Services.AddScoped<EventTicketBooking.Api.Services.IPasswordHasher, EventTicketBooking.Api.Services.PasswordHasher>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Tự động seed tài khoản mẫu khi khởi động ở môi trường Dev
    await DbSeeder.SeedAsync(app.Services);
}

// Bắt buộc UseAuthentication() phải đứng trước UseAuthorization()
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<EventTicketBooking.Api.Middlewares.RoleAuthorizationMiddleware>();
app.MapControllers();

app.Run();
