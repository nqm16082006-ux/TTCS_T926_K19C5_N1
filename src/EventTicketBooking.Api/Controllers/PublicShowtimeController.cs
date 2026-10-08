using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Public Controller phục vụ truy vấn Suất chiếu đang mở bán công khai (Task T-17).
    /// không yêu cầu đăng nhập.
    /// Hỗ trợ Cursor Pagination, Sorting theo StartTime và Caching Redis TTL 30s.
    /// </summary>
    [ApiController]
    [Route("api/public")]
    public class PublicShowtimeController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IDistributedCache _cache;
        private readonly ILogger<PublicShowtimeController> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public PublicShowtimeController(
            AppDbContext context,
            IDistributedCache cache,
            ILogger<PublicShowtimeController> logger)
        {
            _context = context;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Truy vấn danh sách Suất chiếu đang mở bán (Status = OnSale).
        /// GET /api/public/showtimes?cursor={cursor}&limit={limit}
        /// </summary>
        [HttpGet("showtimes")]
        [ProducesResponseType(typeof(ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetOnSaleShowtimes(
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 10)
        {
            if (limit <= 0) limit = 10;
            if (limit > 50) limit = 50;

            string normalizedCursor = cursor?.Trim() ?? "none";
            string cacheKey = $"public:showtimes:cursor:{normalizedCursor}:limit:{limit}";

            // 1. Kiểm tra Redis Cache Hit
            try
            {
                var cachedData = await _cache.GetStringAsync(cacheKey);
                if (!string.IsNullOrEmpty(cachedData))
                {
                    var cachedResponse = JsonSerializer.Deserialize<ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>>(cachedData, JsonOptions);
                    if (cachedResponse != null)
                    {
                        return Ok(cachedResponse);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi đọc từ Redis Cache. Tiếp tục truy vấn Database.");
            }

            // 2. Cache Miss -> Query Database
            var now = DateTime.UtcNow;

            var query = _context.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .Include(s => s.SeatCategories)
                .Where(s => s.Status == ShowtimeStatus.OnSale);

            // Cursor Filter: StartTime > cursorStartTime || (StartTime == cursorStartTime && Id > cursorId)
            if (!string.IsNullOrWhiteSpace(cursor) && TryDecodeCursor(cursor, out DateTime cursorStartTime, out Guid cursorId))
            {
                query = query.Where(s => s.StartTime > cursorStartTime || (s.StartTime == cursorStartTime && s.Id > cursorId));
            }

            // Order By StartTime ASC, Id ASC
            var rawList = await query
                .OrderBy(s => s.StartTime)
                .ThenBy(s => s.Id)
                .Take(limit + 1)
                .ToListAsync();

            bool hasMore = rawList.Count > limit;
            var itemsToReturn = hasMore ? rawList.Take(limit).ToList() : rawList;

            var remainingSeats = await GetRemainingSeatsAsync(itemsToReturn);

            var ownerIds = itemsToReturn.Where(s => s.Event != null).Select(s => s.Event.OwnerId).Distinct().ToList();
            var ownerNames = await _context.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Username);
            var dtoList = itemsToReturn.Select(s =>
            {
                int totalSeats = s.Event?.TotalSeats ?? 0;
                int remaining = remainingSeats[s.Id];

                return new PublicShowtimeDto
                {
                    Id = s.Id,
                    EventId = s.EventId,
                    EventTitle = s.Event?.Title ?? string.Empty,
                    EventDescription = s.Event?.Description,
                    ImageUrl = s.Event?.ImageUrl,
                    EventLocation = s.Event?.Location ?? string.Empty,
                    OrganizerName = s.Event != null ? ownerNames.GetValueOrDefault(s.Event.OwnerId, "Ban Tổ Chức") : "Ban Tổ Chức",
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    AvailableSeats = s.AvailableSeats,
                    TotalSeats = totalSeats,
                    RemainingSeats = remaining,
                    Status = s.Status.ToString(),
                    MinPrice = s.SeatCategories != null && s.SeatCategories.Any() ? (s.SeatCategories.Min(sc => sc.Price) ?? 0) : 0m,
                    MaxPrice = s.SeatCategories != null && s.SeatCategories.Any() ? (s.SeatCategories.Max(sc => sc.Price) ?? 0) : 0m,
                    MaxTicketsPerUser = s.MaxTicketsPerUser > 0 ? s.MaxTicketsPerUser : (s.Event != null && s.Event.MaxTicketsPerUser > 0 ? s.Event.MaxTicketsPerUser : 10)
                };
            }).ToList();

            string? nextCursor = null;
            if (hasMore && itemsToReturn.Count > 0)
            {
                var lastItem = itemsToReturn.Last();
                nextCursor = EncodeCursor(lastItem.StartTime, lastItem.Id);
            }

            var pagedResult = new CursorPagedResultDto<PublicShowtimeDto>
            {
                Items = dtoList,
                NextCursor = nextCursor,
                HasMore = hasMore
            };

            var response = ApiResponse<CursorPagedResultDto<PublicShowtimeDto>>.SuccessResult(
                pagedResult,
                "Lấy danh sách suất chiếu đang mở bán thành công.");

            // 3. Ghi vào Redis Cache với TTL = 30s
            try
            {
                var serialized = JsonSerializer.Serialize(response, JsonOptions);
                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
                };
                await _cache.SetStringAsync(cacheKey, serialized, options);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi ghi vào Redis Cache.");
            }

            return Ok(response);
        }


        /// <summary>
        /// Lấy thông tin chi tiết Sự kiện công khai kèm danh sách Suất chiếu.
        /// GET /api/public/events/{id}
        /// </summary>
        [HttpGet("events/{id:guid}")]
        [ProducesResponseType(typeof(ApiResponse<PublicEventDetailDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPublicEventDetail(Guid id)
        {
            var ev = await _context.Events
                .AsNoTracking()
                .Include(e => e.Owner)
                .Include(e => e.Showtimes)
                    .ThenInclude(s => s.SeatCategories)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy sự kiện."));
            }

            foreach (var s in ev.Showtimes)
            {
                s.Event = ev;
            }

            var remainingSeats = await GetRemainingSeatsAsync(ev.Showtimes.ToList());
            var showtimesList = ev.Showtimes
                .OrderBy(s => s.StartTime)
                .Select(s => new PublicShowtimeDto
                {
                    Id = s.Id,
                    EventId = s.EventId,
                    EventTitle = ev.Title,
                    EventDescription = ev.Description,
                    ImageUrl = ev.ImageUrl,
                    EventLocation = ev.Location,
                    OrganizerName = ev.Owner?.FullName ?? ev.Owner?.Username ?? "Ban Tổ Chức",
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    AvailableSeats = s.AvailableSeats,
                    TotalSeats = ev.TotalSeats,
                    RemainingSeats = remainingSeats[s.Id],
                    Status = s.Status.ToString(),
                    MinPrice = s.SeatCategories != null && s.SeatCategories.Any() ? (s.SeatCategories.Min(sc => sc.Price) ?? 0) : 0m,
                    MaxPrice = s.SeatCategories != null && s.SeatCategories.Any() ? (s.SeatCategories.Max(sc => sc.Price) ?? 0) : 0m,
                    MaxTicketsPerUser = s.MaxTicketsPerUser > 0 ? s.MaxTicketsPerUser : (ev.MaxTicketsPerUser > 0 ? ev.MaxTicketsPerUser : 10)
                }).ToList();

            decimal overallMinPrice = showtimesList.Any(s => s.MinPrice > 0)
                ? showtimesList.Where(s => s.MinPrice > 0).Min(s => s.MinPrice)
                : 0m;
            decimal overallMaxPrice = showtimesList.Any(s => s.MaxPrice > 0)
                ? showtimesList.Max(s => s.MaxPrice)
                : 0m;

            int availableSeats = showtimesList.Sum(s => s.AvailableSeats);

            var eventDetail = new PublicEventDetailDto
            {
                Id = ev.Id,
                Title = ev.Title,
                Description = ev.Description,
                ImageUrl = ev.ImageUrl,
                Location = ev.Location,
                OrganizerName = ev.Owner?.FullName ?? ev.Owner?.Username ?? "Ban Tổ Chức",
                StartTime = ev.StartTime,
                EndTime = ev.EndTime,
                TotalSeats = ev.TotalSeats,
                AvailableSeats = availableSeats,
                MaxTicketsPerUser = ev.MaxTicketsPerUser > 0 ? ev.MaxTicketsPerUser : 10,
                MinPrice = overallMinPrice,
                MaxPrice = overallMaxPrice,
                Showtimes = showtimesList
            };

            return Ok(ApiResponse<PublicEventDetailDto>.SuccessResult(eventDetail, "Lấy thông tin chi tiết sự kiện thành công."));
        }

        /// <summary>
        /// Lấy thông tin chi tiết Suất chiếu công khai theo Event ID và Showtime ID (Dùng cho T-18).
        /// GET /api/public/events/{eventId}/showtimes/{showtimeId} hoặc GET /api/public/showtimes/{showtimeId}
        /// </summary>
        [HttpGet("events/{eventId:guid}/showtimes/{showtimeId:guid}")]
        [HttpGet("showtimes/{showtimeId:guid}")]
        [ProducesResponseType(typeof(ApiResponse<PublicShowtimeDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPublicShowtimeDetail(Guid? eventId, Guid showtimeId)
        {
            var query = _context.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .Include(s => s.SeatCategories)
                .Where(s => s.Id == showtimeId);

            if (eventId.HasValue)
            {
                query = query.Where(s => s.EventId == eventId.Value);
            }

            var showtime = await query.FirstOrDefaultAsync();

            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy suất chiếu."));
            }

            var ownerName = showtime.Event != null ? await _context.Users.AsNoTracking()
                .Where(u => u.Id == showtime.Event.OwnerId).Select(u => u.FullName ?? u.Username).FirstOrDefaultAsync() : null;
            var remainingSeats = await GetRemainingSeatsAsync(new List<Showtime> { showtime });
            var dto = new PublicShowtimeDto
            {
                Id = showtime.Id,
                EventId = showtime.EventId,
                EventTitle = showtime.Event?.Title ?? string.Empty,
                EventDescription = showtime.Event?.Description,
                ImageUrl = showtime.Event?.ImageUrl,
                EventLocation = showtime.Event?.Location ?? string.Empty,
                OrganizerName = ownerName ?? "Ban Tổ Chức",
                StartTime = showtime.StartTime,
                EndTime = showtime.EndTime,
                AvailableSeats = showtime.AvailableSeats,
                TotalSeats = showtime.Event?.TotalSeats ?? 0,
                RemainingSeats = remainingSeats[showtime.Id],
                Status = showtime.Status.ToString(),
                MinPrice = showtime.SeatCategories != null && showtime.SeatCategories.Any() ? (showtime.SeatCategories.Min(sc => sc.Price) ?? 0) : 0m,
                MaxPrice = showtime.SeatCategories != null && showtime.SeatCategories.Any() ? (showtime.SeatCategories.Max(sc => sc.Price) ?? 0) : 0m,
                MaxTicketsPerUser = showtime.MaxTicketsPerUser > 0 ? showtime.MaxTicketsPerUser : (showtime.Event != null && showtime.Event.MaxTicketsPerUser > 0 ? showtime.Event.MaxTicketsPerUser : 10)
            };

            return Ok(ApiResponse<PublicShowtimeDto>.SuccessResult(dto, "Lấy thông tin chi tiết suất chiếu thành công."));
        }

        private async Task<Dictionary<Guid, int>> GetRemainingSeatsAsync(List<Showtime> showtimes)
        {
            var now = DateTime.UtcNow;
            var showtimeIds = showtimes.Select(s => s.Id).ToList();
            // Số ghế đang bị hold active (chưa hết hạn), group theo ShowtimeId
            // Dùng join tường minh để tương thích cả PostgreSQL và InMemory test provider
            var activeHeldCountByShowtime = await (
                from sh in _context.SeatHold.AsNoTracking()
                join seat in _context.Seats.AsNoTracking() on sh.SeatId equals seat.Id
                where sh.Status == "ACTIVE" && sh.ExpiresAt > now && showtimeIds.Contains(seat.ShowtimeId)
                group sh by seat.ShowtimeId into g
                select new { ShowtimeId = g.Key, Count = g.Count() }
            ).ToDictionaryAsync(x => x.ShowtimeId, x => x.Count);

            // Số ghế đã bán (paid orders), group theo ShowtimeId
            var paidSeatCountByShowtime = await (
                from oi in _context.OrderItems.AsNoTracking()
                join o in _context.Orders.AsNoTracking() on oi.OrderId equals o.Id
                where o.Status == OrderStatus.Paid && showtimeIds.Contains(o.ShowtimeId)
                group oi by o.ShowtimeId into g
                select new { ShowtimeId = g.Key, Count = g.Count() }
            ).ToDictionaryAsync(x => x.ShowtimeId, x => x.Count);

            // Tổng số ghế thực tế trong DB theo showtime
            var totalSeatCountByShowtime = await _context.Seats
                .AsNoTracking()
                .Where(s => showtimeIds.Contains(s.ShowtimeId))
                .GroupBy(s => s.ShowtimeId)
                .Select(g => new { ShowtimeId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ShowtimeId, x => x.Count);


            return showtimes.ToDictionary(s => s.Id, s =>
            {
                int baseSeatCount;
                if (totalSeatCountByShowtime.TryGetValue(s.Id, out int actual) && actual > 0)
                {
                    baseSeatCount = actual;
                }
                else if (s.AvailableSeats > 0)
                {
                    baseSeatCount = s.AvailableSeats;
                }
                else if (s.Event != null && s.Event.TotalSeats > 0)
                {
                    baseSeatCount = s.Event.TotalSeats;
                }
                else
                {
                    baseSeatCount = 0;
                }

                int held = activeHeldCountByShowtime.GetValueOrDefault(s.Id, 0);
                int sold = paidSeatCountByShowtime.GetValueOrDefault(s.Id, 0);

                return Math.Max(0, baseSeatCount - held - sold);
            });
        }

        #region Cursor Encoding/Decoding Helpers

        public static string EncodeCursor(DateTime startTime, Guid id)
        {
            string raw = $"{startTime.ToUniversalTime().Ticks}|{id}";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        public static bool TryDecodeCursor(string cursor, out DateTime startTime, out Guid id)
        {
            startTime = DateTime.MinValue;
            id = Guid.Empty;

            try
            {
                byte[] bytes = Convert.FromBase64String(cursor);
                string decoded = Encoding.UTF8.GetString(bytes);
                string[] parts = decoded.Split('|');

                if (parts.Length == 2 &&
                    long.TryParse(parts[0], out long ticks) &&
                    Guid.TryParse(parts[1], out Guid parsedId))
                {
                    startTime = new DateTime(ticks, DateTimeKind.Utc);
                    id = parsedId;
                    return true;
                }
            }
            catch
            {
                // Cursor không hợp lệ
            }

            return false;
        }

        #endregion

        /// <summary>
        /// T-19: Truy vấn toàn bộ ghế của suất diễn kèm trạng thái (trống, giữ chỗ, đã bán) trong 1 query.
        /// </summary>
        [HttpGet("showtimes/{showtimeId:guid}/seats")]
        [ProducesResponseType(typeof(ApiResponse<List<SeatStatusDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetShowtimeSeats(Guid showtimeId)
        {
            var showtimeExists = await _context.Showtimes.AnyAsync(s => s.Id == showtimeId);
            if (!showtimeExists)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy suất chiếu."));
            }

            var now = DateTime.UtcNow;

            var activeHeldSeatIds = await _context.SeatHold
                .AsNoTracking()
                .Where(sh => sh.Status == "ACTIVE" && sh.ExpiresAt > now)
                .Select(sh => sh.SeatId)
                .ToListAsync();

            // Lấy danh sách ghế của các đơn hàng đã thanh toán (Task T-36)
            var paidSeatIds = await _context.OrderItems
                .AsNoTracking()
                .Where(oi => oi.Order.ShowtimeId == showtimeId && oi.Order.Status == OrderStatus.Paid)
                .Select(oi => oi.SeatId)
                .ToListAsync();

            var query = from seat in _context.Seats.AsNoTracking().Where(s => s.ShowtimeId == showtimeId)
                        join category in _context.SeatCategories.AsNoTracking() on seat.SeatCategoryId equals category.Id

                        select new SeatStatusDto
                        {
                            Id = seat.Id,
                            Row = seat.Row,
                            SeatNumber = seat.SeatNumber,
                            CategoryName = category.Name,
                            Price = category.Price,
                            Status = seat.Status == "SOLD" || paidSeatIds.Contains(seat.Id) ? "SOLD" :
                                    (activeHeldSeatIds.Contains(seat.Id) ? "HELD" : "AVAILABLE")
                        };

            var seats = await query.ToListAsync();

            return Ok(ApiResponse<List<SeatStatusDto>>.SuccessResult(seats, "Lấy danh sách ghế thành công."));
        }
    }
}
