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
            if (!_context.Database.IsRelational())
                return await HoldSeatsCoreAsync(showtimeId, seatIds, userId, cancellationToken);
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            if (_context.Database.IsNpgsql() && seatIds != null && seatIds.Count > 0)
            {
                await _context.Showtimes.FromSqlRaw("SELECT * FROM \"Showtimes\" WHERE \"Id\" = {0} FOR UPDATE", showtimeId)
                    .AsNoTracking().ToListAsync(cancellationToken);
                await _context.Seats.FromSqlRaw("SELECT * FROM \"Seats\" WHERE \"Id\" = ANY({0}) ORDER BY \"Id\" FOR UPDATE", seatIds.ToArray())
                    .AsNoTracking().ToListAsync(cancellationToken);
            }
            var result = await HoldSeatsCoreAsync(showtimeId, seatIds!, userId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        private async Task<HoldSeatsResult> HoldSeatsCoreAsync(Guid showtimeId, List<Guid> seatIds, Guid userId, CancellationToken cancellationToken)
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

            if (showtime.Status != ShowtimeStatus.OnSale)
                return HoldSeatsResult.InvalidResult("Suất diễn chưa mở bán hoặc đã đóng bán.");

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

            var soldSeatIds = seats.Where(s => s.Status == "SOLD").Select(s => s.Id).ToList();
            if (soldSeatIds.Count > 0)
                return HoldSeatsResult.ConflictResult("Ghế đã được bán.", soldSeatIds);

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
                        var redisConflictingSeats = await _context.SeatHold
                            .AsNoTracking()
                            .Where(sh => seatIds.Contains(sh.SeatId) && sh.Status == "ACTIVE" && sh.ExpiresAt > now && sh.UserId != userId)
                            .Select(sh => sh.SeatId)
                            .ToListAsync(cancellationToken);

                        if (redisConflictingSeats.Count == 0)
                        {
                            redisConflictingSeats = seatIds;
                        }

                        _logger.LogWarning("Tranh chấp giữ ghế qua Redis: User {UserId} bị từ chối khi giữ các ghế [{SeatIds}]. Ghế tranh chấp: [{ConflictingSeats}].",
                            userId, string.Join(", ", seatIds), string.Join(", ", redisConflictingSeats));

                        return HoldSeatsResult.ConflictResult("Một hoặc nhiều ghế đã được giữ chỗ bởi người dùng khác.", redisConflictingSeats);
                    }

                    redisCheckedAndHeld = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi tương tác Redis khi kiểm tra giữ ghế. Chuyển sang kiểm tra Database.");
                }
            }

            // 5. Khử các giữ chỗ cũ đã hết hạn trên các ghế được yêu cầu (đổi Status sang EXPIRED để không vi phạm ràng buộc Unique)
            var expiredActiveHolds = await _context.SeatHold
                .Where(sh => seatIds.Contains(sh.SeatId) && sh.Status == "ACTIVE" && sh.ExpiresAt <= now)
                .ToListAsync(cancellationToken);

            if (expiredActiveHolds.Count > 0)
            {
                foreach (var expiredHold in expiredActiveHolds)
                {
                    expiredHold.Status = "EXPIRED";
                }
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 6. Kiểm tra trạng thái các ghế bị giữ bởi người khác trong Database
            var conflictingSeatsInDb = await _context.SeatHold
                .AsNoTracking()
                .Where(sh => seatIds.Contains(sh.SeatId) && sh.Status == "ACTIVE" && sh.ExpiresAt > now && sh.UserId != userId)
                .Select(sh => sh.SeatId)
                .ToListAsync(cancellationToken);

            if (conflictingSeatsInDb.Count > 0)
            {
                _logger.LogWarning("Tranh chấp giữ ghế (Hold Conflict): User {UserId} yêu cầu giữ các ghế [{RequestedSeatIds}], nhưng các ghế [{ConflictingSeatIds}] đang bị giữ bởi người khác.",
                    userId, string.Join(", ", seatIds), string.Join(", ", conflictingSeatsInDb));

                if (redisCheckedAndHeld)
                {
                    await ReleaseRedisHoldConditionalAsync(redisKeys, userId);
                }

                return HoldSeatsResult.ConflictResult("Một hoặc nhiều ghế đã được giữ chỗ bởi người dùng khác.", conflictingSeatsInDb);
            }

            // 7. Ghi dữ liệu bền vững vào PostgreSQL seat_holds
            var ownHolds = await _context.SeatHold.AsNoTracking()
                .Where(h => seatIds.Contains(h.SeatId) && h.UserId == userId && h.Status == "ACTIVE" && h.ExpiresAt > now)
                .ToListAsync(cancellationToken);
            var ownSeatIds = ownHolds.Select(h => h.SeatId).ToHashSet();
            if (ownHolds.Count > 0) expiresAt = ownHolds.Min(h => h.ExpiresAt) < expiresAt ? ownHolds.Min(h => h.ExpiresAt) : expiresAt;
            var newHolds = seats.Where(s => !ownSeatIds.Contains(s.Id)).Select(s => new SeatHolds
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
            catch (DbUpdateException ex)
            {
                _context.ChangeTracker.Clear();

                _logger.LogWarning(ex, "Tranh chấp giữ ghế ở tầng DB (Unique Constraint Conflict T-29/T-30): User {UserId} vi phạm khóa Unique khi cố gắng giữ các ghế [{SeatIds}].",
                    userId, string.Join(", ", seatIds));

                if (redisCheckedAndHeld)
                {
                    await ReleaseRedisHoldConditionalAsync(redisKeys, userId);
                }

                var activeConflictingSeats = await _context.SeatHold
                    .AsNoTracking()
                    .Where(sh => seatIds.Contains(sh.SeatId) && sh.Status == "ACTIVE" && sh.ExpiresAt > now && sh.UserId != userId)
                    .Select(sh => sh.SeatId)
                    .ToListAsync(cancellationToken);

                if (activeConflictingSeats.Count == 0)
                {
                    activeConflictingSeats = seatIds;
                }

                return HoldSeatsResult.ConflictResult("Một hoặc nhiều ghế đã được giữ chỗ bởi người dùng khác.", activeConflictingSeats);
            }
            catch (Exception ex)
            {
                _context.ChangeTracker.Clear();
                _logger.LogError(ex, "Lỗi không xác định khi lưu seat_holds vào cơ sở dữ liệu. Tiến hành hoán tác (compensation) giải phóng có điều kiện Redis keys.");

                if (redisCheckedAndHeld)
                {
                    await ReleaseRedisHoldConditionalAsync(redisKeys, userId);
                }

                return HoldSeatsResult.ErrorResult("Không thể lưu thông tin giữ chỗ vào cơ sở dữ liệu. Vui lòng thử lại.");
            }

            // 7. Trả về kết quả giữ chỗ thành công
            var responseData = new HoldSeatsResponseDto
            {
                ShowtimeId = showtimeId,
                SeatIds = seatIds,
                ExpiresAt = expiresAt,
                ServerTime = now
            };

            return HoldSeatsResult.SuccessResult(responseData, "Giữ chỗ ghế thành công.");
        }

        /// <summary>
        /// Huỷ giữ chỗ một ghế (Task T-25).
        /// </summary>
        public async Task<HoldSeatsResult> CancelSeatHoldAsync(
            Guid showtimeId,
            Guid seatId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;

            var showtime = await _context.Showtimes
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null)
            {
                return HoldSeatsResult.NotFoundResult("Suất chiếu không tồn tại.");
            }

            var hold = await _context.SeatHold
                .FirstOrDefaultAsync(sh => sh.SeatId == seatId && sh.Seat.ShowtimeId == showtimeId && sh.Status == "ACTIVE" && sh.ExpiresAt > now, cancellationToken);

            if (hold == null)
            {
                return HoldSeatsResult.NotFoundResult("Ghế này hiện không được giữ.");
            }

            if (hold.UserId != userId)
            {
                return HoldSeatsResult.ForbiddenResult("Bạn không có quyền huỷ giữ chỗ của ghế này.");
            }

            var pendingExpirations = await _context.OrderItems
                .Where(i => i.SeatId == seatId && i.Order.UserId == userId && i.Order.Status == OrderStatus.Pending)
                .Select(i => i.Order.ExpiresAt).ToListAsync(cancellationToken);
            if (pendingExpirations.Any(expiry => expiry > DateTimeOffset.UtcNow))
                return HoldSeatsResult.ConflictResult("Ghế thuộc đơn hàng đang chờ thanh toán.", new List<Guid> { seatId });

            _context.SeatHold.Remove(hold);
            await _context.SaveChangesAsync(cancellationToken);

            if (_redis != null && _redis.IsConnected)
            {
                var redisKey = (RedisKey)$"seat_hold:{showtime.EventId}:{seatId}";
                await ReleaseRedisHoldConditionalAsync(new[] { redisKey }, userId);
            }

            return HoldSeatsResult.SuccessResult(null!, "Huỷ giữ ghế thành công.");
        }

        /// <summary>
        /// Lấy danh sách giữ chỗ còn hiệu lực của người dùng trong một suất diễn (Task T-32 / S-14).
        /// </summary>
        public async Task<HoldSeatsResult> GetUserActiveHoldsAsync(
            Guid showtimeId,
            Guid userId,
            DateTime? nowOverride = null,
            CancellationToken cancellationToken = default)
        {
            var now = nowOverride ?? DateTime.UtcNow;

            var showtimeExists = await _context.Showtimes
                .AsNoTracking()
                .AnyAsync(s => s.Id == showtimeId, cancellationToken);

            if (!showtimeExists)
            {
                return HoldSeatsResult.NotFoundResult("Suất chiếu không tồn tại.");
            }

            var activeHoldsWithSeats = await (from sh in _context.SeatHold
                                              join s in _context.Seats on sh.SeatId equals s.Id
                                              where s.ShowtimeId == showtimeId &&
                                                    sh.UserId == userId &&
                                                    sh.Status == "ACTIVE" &&
                                                    sh.ExpiresAt > now
                                              orderby s.Row, s.SeatNumber
                                              select new
                                              {
                                                  SeatId = s.Id,
                                                  Row = s.Row,
                                                  SeatNumber = s.SeatNumber,
                                                  ExpiresAt = sh.ExpiresAt
                                              }).ToListAsync(cancellationToken);

            if (activeHoldsWithSeats.Count == 0)
            {
                var emptyData = new HoldSeatsResponseDto
                {
                    ShowtimeId = showtimeId,
                    SeatIds = new List<Guid>(),
                    ExpiresAt = DateTime.MinValue,
                    ServerTime = now,
                    Holds = new List<UserSeatHoldItemDto>()
                };

                return HoldSeatsResult.SuccessResult(emptyData, "Người dùng không có ghế nào đang giữ cho suất chiếu này.");
            }

            var holdItems = activeHoldsWithSeats.Select(item => new UserSeatHoldItemDto
            {
                SeatId = item.SeatId,
                Row = item.Row,
                SeatNumber = item.SeatNumber,
                ExpiresAt = item.ExpiresAt
            }).ToList();

            var seatIds = activeHoldsWithSeats.Select(item => item.SeatId).ToList();
            var earliestExpiration = activeHoldsWithSeats.Min(item => item.ExpiresAt);

            var resultData = new HoldSeatsResponseDto
            {
                ShowtimeId = showtimeId,
                SeatIds = seatIds,
                ExpiresAt = earliestExpiration,
                ServerTime = now,
                Holds = holdItems
            };

            return HoldSeatsResult.SuccessResult(resultData, "Lấy danh sách ghế đang giữ thành công.");
        }

        /// <summary>
        /// Tính tổng số vé mà 1 người dùng đang nắm giữ trong 1 suất diễn (Task S-42.1).
        /// Tổng vé = Số ghế đang giữ chỗ còn hạn (SeatHold ACTIVE) + Số ghế trong các đơn hàng đã mua/chờ thanh toán.
        /// </summary>
        public async Task<int> GetUserTicketCountForShowtimeAsync(
            Guid showtimeId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;

            // 1. Đếm số ghế người dùng đang giữ chỗ tạm thời (ACTIVE và chưa hết hạn)
            var activeHoldCount = await _context.SeatHold
                .AsNoTracking()
                .CountAsync(sh => sh.Seat.ShowtimeId == showtimeId &&
                                  sh.UserId == userId &&
                                  sh.Status == "ACTIVE" &&
                                  sh.ExpiresAt > now, cancellationToken);

            // 2. Đếm số vé/ghế đã đặt nằm trong các đơn hàng không bị hủy (Pending, Completed/Paid)
            var purchasedCount = await _context.OrderItems
                .AsNoTracking()
                .CountAsync(oi => oi.Order.ShowtimeId == showtimeId &&
                                  oi.Order.UserId == userId &&
                                  oi.Order.Status != OrderStatus.Cancelled &&
                                  oi.Order.Status != OrderStatus.Expired, cancellationToken);

            return activeHoldCount + purchasedCount;
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
