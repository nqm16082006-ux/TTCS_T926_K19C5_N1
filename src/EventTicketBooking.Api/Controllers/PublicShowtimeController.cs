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

            var dtoList = itemsToReturn.Select(s => new PublicShowtimeDto
            {
                Id = s.Id,
                EventId = s.EventId,
                EventTitle = s.Event?.Title ?? string.Empty,
                EventDescription = s.Event?.Description,
                EventLocation = s.Event?.Location ?? string.Empty,
                StartTime = s.StartTime,
                EndTime = s.EndTime,
                AvailableSeats = s.AvailableSeats,
                Status = s.Status.ToString(),
                MinPrice = s.SeatCategories != null && s.SeatCategories.Any() ? s.SeatCategories.Min(sc => sc.Price) : 0m,
                MaxPrice = s.SeatCategories != null && s.SeatCategories.Any() ? s.SeatCategories.Max(sc => sc.Price) : 0m
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
        /// Lấy thông tin chi tiết Suất chiếu công khai theo Event ID và Showtime ID (Dùng cho T-18).
        /// GET /api/public/events/{eventId}/showtimes/{showtimeId}
        /// </summary>
        [HttpGet("events/{eventId:guid}/showtimes/{showtimeId:guid}")]
        [ProducesResponseType(typeof(ApiResponse<PublicShowtimeDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPublicShowtimeDetail(Guid eventId, Guid showtimeId)
        {
            var showtime = await _context.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .Include(s => s.SeatCategories)
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.Id == showtimeId);

            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy suất chiếu."));
            }

            var dto = new PublicShowtimeDto
            {
                Id = showtime.Id,
                EventId = showtime.EventId,
                EventTitle = showtime.Event?.Title ?? string.Empty,
                EventDescription = showtime.Event?.Description,
                EventLocation = showtime.Event?.Location ?? string.Empty,
                StartTime = showtime.StartTime,
                EndTime = showtime.EndTime,
                AvailableSeats = showtime.AvailableSeats,
                Status = showtime.Status.ToString(),
                MinPrice = showtime.SeatCategories != null && showtime.SeatCategories.Any() ? showtime.SeatCategories.Min(sc => sc.Price) : 0m,
                MaxPrice = showtime.SeatCategories != null && showtime.SeatCategories.Any() ? showtime.SeatCategories.Max(sc => sc.Price) : 0m
            };

            return Ok(ApiResponse<PublicShowtimeDto>.SuccessResult(dto, "Lấy thông tin chi tiết suất chiếu thành công."));
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

            // Mock data: Giả lập danh sách các ID ghế đang bị giữ (Cho T-23)
            var mockHeldSeatIds = new List<Guid>();

            // Mock data: Giả lập danh sách các ID ghế đã được bán thành vé (Cho Epic E-06)
            var mockSoldSeatIds = new List<Guid>();

            // TODO (T-23, E-06): Người làm T-23 và E-06 sẽ tháo 2 list mock trên ra và thay bằng LEFT JOIN với bảng thật
            var query = from seat in _context.Seats.AsNoTracking().Where(s => s.ShowtimeId == showtimeId)
                        join category in _context.SeatCategories.AsNoTracking() on seat.SeatCategoryId equals category.Id

                        select new SeatStatusDto
                        {
                            Id = seat.Id,
                            Row = seat.Row,
                            SeatNumber = seat.SeatNumber,
                            CategoryName = category.Name,
                            Price = category.Price,
                            // TODO (T-23, E-06): Cập nhật lại logic này khi có bảng thật
                            Status = mockSoldSeatIds.Contains(seat.Id) ? "SOLD" :
                                    (mockHeldSeatIds.Contains(seat.Id) ? "HELD" : "AVAILABLE")
                        };

            var seats = await query.ToListAsync();

            return Ok(ApiResponse<List<SeatStatusDto>>.SuccessResult(seats, "Lấy danh sách ghế thành công."));
        }
    }
}
