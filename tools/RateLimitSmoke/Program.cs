using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

// Isolated policy keys expire automatically; no production counters are touched.
var connection = Environment.GetEnvironmentVariable("S41_TEST_REDIS") ?? "localhost:6379";
var config = ConfigurationOptions.Parse(connection);
config.ConnectTimeout = 1500;
config.ConnectRetry = 0;
config.AbortOnConnectFail = true;
try
{
    using var redis = await ConnectionMultiplexer.ConnectAsync(config);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var policy = "smoke-" + Guid.NewGuid().ToString("N");
    var settings = Options.Create(new TicketRateLimitOptions
    {
        AllowInMemoryFallback = false,
        Policies = new() { [policy] = new() { AccountLimit = 5, IpLimit = 100, WindowSeconds = 2 } }
    });
    var first = new TicketRateLimiter(redis, cache, settings, TimeProvider.System, NullLogger<TicketRateLimiter>.Instance);
    var second = new TicketRateLimiter(redis, cache, settings, TimeProvider.System, NullLogger<TicketRateLimiter>.Instance);
    var results = await Task.WhenAll(Enumerable.Range(0, 20)
        .Select(i => (i % 2 == 0 ? first : second).CheckAsync(policy, "account", "ip-" + i)));
    if (results.Count(r => r == 0) != 5 || results.Any(r => r < 0 || r > 2))
        throw new Exception("Concurrent Redis account limit failed.");
    Console.WriteLine("PASS Redis: two limiter instances share atomic account counters; changing IP cannot bypass.");
    await Task.Delay(2200);
    if (await second.CheckAsync(policy, "account", "ip") != 0)
        throw new Exception("Redis TTL expiry failed.");
    Console.WriteLine("PASS Redis: expired counter allows retry.");
    settings.Value.Policies[policy].IpLimit = 2;
    for (var i = 0; i < 3; i++)
    {
        var wait = await first.CheckAsync(policy, "shared-account-" + i, "shared-ip");
        if ((i < 2 && wait != 0) || (i == 2 && wait <= 0))
            throw new Exception("Redis shared-IP limit failed.");
    }
    Console.WriteLine("PASS Redis: independent account counters still obey shared IP limit.");
}
catch (RedisConnectionException)
{
    Console.Error.WriteLine("Redis test unavailable: start local Redis or configure S41_TEST_REDIS. No Redis verification was completed.");
    Environment.ExitCode = 2;
}
