using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Moq;
using StackExchange.Redis;

namespace EventTicketBooking.Tests.Mocks
{
    public class MockRedisState
    {
        public ConcurrentDictionary<string, (string Value, DateTime? Expiry)> Storage { get; } = new();

        public void Clear() => Storage.Clear();

        public bool IsKeyValid(string key)
        {
            if (!Storage.TryGetValue(key, out var item)) return false;
            if (item.Expiry.HasValue && item.Expiry.Value <= DateTime.UtcNow)
            {
                Storage.TryRemove(key, out _);
                return false;
            }
            return true;
        }
    }

    public static class MockRedisHelper
    {
        public static IConnectionMultiplexer CreateMockRedis(MockRedisState? state = null)
        {
            var storageState = state ?? new MockRedisState();
            var mockRedis = new Mock<IConnectionMultiplexer>();
            var mockDb = new Mock<IDatabase>();

            mockRedis.Setup(r => r.IsConnected).Returns(true);
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);

            mockDb.Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, CommandFlags flags) => storageState.IsKeyValid(key.ToString()));

            mockDb.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, CommandFlags flags) =>
                {
                    var k = key.ToString();
                    if (!storageState.IsKeyValid(k)) return null;
                    if (storageState.Storage.TryGetValue(k, out var item) && item.Expiry.HasValue)
                    {
                        var remaining = item.Expiry.Value - DateTime.UtcNow;
                        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                    }
                    return null;
                });

            mockDb.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, long value, CommandFlags flags) =>
                {
                    var k = key.ToString();
                    long updated = 0;
                    storageState.Storage.AddOrUpdate(k,
                        addKey =>
                        {
                            updated = value;
                            return (value.ToString(), null);
                        },
                        (updateKey, existing) =>
                        {
                            long current = 0;
                            if (storageState.IsKeyValid(updateKey))
                            {
                                long.TryParse(existing.Value, out current);
                            }
                            updated = current + value;
                            return (updated.ToString(), existing.Expiry);
                        });
                    return updated;
                });

            mockDb.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, TimeSpan? expiry, CommandFlags flags) =>
                {
                    var k = key.ToString();
                    if (!storageState.IsKeyValid(k)) return false;
                    var val = storageState.Storage[k].Value;
                    DateTime? expDate = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null;
                    storageState.Storage[k] = (val, expDate);
                    return true;
                });

            mockDb.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, RedisValue value, TimeSpan? expiry, bool keepTtl, When when, CommandFlags flags) =>
                {
                    var k = key.ToString();
                    DateTime? expDate = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null;
                    storageState.Storage[k] = (value.ToString(), expDate);
                    return true;
                });

            mockDb.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, CommandFlags flags) =>
                {
                    var k = key.ToString();
                    return storageState.Storage.TryRemove(k, out _);
                });

            return mockRedis.Object;
        }

        public static IConnectionMultiplexer CreateDisconnectedRedis()
        {
            var mockRedis = new Mock<IConnectionMultiplexer>();
            mockRedis.Setup(r => r.IsConnected).Returns(false);
            return mockRedis.Object;
        }
    }
}
