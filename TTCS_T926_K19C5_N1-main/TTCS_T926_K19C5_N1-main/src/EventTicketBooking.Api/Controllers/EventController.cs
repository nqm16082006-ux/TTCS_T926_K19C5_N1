using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller quản lý Sự kiện (Task T-10):
    /// - Cho phép xem, tạo, sửa sự kiện cá nhân.
    /// - Đảm bảo phân quyền sở hữu (Event Ownership): chỉ truy cập sự kiện do chính mình tạo ra.
    /// - Trả về 403 Forbidden nếu cố truy cập sự kiện của người dùng khác.
    /// </summary>
    [ApiController]
    [Route("api/events")]
    [RequireRole("Organizer", "Admin")]
    public class EventController : ControllerBase
    {
        private readonly AppDbContext _context;

        public EventController(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Lấy danh sách sự kiện của người dùng đang đăng nhập.
        /// GET /api/events
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<List<EventResponseDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetMyEvents()
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);

            var query = _context.Events
                .AsNoTracking()
                .Include(e => e.Owner)
                .Include(e => e.Showtimes)
                .AsQueryable();

            if (!isAdmin)
            {
                query = query.Where(e => e.OwnerId == currentUserId.Value);
            }

            var events = await query
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            var result = events.Select(MapToEventResponseDto).ToList();
            return Ok(ApiResponse<List<EventResponseDto>>.SuccessResult(result, "Lấy danh sách sự kiện thành công."));
        }

        /// <summary>
        /// Tạo sự kiện mới cho người dùng đang đăng nhập.
        /// POST /api/events
        /// Backend tự động gán OwnerId = currentUserId.
        /// </summary>
        [HttpPost]
        [ProducesResponseType(typeof(ApiResponse<EventResponseDto>), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> CreateEvent([FromBody] CreateEventDto dto)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Dữ liệu gửi lên không hợp lệ.", GetModelStateErrors()));
            }

            if (dto.EndTime <= dto.StartTime)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thời gian kết thúc phải lớn hơn thời gian bắt đầu."));
            }

            if (dto.TotalSeats <= 0)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Tổng số ghế phải lớn hơn 0."));
            }

            var startTimeUtc = dto.StartTime.Kind == DateTimeKind.Utc ? dto.StartTime : dto.StartTime.ToUniversalTime();
            var endTimeUtc = dto.EndTime.Kind == DateTimeKind.Utc ? dto.EndTime : dto.EndTime.ToUniversalTime();

            var newEvent = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = currentUserId.Value,
                Title = dto.Title.Trim(),
                Description = dto.Description?.Trim(),
                ImageUrl = dto.ImageUrl?.Trim(),
                Location = dto.Location.Trim(),
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                TotalSeats = dto.TotalSeats,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Tự động tạo 1 Showtime mặc định cho Event theo thời gian sự kiện (T-09 integration)
            var defaultShowtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = newEvent.Id,
                StartTime = newEvent.StartTime,
                EndTime = newEvent.EndTime,
                AvailableSeats = newEvent.TotalSeats
            };

            newEvent.Showtimes.Add(defaultShowtime);

            _context.Events.Add(newEvent);
            await _context.SaveChangesAsync();

            var responseDto = MapToEventResponseDto(newEvent);
            return StatusCode(StatusCodes.Status201Created, ApiResponse<EventResponseDto>.SuccessResult(responseDto, "Tạo sự kiện thành công."));
        }

        /// <summary>
        /// Lấy thông tin chi tiết của một sự kiện theo ID.
        /// GET /api/events/{id}
        /// Kiểm tra phân quyền sở hữu: trả 404 nếu không tồn tại, trả 403 nếu thuộc về user khác.
        /// </summary>
        [HttpPost("{id:guid}/duplicate")]
        public async Task<IActionResult> DuplicateEvent(Guid id)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue) return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));
            var source = await _context.Events.AsNoTracking()
                .Include(e => e.Showtimes).ThenInclude(s => s.SeatCategories)
                .FirstOrDefaultAsync(e => e.Id == id);
            if (source == null) return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            if (source.OwnerId != userId && !await IsCurrentUserAdminAsync(userId.Value))
                return StatusCode(403, ApiResponse<object>.FailureResult("Bạn không có quyền nhân bản sự kiện này."));

            var showtimeIds = source.Showtimes.Select(s => s.Id).ToList();
            var seats = await _context.Seats.AsNoTracking().Where(s => showtimeIds.Contains(s.ShowtimeId)).ToListAsync();
            const string suffix = " (Bản sao)";
            var copy = new Event
            {
                OwnerId = userId.Value,
                Title = source.Title[..Math.Min(source.Title.Length, 250 - suffix.Length)] + suffix,
                Description = source.Description,
                ImageUrl = source.ImageUrl,
                Location = source.Location,
                StartTime = source.StartTime,
                EndTime = source.EndTime,
                TotalSeats = source.TotalSeats
            };
            foreach (var original in source.Showtimes)
            {
                var originalSeats = seats.Where(s => s.ShowtimeId == original.Id).ToList();
                var show = new Showtime
                {
                    EventId = copy.Id,
                    StartTime = original.StartTime,
                    EndTime = original.EndTime,
                    AvailableSeats = originalSeats.Count > 0 ? originalSeats.Count : original.AvailableSeats
                };
                var categories = original.SeatCategories.ToDictionary(c => c.Id, c => new SeatCategory
                {
                    ShowtimeId = show.Id,
                    Name = c.Name,
                    Price = c.Price
                });
                show.SeatCategories = categories.Values.ToList();
                copy.Showtimes.Add(show);
                foreach (var seat in originalSeats)
                    _context.Seats.Add(new Seat
                    {
                        ShowtimeId = show.Id,
                        SeatCategoryId = categories[seat.SeatCategoryId].Id,
                        Row = seat.Row,
                        SeatNumber = seat.SeatNumber,
                        Status = "AVAILABLE"
                    });
            }
            _context.Events.Add(copy);
            await _context.SaveChangesAsync();
            return StatusCode(201, ApiResponse<EventResponseDto>.SuccessResult(MapToEventResponseDto(copy),
                "Đã nhân bản sự kiện thành bản nháp. Hãy kiểm tra lịch diễn trước khi mở bán."));
        }

        [HttpGet("{id:guid}")]
        [ProducesResponseType(typeof(ApiResponse<EventResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetEventById(Guid id)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .AsNoTracking()
                .Include(e => e.Owner)
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền truy cập sự kiện này."));
            }

            var responseDto = MapToEventResponseDto(ev);
            return Ok(ApiResponse<EventResponseDto>.SuccessResult(responseDto, "Lấy thông tin sự kiện thành công."));
        }

        /// <summary>
        /// Cập nhật thông tin sự kiện.
        /// PUT /api/events/{id}
        /// Không cho phép cập nhật OwnerId.
        /// </summary>
        [HttpPut("{id:guid}")]
        [ProducesResponseType(typeof(ApiResponse<EventResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateEvent(Guid id, [FromBody] UpdateEventDto dto)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền chỉnh sửa sự kiện này."));
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Dữ liệu gửi lên không hợp lệ.", GetModelStateErrors()));
            }

            if (dto.EndTime <= dto.StartTime)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thời gian kết thúc phải lớn hơn thời gian bắt đầu."));
            }

            if (dto.TotalSeats <= 0)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Tổng số ghế phải lớn hơn 0."));
            }

            // Cập nhật các trường thông tin (OwnerId không thay đổi)
            var startTimeUtc = dto.StartTime.Kind == DateTimeKind.Utc ? dto.StartTime : dto.StartTime.ToUniversalTime();
            var endTimeUtc = dto.EndTime.Kind == DateTimeKind.Utc ? dto.EndTime : dto.EndTime.ToUniversalTime();

            ev.Title = dto.Title.Trim();
            ev.Description = dto.Description?.Trim();
            ev.ImageUrl = dto.ImageUrl?.Trim();
            ev.Location = dto.Location.Trim();
            ev.StartTime = startTimeUtc;
            ev.EndTime = endTimeUtc;
            ev.TotalSeats = dto.TotalSeats;
            ev.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var responseDto = MapToEventResponseDto(ev);
            return Ok(ApiResponse<EventResponseDto>.SuccessResult(responseDto, "Cập nhật sự kiện thành công."));
        }

        /// <summary>
        /// Xóa sự kiện.
        /// DELETE /api/events/{id}
        /// </summary>
        [HttpDelete("{id:guid}")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteEvent(Guid id)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền xóa sự kiện này."));
            }

            var showtimeIds = ev.Showtimes.Select(s => s.Id).ToList();
            if (await _context.Orders.AnyAsync(o => showtimeIds.Contains(o.ShowtimeId)))
                return Conflict(ApiResponse<object>.FailureResult("Không thể xóa sự kiện đã có đơn hàng."));
            _context.Events.Remove(ev);
            await _context.SaveChangesAsync();

            return Ok(ApiResponse<object>.SuccessResult(null, "Xóa sự kiện thành công."));
        }

        /// <summary>
        /// Lấy danh sách Suất diễn (Showtimes) thuộc Sự kiện.
        /// GET /api/events/{id}/showtimes
        /// </summary>
        [HttpGet("{id:guid}/showtimes")]
        [ProducesResponseType(typeof(ApiResponse<List<ShowtimeResponseDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetEventShowtimes(Guid id)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .AsNoTracking()
                .Include(e => e.Showtimes)
                    .ThenInclude(s => s.SeatCategories)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền truy cập suất diễn của sự kiện này."));
            }

            var ids = ev.Showtimes.Select(s => s.Id).ToList();
            var seatCounts = await _context.Seats.AsNoTracking().Where(s => ids.Contains(s.ShowtimeId))
                .GroupBy(s => s.ShowtimeId).Select(g => new { Id = g.Key, Total = g.Count() })
                .ToDictionaryAsync(g => g.Id);
            var heldCounts = await _context.SeatHold
                .AsNoTracking()
                .Where(hold => hold.Status == "ACTIVE" && hold.ExpiresAt > DateTime.UtcNow && ids.Contains(hold.Seat.ShowtimeId))
                .GroupBy(hold => hold.Seat.ShowtimeId)
                .Select(group => new { ShowtimeId = group.Key, Total = group.Count() })
                .ToDictionaryAsync(group => group.ShowtimeId, group => group.Total);
            var categorySeatCounts = await _context.Seats
                .AsNoTracking()
                .Where(seat => ids.Contains(seat.ShowtimeId))
                .GroupBy(seat => new { seat.ShowtimeId, seat.SeatCategoryId })
                .Select(group => new
                {
                    group.Key.ShowtimeId,
                    group.Key.SeatCategoryId,
                    Total = group.Count()
                })
                .ToDictionaryAsync(group => (group.ShowtimeId, group.SeatCategoryId), group => group.Total);
            var categoryHeldCounts = await _context.SeatHold
                .AsNoTracking()
                .Where(hold => hold.Status == "ACTIVE" &&
                               hold.ExpiresAt > DateTime.UtcNow &&
                               ids.Contains(hold.Seat.ShowtimeId))
                .GroupBy(hold => new { hold.Seat.ShowtimeId, hold.Seat.SeatCategoryId })
                .Select(group => new
                {
                    group.Key.ShowtimeId,
                    group.Key.SeatCategoryId,
                    Total = group.Count()
                })
                .ToDictionaryAsync(group => (group.ShowtimeId, group.SeatCategoryId), group => group.Total);
            var paidCategorySales = await _context.OrderItems
                .AsNoTracking()
                .Where(item => item.Order.Status == OrderStatus.Paid && ids.Contains(item.Order.ShowtimeId))
                .GroupBy(item => new
                {
                    ShowtimeId = item.Order.ShowtimeId,
                    SeatCategoryId = item.Seat.SeatCategoryId
                })
                .Select(group => new
                {
                    group.Key.ShowtimeId,
                    group.Key.SeatCategoryId,
                    Total = group.Count()
                })
                .ToDictionaryAsync(group => (group.ShowtimeId, group.SeatCategoryId), group => group.Total);
            var showtimesDto = ev.Showtimes.Select(s =>
            {
                var totalActualSeats = seatCounts.TryGetValue(s.Id, out var counts) ? counts.Total : 0;
                var soldSeatCount = s.SeatCategories.Sum(category =>
                    paidCategorySales.GetValueOrDefault((s.Id, category.Id)));
                var heldSeatCount = heldCounts.GetValueOrDefault(s.Id);
                var remainingSeatCount = Math.Max(0, totalActualSeats - soldSeatCount - heldSeatCount);
                var categories = s.SeatCategories
                    .OrderBy(category => category.Name)
                    .Select(category =>
                    {
                        var categoryTotal = categorySeatCounts.GetValueOrDefault((s.Id, category.Id));
                        var categorySold = paidCategorySales.GetValueOrDefault((s.Id, category.Id));
                        var categoryHeld = categoryHeldCounts.GetValueOrDefault((s.Id, category.Id));
                        var categoryAvailable = Math.Max(0, categoryTotal - categorySold - categoryHeld);

                        return new SeatCategoryPriceDto
                        {
                            Id = category.Id,
                            Name = category.Name,
                            Price = category.Price,
                            TotalQuantity = categoryTotal,
                            HeldQuantity = categoryHeld,
                            AvailableQuantity = categoryAvailable,
                            RemainingQuantity = categoryAvailable
                        };
                    })
                    .ToList();

                return new ShowtimeResponseDto
                {
                    Id = s.Id,
                    EventId = s.EventId,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    AvailableSeats = s.AvailableSeats,
                    RemainingTickets = remainingSeatCount,
                    AvailableTickets = remainingSeatCount,
                    ActualSeatCount = totalActualSeats,
                    SoldSeatCount = soldSeatCount,
                    HeldSeatCount = heldSeatCount,
                    Status = s.Status,
                    SeatCategories = categories,
                    StatusActionMessage = s.Status != ShowtimeStatus.Draft ? GetShowtimeStatusActionMessage(s) :
                        !seatCounts.ContainsKey(s.Id) ? "Tải sơ đồ ghế trước khi mở bán." :
                        s.SeatCategories.Count == 0 || s.SeatCategories.Any(c => c.Price is null or < 0) ? "Thiết lập giá cho tất cả hạng ghế trước khi mở bán." :
                        GetShowtimeStatusActionMessage(s)
                };
            }).ToList();

            return Ok(ApiResponse<List<ShowtimeResponseDto>>.SuccessResult(showtimesDto, "Lấy danh sách suất diễn thành công."));
        }

        /// <summary>
        /// Tạo suất diễn mới cho Sự kiện.
        /// POST /api/events/{id}/showtimes
        /// </summary>
        [HttpPost("{id:guid}/showtimes")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<ShowtimeResponseDto>), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CreateShowtime(Guid id, [FromBody] CreateShowtimeDto dto)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền thao tác trên sự kiện này."));
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Dữ liệu không hợp lệ.", GetModelStateErrors()));
            }

            if (dto.EndTime <= dto.StartTime)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thời gian kết thúc phải lớn hơn thời gian bắt đầu."));
            }

            var startTimeUtc = dto.StartTime.Kind == DateTimeKind.Utc ? dto.StartTime : dto.StartTime.ToUniversalTime();
            var endTimeUtc = dto.EndTime.Kind == DateTimeKind.Utc ? dto.EndTime : dto.EndTime.ToUniversalTime();

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                AvailableSeats = dto.AvailableSeats > 0 ? dto.AvailableSeats : ev.TotalSeats
            };

            _context.Showtimes.Add(showtime);
            await _context.SaveChangesAsync();

            var responseDto = new ShowtimeResponseDto
            {
                Id = showtime.Id,
                EventId = showtime.EventId,
                StartTime = showtime.StartTime,
                EndTime = showtime.EndTime,
                AvailableSeats = showtime.AvailableSeats,
                RemainingTickets = showtime.AvailableSeats,
                AvailableTickets = showtime.AvailableSeats,
                Status = showtime.Status,
                SeatCategories = new List<SeatCategoryPriceDto>(),
                StatusActionMessage = GetShowtimeStatusActionMessage(showtime)
            };

            return StatusCode(StatusCodes.Status201Created, ApiResponse<ShowtimeResponseDto>.SuccessResult(responseDto, "Tạo suất diễn thành công."));
        }

        /// <summary>
        /// Cập nhật thời gian hoặc số ghế của suất diễn.
        /// PUT /api/events/{eventId}/showtimes/{showtimeId}
        /// </summary>
        [HttpPut("{eventId:guid}/showtimes/{showtimeId:guid}")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<ShowtimeResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateShowtime(Guid eventId, Guid showtimeId, [FromBody] UpdateShowtimeDto dto)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền thao tác trên sự kiện này."));
            }

            var showtime = ev.Showtimes.FirstOrDefault(s => s.Id == showtimeId);
            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại."));
            }

            if (dto.EndTime <= dto.StartTime)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thời gian kết thúc phải lớn hơn thời gian bắt đầu."));
            }

            var startTimeUtc = dto.StartTime.Kind == DateTimeKind.Utc ? dto.StartTime : dto.StartTime.ToUniversalTime();
            var endTimeUtc = dto.EndTime.Kind == DateTimeKind.Utc ? dto.EndTime : dto.EndTime.ToUniversalTime();

            showtime.StartTime = startTimeUtc;
            showtime.EndTime = endTimeUtc;
            if (dto.AvailableSeats.HasValue && dto.AvailableSeats.Value > 0)
            {
                showtime.AvailableSeats = dto.AvailableSeats.Value;
            }

            await _context.SaveChangesAsync();

            var responseDto = new ShowtimeResponseDto
            {
                Id = showtime.Id,
                EventId = showtime.EventId,
                StartTime = showtime.StartTime,
                EndTime = showtime.EndTime,
                AvailableSeats = showtime.AvailableSeats,
                RemainingTickets = showtime.AvailableSeats,
                AvailableTickets = showtime.AvailableSeats,
                Status = showtime.Status,
                StatusActionMessage = GetShowtimeStatusActionMessage(showtime)
            };

            return Ok(ApiResponse<ShowtimeResponseDto>.SuccessResult(responseDto, "Cập nhật suất diễn thành công."));
        }

        /// <summary>
        /// Lấy số vé bán, ghế đang giữ, ghế còn và doanh thu theo hạng cho từng suất diễn.
        /// GET /api/events/sales-by-showtime
        /// </summary>
        [HttpGet("sales-by-showtime")]
        [ProducesResponseType(typeof(ApiResponse<ShowtimeSalesReportDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetSalesByShowtime()
        {
            Response.Headers["Cache-Control"] = "no-store";
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            var showtimeQuery = _context.Showtimes
                .AsNoTracking()
                .Include(showtime => showtime.Event)
                .Include(showtime => showtime.SeatCategories)
                .AsQueryable();

            if (!isAdmin)
            {
                showtimeQuery = showtimeQuery.Where(showtime => showtime.Event.OwnerId == currentUserId.Value);
            }

            var showtimes = await showtimeQuery
                .OrderBy(showtime => showtime.StartTime)
                .ToListAsync();
            var showtimeIds = showtimes.Select(showtime => showtime.Id).ToList();
            var now = DateTime.UtcNow;

            var seatCounts = await _context.Seats
                .AsNoTracking()
                .Where(seat => showtimeIds.Contains(seat.ShowtimeId))
                .GroupBy(seat => seat.ShowtimeId)
                .Select(group => new { ShowtimeId = group.Key, Total = group.Count() })
                .ToDictionaryAsync(group => group.ShowtimeId, group => group.Total);

            var categorySeatCounts = await _context.Seats
                .AsNoTracking()
                .Where(seat => showtimeIds.Contains(seat.ShowtimeId))
                .GroupBy(seat => new { seat.ShowtimeId, seat.SeatCategoryId })
                .Select(group => new { group.Key.ShowtimeId, group.Key.SeatCategoryId, Total = group.Count() })
                .ToDictionaryAsync(group => (group.ShowtimeId, group.SeatCategoryId), group => group.Total);

            var heldCounts = await _context.SeatHold
                .AsNoTracking()
                .Where(hold => hold.Status == "ACTIVE" &&
                               hold.ExpiresAt > now &&
                               showtimeIds.Contains(hold.Seat.ShowtimeId))
                .GroupBy(hold => hold.Seat.ShowtimeId)
                .Select(group => new { ShowtimeId = group.Key, Total = group.Count() })
                .ToDictionaryAsync(group => group.ShowtimeId, group => group.Total);

            var heldCountsByCategory = await _context.SeatHold
                .AsNoTracking()
                .Where(hold => hold.Status == "ACTIVE" &&
                               hold.ExpiresAt > now &&
                               showtimeIds.Contains(hold.Seat.ShowtimeId))
                .GroupBy(hold => new { hold.Seat.ShowtimeId, hold.Seat.SeatCategoryId })
                .Select(group => new { group.Key.ShowtimeId, group.Key.SeatCategoryId, Total = group.Count() })
                .ToDictionaryAsync(group => (group.ShowtimeId, group.SeatCategoryId), group => group.Total);

            var paidSales = await _context.OrderItems
                .AsNoTracking()
                .Where(item => item.Order.Status == OrderStatus.Paid &&
                               showtimeIds.Contains(item.Order.ShowtimeId))
                .GroupBy(item => new
                {
                    ShowtimeId = item.Order.ShowtimeId,
                    SeatCategoryId = item.Seat.SeatCategoryId
                })
                .Select(group => new
                {
                    group.Key.ShowtimeId,
                    group.Key.SeatCategoryId,
                    TicketSoldCount = group.Count(),
                    Revenue = group.Sum(item => (long)item.Price)
                })
                .ToListAsync();

            var salesByCategory = paidSales.ToDictionary(
                sale => (sale.ShowtimeId, sale.SeatCategoryId));

            var result = showtimes.Select(showtime =>
            {
                var categories = showtime.SeatCategories
                    .OrderBy(category => category.Name)
                    .Select(category =>
                    {
                        salesByCategory.TryGetValue((showtime.Id, category.Id), out var sale);
                        var categorySoldCount = sale?.TicketSoldCount ?? 0;
                        var categoryTotal = categorySeatCounts.GetValueOrDefault((showtime.Id, category.Id), 0);
                        var categoryHeldCount = heldCountsByCategory.GetValueOrDefault((showtime.Id, category.Id), 0);
                        var categoryAvailable = Math.Max(0, categoryTotal - categorySoldCount - categoryHeldCount);

                        return new SeatCategorySalesDto
                        {
                            SeatCategoryId = category.Id,
                            Name = category.Name,
                            TotalQuantity = categoryTotal,
                            TicketSoldCount = categorySoldCount,
                            HeldQuantity = categoryHeldCount,
                            AvailableQuantity = categoryAvailable,
                            RemainingQuantity = categoryAvailable,
                            Revenue = sale?.Revenue ?? 0
                        };
                    })
                    .ToList();
                var soldCount = categories.Sum(category => category.TicketSoldCount);
                var heldCount = heldCounts.GetValueOrDefault(showtime.Id);
                var totalSeats = seatCounts.GetValueOrDefault(showtime.Id);
                var availableSeats = Math.Max(0, totalSeats - soldCount - heldCount);

                return new ShowtimeSalesDto
                {
                    ShowtimeId = showtime.Id,
                    EventTitle = showtime.Event.Title,
                    StartTime = showtime.StartTime,
                    EndTime = showtime.EndTime,
                    TicketSoldCount = soldCount,
                    HeldSeatCount = heldCount,
                    AvailableSeatCount = availableSeats,
                    RemainingTickets = availableSeats,
                    AvailableTickets = availableSeats,
                    RevenueByCategory = categories
                };
            }).ToList();

            var report = new ShowtimeSalesReportDto
            {
                GeneratedAt = DateTime.UtcNow,
                Showtimes = result
            };

            return Ok(ApiResponse<ShowtimeSalesReportDto>.SuccessResult(report, "Lấy báo cáo bán vé theo suất diễn thành công."));
        }

        /// <summary>
        /// Xóa suất diễn.
        /// DELETE /api/events/{eventId}/showtimes/{showtimeId}
        /// </summary>
        [HttpDelete("{eventId:guid}/showtimes/{showtimeId:guid}")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteShowtime(Guid eventId, Guid showtimeId)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền thao tác trên sự kiện này."));
            }

            var showtime = ev.Showtimes.FirstOrDefault(s => s.Id == showtimeId);
            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại."));
            }

            if (showtime.Status == ShowtimeStatus.OnSale)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Không thể xóa suất diễn đang trong trạng thái mở bán. Vui lòng đóng bán trước khi xóa."));
            }

            var hasOrders = await _context.Orders.AnyAsync(o => o.ShowtimeId == showtimeId);
            if (hasOrders)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Không thể xóa suất diễn đã có đơn đặt vé."));
            }

            _context.Showtimes.Remove(showtime);
            await _context.SaveChangesAsync();

            return Ok(ApiResponse<object>.SuccessResult(null, "Xóa suất diễn thành công."));
        }


        /// <summary>
        /// Cập nhật giá các hạng ghế của một suất diễn (Task T-35).
        /// PUT /api/events/{eventId}/showtimes/{showtimeId}/seat-categories/prices
        /// </summary>
        [HttpPut("{eventId:guid}/showtimes/{showtimeId:guid}/seat-categories/prices")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<List<SeatCategoryPriceDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateSeatCategoryPrices(
            Guid eventId,
            Guid showtimeId,
            [FromBody] UpdateSeatCategoryPricesDto dto)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền cập nhật giá cho sự kiện này."));
            }

            var pricingShowtime = await _context.Showtimes
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == showtimeId && s.EventId == eventId);

            if (pricingShowtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại trong sự kiện này."));
            }

            if (dto.Categories == null || dto.Categories.Count == 0)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Danh sách hạng ghế cần cập nhật không được để trống."));
            }

            if (dto.Categories.Any(item => item == null || item.Price < 0))
            {
                return BadRequest(ApiResponse<object>.FailureResult("Giá hạng ghế không được là số âm."));
            }

            if (pricingShowtime.Status == ShowtimeStatus.OnSale && dto.Categories.Any(item => item.Price == null))
                return BadRequest(ApiResponse<object>.FailureResult("Không thể xóa giá ghế của suất diễn đang mở bán."));

            var requestedCategoryIds = dto.Categories
                .Select(item => item.SeatCategoryId)
                .ToList();

            if (requestedCategoryIds.Any(id => id == Guid.Empty) ||
                requestedCategoryIds.Distinct().Count() != requestedCategoryIds.Count)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Danh sách hạng ghế không hợp lệ hoặc bị trùng."));
            }

            var categories = await _context.SeatCategories
                .Where(category => requestedCategoryIds.Contains(category.Id))
                .ToListAsync();

            if (categories.Count != requestedCategoryIds.Count ||
                categories.Any(category => category.ShowtimeId != showtimeId))
            {
                return BadRequest(ApiResponse<object>.FailureResult("Có hạng ghế không thuộc suất diễn được chọn."));
            }

            var requestedPrices = dto.Categories
                .ToDictionary(item => item.SeatCategoryId, item => item.Price);

            foreach (var category in categories)
            {
                category.Price = requestedPrices[category.Id];
            }

            await _context.SaveChangesAsync();

            var response = categories
                .OrderBy(category => category.Name)
                .Select(category => new SeatCategoryPriceDto
                {
                    Id = category.Id,
                    Name = category.Name,
                    Price = category.Price
                })
                .ToList();

            return Ok(ApiResponse<List<SeatCategoryPriceDto>>.SuccessResult(response, "Cập nhật giá hạng ghế thành công."));
        }

        /// <summary>
        /// Mở bán suất diễn (Task T-16).
        /// POST /api/events/{eventId}/showtimes/{showtimeId}/open-sale
        /// </summary>
        [HttpPost("{eventId:guid}/showtimes/{showtimeId:guid}/open-sale")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<ShowtimeResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> OpenSale(Guid eventId, Guid showtimeId)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền thao tác trên sự kiện này."));
            }

            var showtime = ev.Showtimes.FirstOrDefault(s => s.Id == showtimeId);
            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại."));
            }

            await _context.Entry(showtime)
                .Collection(s => s.SeatCategories)
                .LoadAsync();

            try
            {
                if (showtime.AvailableSeats <= 0)
                    return BadRequest(ApiResponse<object>.FailureResult("Không thể chuyển sang trạng thái Đang bán vì suất diễn chưa có ghế."));
                if (showtime.SeatCategories.Count == 0)
                    return BadRequest(ApiResponse<object>.FailureResult("Suất diễn chưa có hạng ghế để mở bán."));
                if (showtime.SeatCategories.All(c => c.Price.HasValue && c.Price.Value >= 0) &&
                    !await _context.Seats.AnyAsync(s => s.ShowtimeId == showtimeId))
                    return BadRequest(ApiResponse<object>.FailureResult("Suất diễn chưa có sơ đồ ghế để mở bán."));
                showtime.ChangeStatus(ShowtimeStatus.OnSale);
                await _context.SaveChangesAsync();

                var responseDto = new ShowtimeResponseDto
                {
                    Id = showtime.Id,
                    EventId = showtime.EventId,
                    StartTime = showtime.StartTime,
                    EndTime = showtime.EndTime,
                    AvailableSeats = showtime.AvailableSeats,
                        RemainingTickets = showtime.AvailableSeats,
                        AvailableTickets = showtime.AvailableSeats,
                        Status = showtime.Status,
                        StatusActionMessage = GetShowtimeStatusActionMessage(showtime)
                    };
                    return Ok(ApiResponse<ShowtimeResponseDto>.SuccessResult(responseDto, "Mở bán suất diễn thành công."));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.FailureResult(ex.Message));
            }
        }

        /// <summary>
        /// Đóng bán suất diễn (Task T-16).
        /// POST /api/events/{eventId}/showtimes/{showtimeId}/close-sale
        /// </summary>
        [HttpPost("{eventId:guid}/showtimes/{showtimeId:guid}/close-sale")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<ShowtimeResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CloseSale(Guid eventId, Guid showtimeId)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền thao tác trên sự kiện này."));
            }

            var showtime = ev.Showtimes.FirstOrDefault(s => s.Id == showtimeId);
            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại."));
            }

            try
            {
                showtime.ChangeStatus(ShowtimeStatus.Closed);
                await _context.SaveChangesAsync();

                var responseDto = new ShowtimeResponseDto
                {
                    Id = showtime.Id,
                    EventId = showtime.EventId,
                    StartTime = showtime.StartTime,
                    EndTime = showtime.EndTime,
                    AvailableSeats = showtime.AvailableSeats,
                    RemainingTickets = showtime.AvailableSeats,
                    AvailableTickets = showtime.AvailableSeats,
                    Status = showtime.Status,
                    StatusActionMessage = GetShowtimeStatusActionMessage(showtime)
                };
                return Ok(ApiResponse<ShowtimeResponseDto>.SuccessResult(responseDto, "Đóng bán suất diễn thành công."));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.FailureResult(ex.Message));
            }
        }

        #region Helper Methods

        private static string? GetShowtimeStatusActionMessage(Showtime showtime)
        {
            if (showtime.Status == ShowtimeStatus.Draft)
            {
                if (showtime.AvailableSeats <= 0)
                {
                    return "Chưa có ghế để mở bán.";
                }
                return "Suất diễn đủ điều kiện để mở bán.";
            }

            if (showtime.Status == ShowtimeStatus.OnSale)
            {
                return "Suất diễn đang mở bán.";
            }

            if (showtime.Status == ShowtimeStatus.Closed)
            {
                return "Suất diễn đã đóng bán.";
            }

            return null;
        }

        private Guid? GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? User.FindFirst("id")?.Value
                              ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
            {
                return userId;
            }
            return null;
        }

        private async Task<bool> IsCurrentUserAdminAsync(Guid userId)
        {
            if (User.IsInRole("Admin") ||
                User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") &&
                                   string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return await _context.UserRoles
                .AsNoTracking()
                .AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin");
        }

        private static EventResponseDto MapToEventResponseDto(Event ev)
        {
            return new EventResponseDto
            {
                Id = ev.Id,
                OwnerId = ev.OwnerId,
                OwnerName = ev.Owner != null ? (!string.IsNullOrWhiteSpace(ev.Owner.FullName) ? ev.Owner.FullName : ev.Owner.Username) : null,
                OwnerEmail = ev.Owner?.Email,
                Title = ev.Title,
                Description = ev.Description,
                ImageUrl = ev.ImageUrl,
                Location = ev.Location,
                StartTime = ev.StartTime,
                EndTime = ev.EndTime,
                TotalSeats = ev.TotalSeats,
                CreatedAt = ev.CreatedAt,
                UpdatedAt = ev.UpdatedAt,
                Showtimes = ev.Showtimes?.Select(s => new ShowtimeResponseDto
                {
                    Id = s.Id,
                    EventId = s.EventId,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    AvailableSeats = s.AvailableSeats,
                    Status = s.Status,
                    StatusActionMessage = GetShowtimeStatusActionMessage(s)
                }).ToList() ?? new List<ShowtimeResponseDto>()
            };
        }

        private object GetModelStateErrors()
        {
            return ModelState
                .Where(x => x.Value?.Errors.Count > 0)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.Errors.Select(e => e.ErrorMessage).ToArray()
                );
        }

        #endregion
    }
}
