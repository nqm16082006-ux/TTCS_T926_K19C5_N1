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

// Add Controllers & Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.Run();
