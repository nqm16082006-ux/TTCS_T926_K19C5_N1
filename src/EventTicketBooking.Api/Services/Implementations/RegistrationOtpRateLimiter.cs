using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class RegistrationOtpRateLimiter : IRegistrationOtpRateLimiter
    {
        private readonly IConnectionMultiplexer? _redis;
        private readonly IMemoryCache _cache;
        private readonly RegistrationOtpRateLimitOptions _options;
        private readonly TimeProvider _clock;
        private readonly ILogger<RegistrationOtpRateLimiter>? _logger;
        private readonly object _gate = new();

        private const string RedisLuaScript = """
            local count = redis.call('INCR', KEYS[1])
            if count == 1 then
                redis.call('EXPIRE', KEYS[1], ARGV[2])
            end
            local ttl = redis.call('TTL', KEYS[1])
            return {count, ttl}
            """;

        public RegistrationOtpRateLimiter(
            IConnectionMultiplexer? redis = null,
            IMemoryCache? cache = null,
            IOptions<RegistrationOtpRateLimitOptions>? options = null,
            TimeProvider? clock = null,
            ILogger<RegistrationOtpRateLimiter>? logger = null)
        {
            _redis = redis;
            _cache = cache ?? new MemoryCache(new MemoryCacheOptions());
            _options = options?.Value ?? new RegistrationOtpRateLimitOptions();
            _clock = clock ?? TimeProvider.System;
            _logger = logger;
        }

        public async Task<OtpRateLimitResult> CheckAndRecordResendAsync(string email)
        {
            var normalizedEmail = NormalizeEmail(email);
            var maxAttempts = _options.MaxResendAttemptsPerHour > 0 ? _options.MaxResendAttemptsPerHour : 5;
            var windowSeconds = _options.WindowSeconds > 0 ? _options.WindowSeconds : 3600;
            var key = CacheKey(normalizedEmail);

            if (_redis != null && _redis.IsConnected)
            {
                try
                {
                    var db = _redis.GetDatabase();
                    var redisResult = (RedisValue[]?)await db.ScriptEvaluateAsync(
                        RedisLuaScript,
                        new RedisKey[] { key },
                        new RedisValue[] { maxAttempts, windowSeconds });

                    if (redisResult != null && redisResult.Length >= 2)
                    {
                        var count = (long)redisResult[0];
                        var ttl = (long)redisResult[1];
                        if (ttl <= 0) ttl = windowSeconds;

                        var retryAfterSeconds = Math.Max(1, (int)ttl);
                        var retryAfterMinutes = (int)Math.Max(1, Math.Ceiling(retryAfterSeconds / 60.0));

                        if (count > maxAttempts)
                        {
                            _logger?.LogWarning("Email {Email} exceeded OTP resend rate limit ({Count}/{Limit}). Wait: {Seconds}s.",
                                normalizedEmail, count, maxAttempts, retryAfterSeconds);
                            return new OtpRateLimitResult(false, 0, retryAfterSeconds, retryAfterMinutes);
                        }

                        var remaining = (int)Math.Max(0, maxAttempts - count);
                        return new OtpRateLimitResult(true, remaining, retryAfterSeconds, retryAfterMinutes);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Redis error during OTP rate limiting for {Email}. Falling back to in-memory cache.", normalizedEmail);
                }
            }

            // Fallback in-memory
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (!_cache.TryGetValue<ResendCounter>(key, out var entry) || entry == null || entry.ExpiresAt <= now)
                {
                    entry = new ResendCounter
                    {
                        Count = 1,
                        ExpiresAt = now.AddSeconds(windowSeconds)
                    };
                    _cache.Set(key, entry, TimeSpan.FromSeconds(windowSeconds));

                    var rem = maxAttempts - 1;
                    return new OtpRateLimitResult(true, rem, windowSeconds, (int)Math.Ceiling(windowSeconds / 60.0));
                }

                if (entry.Count >= maxAttempts)
                {
                    var waitSec = Math.Max(1, (int)Math.Ceiling((entry.ExpiresAt - now).TotalSeconds));
                    var waitMin = (int)Math.Max(1, Math.Ceiling(waitSec / 60.0));
                    _logger?.LogWarning("Email {Email} exceeded in-memory OTP resend limit ({Count}/{Limit}). Wait: {Seconds}s.",
                        normalizedEmail, entry.Count, maxAttempts, waitSec);
                    return new OtpRateLimitResult(false, 0, waitSec, waitMin);
                }

                entry.Count++;
                var remainingSec = Math.Max(1, (int)Math.Ceiling((entry.ExpiresAt - now).TotalSeconds));
                _cache.Set(key, entry, TimeSpan.FromSeconds(remainingSec));

                var remainingAttempts = (int)Math.Max(0, maxAttempts - entry.Count);
                return new OtpRateLimitResult(true, remainingAttempts, remainingSec, (int)Math.Ceiling(remainingSec / 60.0));
            }
        }

        public async Task<int> GetRemainingAttemptsAsync(string email)
        {
            var normalizedEmail = NormalizeEmail(email);
            var maxAttempts = _options.MaxResendAttemptsPerHour > 0 ? _options.MaxResendAttemptsPerHour : 5;
            var key = CacheKey(normalizedEmail);

            if (_redis != null && _redis.IsConnected)
            {
                try
                {
                    var val = await _redis.GetDatabase().StringGetAsync(key);
                    if (val.HasValue && long.TryParse(val, out var count))
                    {
                        return (int)Math.Max(0, maxAttempts - count);
                    }
                    return maxAttempts;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Redis error checking remaining OTP attempts for {Email}.", normalizedEmail);
                }
            }

            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (_cache.TryGetValue<ResendCounter>(key, out var entry) && entry != null && entry.ExpiresAt > now)
                {
                    return (int)Math.Max(0, maxAttempts - entry.Count);
                }
                return maxAttempts;
            }
        }

        public async Task ResetAsync(string email)
        {
            var normalizedEmail = NormalizeEmail(email);
            var key = CacheKey(normalizedEmail);

            if (_redis != null && _redis.IsConnected)
            {
                try
                {
                    await _redis.GetDatabase().KeyDeleteAsync(key);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Redis error deleting OTP rate limit key for {Email}.", normalizedEmail);
                }
            }

            lock (_gate)
            {
                _cache.Remove(key);
            }
        }

        private static string NormalizeEmail(string? email) =>
            string.IsNullOrWhiteSpace(email) ? "unknown" : email.Trim().ToLowerInvariant();

        private static string CacheKey(string normalizedEmail) =>
            $"otp:resend:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)))}";

        private sealed class ResendCounter
        {
            public int Count { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }
    }
}
