using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ giữ ghế (Task T-23 / S-10).
    /// Sử dụng Redis TTL (600s) + Lua Script làm cơ chế atomic realtime chính.
    /// Ghi dữ liệu bền vững vào PostgreSQL seat_holds.
    /// Có cơ chế compensation (hoán tác) xóa Redis keys CÓ ĐIỀU KIỆN (chỉ xóa key thuộc về user) nếu lưu DB thất bại.
    /// </summary>
    public class SeatHoldService : ISeatHoldService
    {
        private readonly AppDbContext _context;
        private readonly IConnectionMultiplexer? _redis;
        private readonly ILogger<SeatHoldService> _logger;

        public const int DefaultHoldTtlSeconds = 600; // 10 phút

        public SeatHoldService(
            AppDbContext context,
            ILogger<SeatHoldService> logger,
            IConnectionMultiplexer? redis = null)
        {
            _context = context;
            _logger = logger;
            _redis = redis;
        }

        public async Task<HoldSeatsResult> HoldSeatsAsync(
            Guid showtimeId,
            List<Guid> seatIds,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            // 1. Validation cơ bản đầu vào
            if (seatIds == null || seatIds.Count == 0)
            {
                return HoldSeatsResult.InvalidResult("Vui lòng chọn ít nhất 1 ghế để giữ chỗ.");
            }

            if (seatIds.Count != seatIds.Distinct().Count())
            {
                return HoldSeatsResult.InvalidResult("Danh sách ghế chứa ID trùng lặp.");
            }

            // 2. Validate Suất chiếu và Ghế trong Database
            var showtime = await _context.Showtimes
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null)
            {
                return HoldSeatsResult.NotFoundResult("Suất chiếu không tồn tại.");
            }

            var seats = await _context.Seats
                .AsNoTracking()
                .Where(s => seatIds.Contains(s.Id))
                .ToListAsync(cancellationToken);

            if (seats.Count != seatIds.Count)
            {
                return HoldSeatsResult.InvalidResult("Một hoặc nhiều ghế không tồn tại.");
            }

            if (seats.Any(s => s.ShowtimeId != showtimeId))
            {
                return HoldSeatsResult.InvalidResult("Một hoặc nhiều ghế không thuộc suất chiếu này.");
            }

            var now = DateTime.UtcNow;
            var expiresAt = now.AddSeconds(DefaultHoldTtlSeconds);

            // 3. Chuẩn bị danh sách Redis keys: seat_hold:{event_id}:{seat_id}
            var redisKeys = seats.Select(s => (RedisKey)$"seat_hold:{showtime.EventId}:{s.Id}").ToArray();

            // 4. Thực thi Redis Lua Script Atomic All-Or-Nothing
            bool redisCheckedAndHeld = false;

            if (_redis != null && _redis.IsConnected)
            {
                var db = _redis.GetDatabase();

                var holdLuaScript = @"
                    for i, key in ipairs(KEYS) do
                        local val = redis.call('GET', key)
                        if val and val ~= ARGV[1] then
                            return 0
                        end
                    end
                    for i, key in ipairs(KEYS) do
                        redis.call('SET', key, ARGV[1], 'EX', ARGV[2])
                    end
                    return 1";

                try
                {
                    var res = (int)await db.ScriptEvaluateAsync(
                        holdLuaScript,
                        redisKeys,
                        new RedisValue[] { userId.ToString(), DefaultHoldTtlSeconds }
                    );

                    if (res == 0)
                    {
                        return HoldSeatsResult.ConflictResult("Một hoặc nhiều ghế đã được giữ chỗ bởi người dùng khác.");
                    }

                    redisCheckedAndHeld = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi tương tác Redis khi kiểm tra giữ ghế. Chuyển sang kiểm tra Database.");
                }
            }

            // 5. Kiểm tra trạng thái trong Database (PostgreSQL)
            var activeHoldsInDb = await _context.SeatHold
                .AsNoTracking()
                .Where(sh => seatIds.Contains(sh.SeatId) && sh.Status == "ACTIVE" && sh.ExpiresAt > now && sh.UserId != userId)
                .AnyAsync(cancellationToken);

            if (activeHoldsInDb)
            {
                // Nếu DB có ghế bị người khác giữ -> Compensation xóa CÓ ĐIỀU KIỆN các Redis keys vừa set (chỉ xóa key thuộc về user)
                if (redisCheckedAndHeld)
                {
                    await ReleaseRedisHoldConditionalAsync(redisKeys, userId);
                }

                return HoldSeatsResult.ConflictResult("Một hoặc nhiều ghế đã được giữ chỗ bởi người dùng khác.");
            }

            // 6. Ghi dữ liệu bền vững vào PostgreSQL seat_holds
            var newHolds = seats.Select(s => new SeatHolds
            {
                Id = Guid.NewGuid(),
                SeatId = s.Id,
                UserId = userId,
                Status = "ACTIVE",
                HeldAt = now,
                ExpiresAt = expiresAt
            }).ToList();

            try
            {
                _context.SeatHold.AddRange(newHolds);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu seat_holds vào PostgreSQL. Tiến hành hoán tác (compensation) giải phóng có điều kiện Redis keys.");

                // Compensation: Xóa CÓ ĐIỀU KIỆN các key Redis vừa tạo (chỉ xóa key sở hữu bởi userId)
                if (redisCheckedAndHeld)
                {
                    await ReleaseRedisHoldConditionalAsync(redisKeys, userId);
                }

                return HoldSeatsResult.ErrorResult("Không thể lưu thông tin giữ chỗ vào cơ sở dữ liệu. Vui lòng thử lại.");
            }

            // 7. Trả về kết quả giữ chỗ thành công
            var responseData = new HoldSeatsResponseDto
            {
                SeatIds = seatIds,
                ExpiresAt = expiresAt,
                ServerTime = now
            };

            return HoldSeatsResult.SuccessResult(responseData, "Giữ chỗ ghế thành công.");
        }

        /// <summary>
        /// Giải phóng hoán tác (compensation) Redis keys CÓ ĐIỀU KIỆN bằng Lua Script.
        /// Chỉ xóa key nếu giá trị hiện tại trên Redis bằng chính userId của request.
        /// </summary>
        private async Task ReleaseRedisHoldConditionalAsync(RedisKey[] redisKeys, Guid userId)
        {
            if (_redis == null || !_redis.IsConnected || redisKeys.Length == 0) return;

            try
            {
                var db = _redis.GetDatabase();

                var conditionalReleaseLuaScript = @"
                    for i, key in ipairs(KEYS) do
                        local current = redis.call('GET', key)
                        if current == ARGV[1] then
                            redis.call('DEL', key)
                        end
                    end
                    return 1";

                await db.ScriptEvaluateAsync(
                    conditionalReleaseLuaScript,
                    redisKeys,
                    new RedisValue[] { userId.ToString() }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thực thi hoán tác xóa có điều kiện Redis keys cho User {UserId}.", userId);
            }
        }
    }
}
