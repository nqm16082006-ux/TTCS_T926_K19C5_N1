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
        private readonly IDistributedCache _cache;
        private readonly IConfiguration _configuration;

        public HealthController(AppDbContext dbContext, IDistributedCache cache, IConfiguration configuration)
        {
            _dbContext = dbContext;
            _cache = cache;
            _configuration = configuration;
        }

        [HttpGet]
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
                dbError = ex.Message;
            }

            bool isRedisConnected = false;
            string redisError = string.Empty;

            try
            {
                var pingKey = "health_ping_" + Guid.NewGuid();
                await _cache.SetStringAsync(pingKey, "ok", new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5)
                });
                var value = await _cache.GetStringAsync(pingKey);
                isRedisConnected = value == "ok";
            }
            catch (Exception ex)
            {
                redisError = ex.Message;
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