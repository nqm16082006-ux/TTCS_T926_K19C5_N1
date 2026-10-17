using Microsoft.AspNetCore.Authorization;
using EventTicketBooking.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly StackExchange.Redis.IConnectionMultiplexer? _redis;
        private readonly IConfiguration _configuration;

        public HealthController(AppDbContext dbContext, StackExchange.Redis.IConnectionMultiplexer? redis, IConfiguration configuration)
        {
            _dbContext = dbContext;
            _redis = redis;
            _configuration = configuration;
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetHealthStatus()
        {
            bool isDbConnected = false;
            string dbError = string.Empty;

            try
            {
                isDbConnected = await _dbContext.Database.CanConnectAsync();
            }
            catch (Exception ex)
            {
                dbError = $"Database connection failed: {ex.Message}";
            }

            bool isRedisConnected = false;
            string redisError = string.Empty;

            try
            {
                if (_redis != null && _redis.IsConnected)
                {
                    var db = _redis.GetDatabase();
                    await db.PingAsync();
                    isRedisConnected = true;
                }
                else
                {
                    redisError = _redis == null ? "Redis multiplexer service not registered." : "Redis connection is disconnected.";
                }
            }
            catch (Exception ex)
            {
                isRedisConnected = false;
                redisError = $"Redis ping failed: {ex.Message}";
            }

            var response = new
            {
                Status = (isDbConnected && isRedisConnected) ? "Healthy" : "Degraded",
                Timestamp = DateTime.UtcNow,
                Database = new
                {
                    Status = isDbConnected ? "Connected" : "Disconnected",
                    Error = string.IsNullOrEmpty(dbError) ? null : dbError
                },
                Redis = new
                {
                    Status = isRedisConnected ? "Connected" : "Disconnected",
                    Error = string.IsNullOrEmpty(redisError) ? null : redisError
                },
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"
            };

            return (isDbConnected && isRedisConnected) ? Ok(response) : StatusCode(503, response);
        }
    }
}
