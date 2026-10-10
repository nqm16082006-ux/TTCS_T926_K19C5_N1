using System.Security.Cryptography;
using System.Text;
using EventTicketBooking.Api.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EventTicketBooking.Api.Services;

public interface ITicketRateLimiter
{
    Task<int> CheckAsync(string policy, string account, string ip);
}

// Returns zero when allowed, otherwise the remaining wait in seconds.
public sealed class TicketRateLimiter(
    IConnectionMultiplexer? redis,
    IMemoryCache cache,
    IOptions<TicketRateLimitOptions> options,
    TimeProvider clock,
    ILogger<TicketRateLimiter> logger) : ITicketRateLimiter
{
    private readonly object gate = new();
    private const string Script = """
        local wait = 0
        for i = 1, 2 do
            local count = redis.call('INCR', KEYS[i])
            if count == 1 then redis.call('EXPIRE', KEYS[i], ARGV[3]) end
            if count > tonumber(ARGV[i]) then
                wait = math.max(wait, 1, redis.call('TTL', KEYS[i]))
            end
        end
        return wait
        """;

    public async Task<int> CheckAsync(string policy, string account, string ip)
    {
        var settings = options.Value;
        var rule = settings.Policies[policy];
        // Separate identity dimensions; an account changing IP keeps its counter.
        var keys = new[] { Key(policy, "account", account), Key(policy, "ip", ip) };
        if (redis != null)
        {
            try
            {
                return (int)(long)await redis.GetDatabase().ScriptEvaluateAsync(Script,
                    keys.Select(k => (RedisKey)k).ToArray(),
                    new RedisValue[] { rule.AccountLimit, rule.IpLimit, rule.WindowSeconds });
            }
            catch (RedisException ex)
            {
                logger.LogWarning(ex, "Ticket rate-limit Redis unavailable.");
                if (!settings.AllowInMemoryFallback) throw;
            }
        }
        else if (!settings.AllowInMemoryFallback)
            throw new InvalidOperationException("Ticket rate limiting requires Redis.");

        // Development-only fallback: atomic within this process, with expiring entries.
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var wait = 0;
            var limits = new[] { rule.AccountLimit, rule.IpLimit };
            for (var i = 0; i < keys.Length; i++)
            {
                if (!cache.TryGetValue<Counter>(keys[i], out var counter) || counter!.Expires <= now)
                    counter = new Counter(now.AddSeconds(rule.WindowSeconds));
                counter.Count = Math.Min(counter.Count + 1, limits[i] + 1L);
                cache.Set(keys[i], counter, TimeSpan.FromSeconds(Math.Max(1, (counter.Expires - now).TotalSeconds)));
                if (counter.Count > limits[i])
                    wait = Math.Max(wait, (int)Math.Ceiling((counter.Expires - now).TotalSeconds));
            }
            return wait;
        }
    }

    private static string Key(string policy, string dimension, string value) =>
        $"ticket-rate:{{{policy}}}:{dimension}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private sealed class Counter(DateTimeOffset expires)
    {
        public long Count { get; set; }
        public DateTimeOffset Expires { get; } = expires;
    }
}
