using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using EventTicketBooking.Api.Services;

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
        private readonly IOrderAuditService _orderAuditService;
        private readonly IHubContext<SeatStatusHub>? _hubContext;
        private readonly IRefundService _refundService;

        public EventController(
            AppDbContext context,
            IOrderAuditService? orderAuditService = null,
            IHubContext<SeatStatusHub>? hubContext = null,
            IRefundService? refundService = null,
            IPaymentGateway? paymentGateway = null)
        {
            _context = context;
            _orderAuditService = orderAuditService ?? new OrderAuditService(context, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderAuditService>.Instance);
            _hubContext = hubContext;
            _refundService = refundService ?? new RefundService(context, _orderAuditService, paymentGateway);
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
            var showtimeIds = events.SelectMany(e => e.Showtimes).Select(s => s.Id).ToList();
            var capacities = await _context.Seats.AsNoTracking().Where(s => showtimeIds.Contains(s.ShowtimeId))
                .GroupBy(s => s.ShowtimeId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count);
            var sales = await _context.OrderItems.AsNoTracking().Where(i => showtimeIds.Contains(i.Order.ShowtimeId) && i.Order.Status == OrderStatus.Paid)
                .GroupBy(i => i.Order.ShowtimeId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count);
            foreach (var show in result.SelectMany(e => e.Showtimes))
            {
                show.ActualSeatCount = capacities.GetValueOrDefault(show.Id);
                show.SoldSeatCount = sales.GetValueOrDefault(show.Id);
            }
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

            var maxTickets = dto.MaxTicketsPerUser.HasValue && dto.MaxTicketsPerUser.Value > 0
                ? dto.MaxTicketsPerUser.Value
                : 10;

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
                MaxTicketsPerUser = maxTickets,
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
                AvailableSeats = newEvent.TotalSeats,
                MaxTicketsPerUser = newEvent.MaxTicketsPerUser
            };

            newEvent.Showtimes.Add(defaultShowtime);

            _context.Events.Add(newEvent);
            await _context.SaveChangesAsync();

            var responseDto = MapToEventResponseDto(newEvent);
            return StatusCode(StatusCodes.Status201Created, ApiResponse<EventResponseDto>.SuccessResult(responseDto, "Tạo sự kiện thành công."));
        }

        /// <summary>
        /// S-58: Nhân bản một suất diễn trong cùng sự kiện, giữ sơ đồ và giá, tạo tất cả ghế trống.
        /// </summary>
        [HttpPost("{eventId:guid}/showtimes/{showtimeId:guid}/duplicate")]
        public async Task<IActionResult> DuplicateShowtime(Guid eventId, Guid showtimeId, [FromBody] DuplicateShowtimeDto dto)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue) return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));
            var source = await _context.Showtimes.AsNoTracking().Include(s => s.Event).Include(s => s.SeatCategories)
                .SingleOrDefaultAsync(s => s.Id == showtimeId && s.EventId == eventId);
            if (source == null) return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại trong sự kiện này."));
            if (source.Event.OwnerId != userId.Value && !await IsCurrentUserAdminAsync(userId.Value))
                return StatusCode(403, ApiResponse<object>.FailureResult("Bạn không có quyền nhân bản suất diễn này."));
            if (dto?.StartTime == null || dto.EndTime == null || dto.StartTime == default(DateTime) || dto.EndTime <= dto.StartTime)
                return BadRequest(ApiResponse<object>.FailureResult("Chọn thời gian bắt đầu và kết thúc hợp lệ; kết thúc phải sau bắt đầu."));
            var startTime = dto.StartTime.Value.ToUniversalTime();
            var endTime = dto.EndTime.Value.ToUniversalTime();
            if (startTime == source.StartTime && endTime == source.EndTime)
                return BadRequest(ApiResponse<object>.FailureResult("Chọn lịch mới khác lịch của suất diễn gốc."));
            if (dto.SalesEndTime.HasValue && dto.SalesEndTime.Value.ToUniversalTime() > endTime)
                return BadRequest(ApiResponse<object>.FailureResult("Hạn bán vé không được vượt giờ kết thúc suất diễn."));
            var seats = await _context.Seats.AsNoTracking().Where(s => s.ShowtimeId == showtimeId).ToListAsync();
            var copy = new Showtime
            {
                Name = string.IsNullOrWhiteSpace(dto.Name) ? source.Name : dto.Name.Trim(),
                EventId = eventId,
                SalesEndTime = dto.SalesEndTime?.ToUniversalTime() ?? startTime,
                StartTime = startTime,
                EndTime = endTime,
                AvailableSeats = seats.Count,
                MaxTicketsPerUser = source.MaxTicketsPerUser,
                SeatingMapImageUrl = source.SeatingMapImageUrl
            };
            var categories = source.SeatCategories.ToDictionary(c => c.Id, c => new SeatCategory
            {
                ShowtimeId = copy.Id,
                Name = c.Name,
                Price = c.Price
            });
            copy.SeatCategories = categories.Values.ToList();
            _context.Showtimes.Add(copy);
            _context.Seats.AddRange(seats.Select(s => new Seat
            {
                ShowtimeId = copy.Id,
                SeatCategoryId = categories[s.SeatCategoryId].Id,
                Row = s.Row,
                SeatNumber = s.SeatNumber,
                Status = "AVAILABLE"
            }));
            // One SaveChanges transaction persists the entire copy; no orders, holds or scans are copied.
            await _context.SaveChangesAsync();
            var quantities = seats.GroupBy(s => s.SeatCategoryId)
                .ToDictionary(g => categories[g.Key].Id, g => g.Count());
            var response = new ShowtimeResponseDto
            {
                Name = copy.Name,
                SalesEndTime = copy.SalesEndTime,
                Id = copy.Id,
                EventId = eventId,
                StartTime = copy.StartTime,
                EndTime = copy.EndTime,
                AvailableSeats = seats.Count,
                ActualSeatCount = seats.Count,
                RemainingTickets = seats.Count,
                MaxTicketsPerUser = copy.MaxTicketsPerUser,
                SeatingMapImageUrl = copy.SeatingMapImageUrl,
                Status = copy.Status,
                SeatCategories = copy.SeatCategories.Select(c => new SeatCategoryPriceDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Price = c.Price,
                    AvailableQuantity = quantities.GetValueOrDefault(c.Id)
                }).ToList()
            };
            return StatusCode(201, ApiResponse<ShowtimeResponseDto>.SuccessResult(response,
                "Đã nhân bản suất diễn ở trạng thái Nháp với sơ đồ và bảng giá, tất cả ghế đều trống."));
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
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Event", id.ToString());
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
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Event", id.ToString());
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
            if (dto.MaxTicketsPerUser.HasValue && dto.MaxTicketsPerUser.Value > 0)
            {
                ev.MaxTicketsPerUser = dto.MaxTicketsPerUser.Value;
                foreach (var s in ev.Showtimes)
                {
                    s.MaxTicketsPerUser = ev.MaxTicketsPerUser;
                }
            }
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
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Event", id.ToString());
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
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Event", id.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền truy cập suất diễn của sự kiện này."));
            }

            var ids = ev.Showtimes.Select(s => s.Id).ToList();
            var seatCounts = await _context.Seats.AsNoTracking().Where(s => ids.Contains(s.ShowtimeId))
                .GroupBy(s => s.ShowtimeId).Select(g => new { Id = g.Key, Total = g.Count(), Sold = g.Count(s => s.Status == "SOLD") })
                .ToDictionaryAsync(g => g.Id);
            var showtimesDto = ev.Showtimes.Select(s => new ShowtimeResponseDto
            {
                Name = s.Name,
                SalesEndTime = s.SalesEndTime,
                Id = s.Id,
                SeatingMapImageUrl = s.SeatingMapImageUrl,
                EventId = s.EventId,
                StartTime = s.StartTime,
                EndTime = s.EndTime,
                AvailableSeats = s.AvailableSeats,
                MaxTicketsPerUser = s.MaxTicketsPerUser,
                ActualSeatCount = seatCounts.TryGetValue(s.Id, out var counts) ? counts.Total : 0,
                SoldSeatCount = seatCounts.TryGetValue(s.Id, out var soldCounts) ? soldCounts.Sold : 0,
                Status = s.Status,
                SeatCategories = s.SeatCategories
                    .OrderBy(category => category.Name)
                    .Select(category => new SeatCategoryPriceDto
                    {
                        Id = category.Id,
                        Name = category.Name,
                        Price = category.Price
                    })
                    .ToList(),
                StatusActionMessage = s.Status != ShowtimeStatus.Draft ? GetShowtimeStatusActionMessage(s) :
                    !seatCounts.ContainsKey(s.Id) ? "Tải sơ đồ ghế trước khi mở bán." :
                    s.SeatCategories.Count == 0 || s.SeatCategories.Any(c => c.Price is null or < 0) ? "Thiết lập giá cho tất cả hạng ghế trước khi mở bán." :
                    GetShowtimeStatusActionMessage(s)
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
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Event", id.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền thao tác trên sự kiện này."));
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Dữ liệu không hợp lệ.", GetModelStateErrors()));
            }

            if (dto.SalesEndTime.HasValue && dto.SalesEndTime.Value.ToUniversalTime() > dto.EndTime.ToUniversalTime())
                return BadRequest(ApiResponse<object>.FailureResult("Hạn bán vé không được vượt giờ kết thúc suất diễn."));
            if (dto.EndTime <= dto.StartTime)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thời gian kết thúc phải lớn hơn thời gian bắt đầu."));
            }

            var startTimeUtc = dto.StartTime.Kind == DateTimeKind.Utc ? dto.StartTime : dto.StartTime.ToUniversalTime();
            var endTimeUtc = dto.EndTime.Kind == DateTimeKind.Utc ? dto.EndTime : dto.EndTime.ToUniversalTime();

            var showtime = new Showtime
            {
                Name = string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim(),
                SalesEndTime = dto.SalesEndTime?.ToUniversalTime() ?? startTimeUtc,
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = startTimeUtc,
                EndTime = endTimeUtc,
                AvailableSeats = dto.AvailableSeats > 0 ? dto.AvailableSeats : ev.TotalSeats,
                MaxTicketsPerUser = dto.MaxTicketsPerUser.HasValue && dto.MaxTicketsPerUser.Value > 0 ? dto.MaxTicketsPerUser.Value : ev.MaxTicketsPerUser
            };

            _context.Showtimes.Add(showtime);
            await _context.SaveChangesAsync();

            var responseDto = new ShowtimeResponseDto
            {
                Name = showtime.Name,
                SalesEndTime = showtime.SalesEndTime,
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
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Event", eventId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền thao tác trên sự kiện này."));
            }

            var showtime = ev.Showtimes.FirstOrDefault(s => s.Id == showtimeId);
            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại."));
            }

            if (dto.SalesEndTime.HasValue && dto.SalesEndTime.Value.ToUniversalTime() > dto.EndTime.ToUniversalTime())
                return BadRequest(ApiResponse<object>.FailureResult("Hạn bán vé không được vượt giờ kết thúc suất diễn."));
            if (dto.EndTime <= dto.StartTime)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thời gian kết thúc phải lớn hơn thời gian bắt đầu."));
            }

            var startTimeUtc = dto.StartTime.Kind == DateTimeKind.Utc ? dto.StartTime : dto.StartTime.ToUniversalTime();
            var endTimeUtc = dto.EndTime.Kind == DateTimeKind.Utc ? dto.EndTime : dto.EndTime.ToUniversalTime();

            if (dto.Name != null) showtime.Name = string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim();
            showtime.SalesEndTime = dto.SalesEndTime?.ToUniversalTime() ?? startTimeUtc;
            showtime.StartTime = startTimeUtc;
            showtime.EndTime = endTimeUtc;
            if (dto.AvailableSeats.HasValue && dto.AvailableSeats.Value > 0)
            {
                showtime.AvailableSeats = dto.AvailableSeats.Value;
            }
            if (dto.MaxTicketsPerUser.HasValue && dto.MaxTicketsPerUser.Value > 0)
            {
                showtime.MaxTicketsPerUser = dto.MaxTicketsPerUser.Value;
            }

            await _context.SaveChangesAsync();

            var responseDto = new ShowtimeResponseDto
            {
                Name = showtime.Name,
                SalesEndTime = showtime.SalesEndTime,
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
        /// S-36: Báo cáo các lượt quét xung đột của suất diễn, giới hạn theo chủ sự kiện.
        /// </summary>
        [HttpPut("{eventId:guid}/showtimes/{showtimeId:guid}/seating-map-image")]
        public async Task<IActionResult> UpdateSeatingMapImage(Guid eventId, Guid showtimeId, [FromBody] SeatingMapImageDto dto)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue) return Unauthorized();
            var showtime = await _context.Showtimes.Include(s => s.Event)
                .SingleOrDefaultAsync(s => s.Id == showtimeId && s.EventId == eventId);
            if (showtime == null) return NotFound();
            if (showtime.Event.OwnerId != userId.Value && !await IsCurrentUserAdminAsync(userId.Value))
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Showtime", showtimeId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden);
            }
            var url = dto.ImageUrl?.Trim();
            if (!string.IsNullOrEmpty(url) && (url.Length > 2048 ||
                !(url.StartsWith("/uploads/events/", StringComparison.Ordinal) && !url.Contains('\\') && !url.Contains("..") && !url.Contains('?') && !url.Contains('#') ||
                  Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))))
                return BadRequest(ApiResponse<object>.FailureResult("Dùng đường link ảnh http/https hoặc ảnh đã tải lên, tối đa 2048 ký tự."));
            showtime.SeatingMapImageUrl = string.IsNullOrEmpty(url) ? null : url;
            await _context.SaveChangesAsync();
            return Ok(ApiResponse<object>.SuccessResult(new { showtime.Id, showtime.SeatingMapImageUrl }, "Đã lưu ảnh sơ đồ khán phòng."));
        }

        [HttpGet("showtimes/{showtimeId:guid}/checkin-conflicts")]
        public async Task<IActionResult> GetCheckInConflicts(Guid showtimeId, int page = 1, int pageSize = 20)
        {
            Response.Headers["Cache-Control"] = "no-store";
            var userId = GetCurrentUserId();
            if (!userId.HasValue) return Unauthorized();
            if (page < 1 || pageSize < 1 || pageSize > 100 || page > 1000000) return BadRequest();
            var showtime = await _context.Showtimes.AsNoTracking().Include(s => s.Event)
                .SingleOrDefaultAsync(s => s.Id == showtimeId);
            if (showtime == null) return NotFound();
            if (showtime.Event.OwnerId != userId.Value && !await IsCurrentUserAdminAsync(userId.Value))
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Showtime", showtimeId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden);
            }
            var query = _context.OfflineCheckInRecords.AsNoTracking()
                .Where(r => r.ShowtimeId == showtimeId && r.IsConflict);
            var totalConflicts = await query.CountAsync();
            var totalTickets = await query.Select(r => r.OrderItemId).Distinct().CountAsync();
            var items = await query.OrderBy(r => r.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => new
                {
                    r.TicketCode,
                    ConflictGate = r.GateName,
                    ConflictTime = r.ScannedAtDevice,
                    ValidGate = r.ExistingCheckInGate,
                    ValidTime = r.ExistingCheckInTime
                }).ToListAsync();
            return Ok(ApiResponse<object>.SuccessResult(new { showtimeId, page, pageSize, totalConflicts, totalTickets, items }));
        }

        /// <summary>
        /// Lấy số vé bán, ghế đang giữ, ghế còn, vé xung đột và doanh thu theo hạng cho từng suất diễn.
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
            var conflictCounts = await _context.OfflineCheckInRecords.AsNoTracking()
                .Where(r => showtimeIds.Contains(r.ShowtimeId) && r.IsConflict)
                .GroupBy(r => r.ShowtimeId)
                .Select(g => new { ShowtimeId = g.Key, Count = g.Select(r => r.OrderItemId).Distinct().Count() })
                .ToDictionaryAsync(g => g.ShowtimeId, g => g.Count);
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
                            HeldCount = categoryHeldCount,
                            AvailableQuantity = categoryAvailable,
                            RemainingQuantity = categoryAvailable,
                            Revenue = sale?.Revenue ?? 0
                        };
                    })
                    .ToList();
                var soldCount = categories.Sum(category => category.TicketSoldCount);
                var heldCount = heldCounts.GetValueOrDefault(showtime.Id, 0);
                var totalSeats = seatCounts.GetValueOrDefault(showtime.Id, 0);
                var availableSeats = Math.Max(0, totalSeats - soldCount - heldCount);

                return new ShowtimeSalesDto
                {
                    ConflictTicketCount = conflictCounts.GetValueOrDefault(showtime.Id),
                    ShowtimeId = showtime.Id,
                    EventTitle = showtime.Event.Title,
                    StartTime = showtime.StartTime,
                    EndTime = showtime.EndTime,
                    TotalSeats = totalSeats,
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
                if (showtime.Status == ShowtimeStatus.Cancelled)
                    return BadRequest(ApiResponse<object>.FailureResult("Không thể mở bán suất diễn đã bị huỷ."));
                if (showtime.AvailableSeats <= 0)
                    return BadRequest(ApiResponse<object>.FailureResult("Không thể chuyển sang trạng thái Đang bán vì suất diễn chưa có ghế."));
                if (showtime.SeatCategories.Count == 0)
                    return BadRequest(ApiResponse<object>.FailureResult("Suất diễn chưa có hạng ghế để mở bán."));
                if (showtime.SeatCategories.All(c => c.Price.HasValue && c.Price.Value >= 0) &&
                    !await _context.Seats.AnyAsync(s => s.ShowtimeId == showtimeId))
                    return BadRequest(ApiResponse<object>.FailureResult("Suất diễn chưa có sơ đồ ghế để mở bán."));
                if (showtime.IsSaleExpired(DateTime.UtcNow))
                    return BadRequest(ApiResponse<object>.FailureResult("Suất diễn đã hết thời gian bán vé."));
                showtime.ChangeStatus(ShowtimeStatus.OnSale);
                await _context.SaveChangesAsync();

                var responseDto = new ShowtimeResponseDto
                {
                    Name = showtime.Name,
                    SalesEndTime = showtime.SalesEndTime,
                    Id = showtime.Id,
                    EventId = showtime.EventId,
                    StartTime = showtime.StartTime,
                    EndTime = showtime.EndTime,
                    AvailableSeats = showtime.AvailableSeats,
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
                    Name = showtime.Name,
                    SalesEndTime = showtime.SalesEndTime,
                    Id = showtime.Id,
                    EventId = showtime.EventId,
                    StartTime = showtime.StartTime,
                    EndTime = showtime.EndTime,
                    AvailableSeats = showtime.AvailableSeats,
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

        /// <summary>
        /// Story S-47: Huỷ suất diễn, đóng bán và xử lý trạng thái vé (Task 1).
        /// POST /api/events/{eventId}/showtimes/{showtimeId}/cancel
        /// </summary>
        [HttpPost("{eventId:guid}/showtimes/{showtimeId:guid}/cancel")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<ShowtimeResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CancelShowtime(Guid eventId, Guid showtimeId)
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
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại trong sự kiện này."));
            }

            if (showtime.Status == ShowtimeStatus.Cancelled)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Suất diễn đã ở trạng thái huỷ."));
            }

            if (showtime.Status == ShowtimeStatus.Draft)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Không thể huỷ suất diễn đang ở trạng thái Nháp (Draft)."));
            }

            if (showtime.Status != ShowtimeStatus.OnSale && showtime.Status != ShowtimeStatus.Closed)
            {
                return BadRequest(ApiResponse<object>.FailureResult($"Không thể huỷ suất diễn ở trạng thái {showtime.Status}."));
            }

            var utcNow = DateTime.UtcNow;
            var endTimeUtc = showtime.EndTime.Kind == DateTimeKind.Utc ? showtime.EndTime : showtime.EndTime.ToUniversalTime();
            if (endTimeUtc < utcNow)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Không thể huỷ suất diễn đã kết thúc trong quá khứ."));
            }

            IDbContextTransaction? transaction = null;
            if (_context.Database.IsRelational())
            {
                transaction = await _context.Database.BeginTransactionAsync();
            }
            else
            {
                try
                {
                    transaction = await _context.Database.BeginTransactionAsync();
                }
                catch (InvalidOperationException)
                {
                    // InMemory DB provider does not support real transactions
                }
            }

            try
            {
                var now = DateTimeOffset.UtcNow;

                // 1. Khoá dòng suất diễn và các đơn hàng liên quan nếu trên PostgreSQL
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT 1 FROM \"Showtimes\" WHERE \"Id\" = {0} FOR UPDATE", showtimeId);

                    var currentStatus = await _context.Showtimes
                        .AsNoTracking()
                        .Where(s => s.Id == showtimeId)
                        .Select(s => s.Status)
                        .FirstOrDefaultAsync();

                    if (currentStatus == ShowtimeStatus.Cancelled)
                    {
                        if (transaction != null) await transaction.RollbackAsync();
                        return BadRequest(ApiResponse<object>.FailureResult("Suất diễn đã ở trạng thái huỷ."));
                    }
                }

                List<Order> relatedOrders;
                if (_context.Database.IsNpgsql())
                {
                    relatedOrders = await _context.Orders
                        .FromSqlRaw("SELECT * FROM orders WHERE \"ShowtimeId\" = {0} FOR UPDATE", showtimeId)
                        .Include(o => o.OrderItems)
                        .ToListAsync();
                }
                else
                {
                    relatedOrders = await _context.Orders
                        .Where(o => o.ShowtimeId == showtimeId)
                        .Include(o => o.OrderItems)
                        .ToListAsync();
                }

                // 2. Chuyển trạng thái suất diễn thành Cancelled
                showtime.ChangeStatus(ShowtimeStatus.Cancelled);

                var userDisplayName = User.FindFirstValue(ClaimTypes.Name)
                    ?? User.FindFirst("name")?.Value
                    ?? (isAdmin ? "Admin" : "Organizer");
                var actorType = isAdmin ? "ADMIN" : "ORGANIZER";

                var pendingSeatIdsToRelease = new List<Guid>();
                var paidOrderIdsToRefund = new List<Guid>();

                // 3. Xử lý các đơn hàng Pending và Paid
                foreach (var order in relatedOrders)
                {
                    var oldStatus = order.Status.ToString();

                    if (order.Status == OrderStatus.Pending)
                    {
                        order.Status = OrderStatus.Cancelled;
                        order.CancelReason = OrderCancelReasons.ShowtimeCancelled;
                        order.UpdatedAt = now;

                        if (order.OrderItems != null && order.OrderItems.Any())
                        {
                            pendingSeatIdsToRelease.AddRange(order.OrderItems.Select(oi => oi.SeatId));
                        }

                        _orderAuditService.Record(
                            order.Id,
                            "ORDER",
                            order.Id.ToString(),
                            "SHOWTIME_CANCELLED",
                            oldStatus,
                            OrderStatus.Cancelled.ToString(),
                            actorType,
                            userDisplayName,
                            currentUserId.Value,
                            "SHOWTIME_CANCELLED_AWAITING_REFUND",
                            now);
                    }
                    else if (order.Status == OrderStatus.Paid)
                    {
                        order.Status = OrderStatus.Cancelled;
                        order.CancelReason = OrderCancelReasons.ShowtimeCancelled;
                        order.UpdatedAt = now;
                        paidOrderIdsToRefund.Add(order.Id);

                        _orderAuditService.Record(
                            order.Id,
                            "ORDER",
                            order.Id.ToString(),
                            "SHOWTIME_CANCELLED",
                            oldStatus,
                            OrderStatus.Cancelled.ToString(),
                            actorType,
                            userDisplayName,
                            currentUserId.Value,
                            "SHOWTIME_CANCELLED_AWAITING_REFUND",
                            now);
                    }
                }

                // 4. Giải phóng ghế và giữ chỗ của các đơn Pending
                pendingSeatIdsToRelease = pendingSeatIdsToRelease.Distinct().ToList();
                if (pendingSeatIdsToRelease.Any())
                {
                    var activeHolds = await _context.SeatHold
                        .Where(sh => pendingSeatIdsToRelease.Contains(sh.SeatId) && sh.Status == "ACTIVE")
                        .ToListAsync();

                    foreach (var hold in activeHolds)
                    {
                        hold.Status = "EXPIRED";
                    }

                    var heldSeats = await _context.Seats
                        .Where(s => pendingSeatIdsToRelease.Contains(s.Id) && s.Status == "HELD")
                        .ToListAsync();

                    foreach (var seat in heldSeats)
                    {
                        seat.Status = "AVAILABLE";
                    }
                }

                await _context.SaveChangesAsync();

                if (transaction != null)
                {
                    await transaction.CommitAsync();
                }

                // 5. Task 2 (Story S-47): Xử lý hoàn tiền cho các đơn hàng đã thanh toán (100% giá trị đơn, gọi ngoài transaction DB)
                if (paidOrderIdsToRefund.Any())
                {
                    try
                    {
                        await _refundService.ProcessShowtimeRefundsAsync(
                            showtimeId,
                            paidOrderIdsToRefund,
                            actorType,
                            userDisplayName,
                            currentUserId.Value);
                    }
                    catch
                    {
                        // Lỗi refund của background không làm hỏng kết quả trả về huỷ suất diễn đã commit
                    }
                }

                // Bắn thông báo SignalR nếu có client đang theo dõi
                if (_hubContext != null && pendingSeatIdsToRelease.Any())
                {
                    try
                    {
                        await _hubContext.Clients.Group(showtimeId.ToString())
                            .SendAsync("SeatReleased", new { ShowtimeId = showtimeId, SeatIds = pendingSeatIdsToRelease });
                    }
                    catch
                    {
                        // SignalR fail không làm hỏng kết quả trả về
                    }
                }

                var responseDto = new ShowtimeResponseDto
                {
                    Name = showtime.Name,
                    SalesEndTime = showtime.SalesEndTime,
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

                return Ok(ApiResponse<ShowtimeResponseDto>.SuccessResult(responseDto, "Huỷ suất diễn thành công."));
            }
            catch (Exception)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync();
                }
                throw;
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
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

            if (showtime.Status == ShowtimeStatus.Cancelled)
            {
                return "Suất diễn đã bị huỷ.";
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
                MaxTicketsPerUser = ev.MaxTicketsPerUser,
                CreatedAt = ev.CreatedAt,
                UpdatedAt = ev.UpdatedAt,
                Showtimes = ev.Showtimes?.Select(s => new ShowtimeResponseDto
                {
                    Name = s.Name,
                    SalesEndTime = s.SalesEndTime,
                    Id = s.Id,
                    SeatingMapImageUrl = s.SeatingMapImageUrl,
                    EventId = s.EventId,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    AvailableSeats = s.AvailableSeats,
                    MaxTicketsPerUser = s.MaxTicketsPerUser,
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

        /// <summary>
        /// Báo cáo tỉ lệ bỏ giỏ và thời gian giữ chỗ thực tế (S-63).
        /// GET /api/events/{eventId}/showtimes/{showtimeId}/hold-stats
        /// </summary>
        [HttpGet("{eventId:guid}/showtimes/{showtimeId:guid}/hold-stats")]
        [RequireRole("Organizer", "Admin")]
        [ProducesResponseType(typeof(ApiResponse<SeatHoldReportDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetHoldStats(Guid eventId, Guid showtimeId)
        {
            var currentUserId = GetCurrentUserId();
            if (!currentUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var ev = await _context.Events
                .AsNoTracking()
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            var isAdmin = await IsCurrentUserAdminAsync(currentUserId.Value);
            if (!isAdmin && ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền xem báo cáo của sự kiện này."));
            }

            var showtime = ev.Showtimes.FirstOrDefault(s => s.Id == showtimeId);
            if (showtime == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Suất diễn không tồn tại."));
            }

            var holdStatsQuery = await _context.SeatHold
                .AsNoTracking()
                .Where(h => h.Seat.ShowtimeId == showtimeId)
                .GroupBy(h => h.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            int activeCount = holdStatsQuery.FirstOrDefault(x => x.Status == "ACTIVE")?.Count ?? 0;
            int convertedCount = holdStatsQuery.FirstOrDefault(x => x.Status == "CONVERTED")?.Count ?? 0;
            int expiredCount = holdStatsQuery.FirstOrDefault(x => x.Status == "EXPIRED")?.Count ?? 0;
            int releasedCount = holdStatsQuery.FirstOrDefault(x => x.Status == "RELEASED")?.Count ?? 0;

            int totalHoldCount = activeCount + convertedCount + expiredCount + releasedCount;
            int unsuccessfulHoldCount = expiredCount + releasedCount;
            int resolvedHoldCount = convertedCount + expiredCount + releasedCount;

            double conversionRate = resolvedHoldCount > 0 ? Math.Round((double)convertedCount / resolvedHoldCount * 100, 2) : 0;

            var convertedHoldsData = await (from h in _context.SeatHold
                                            join oi in _context.OrderItems on h.SeatId equals oi.SeatId
                                            join o in _context.Orders on oi.OrderId equals o.Id
                                            join pt in _context.PaymentTransactions on o.Id equals pt.OrderId
                                            where h.Seat.ShowtimeId == showtimeId
                                                  && h.Status == "CONVERTED"
                                                  && h.UserId == o.UserId
                                                  && o.Status == OrderStatus.Paid
                                                  && pt.Status == "PAID"
                                                  && pt.UpdatedAt.UtcDateTime >= h.HeldAt
                                            select new
                                            {
                                                HoldId = h.Id,
                                                HeldAt = h.HeldAt,
                                                PaidAt = pt.UpdatedAt.UtcDateTime
                                            }).AsNoTracking().ToListAsync();

            var durations = convertedHoldsData
                .GroupBy(x => x.HoldId)
                .Select(g =>
                {
                    var earliestPaidAt = g.Min(x => x.PaidAt);
                    return (earliestPaidAt - g.First().HeldAt).TotalSeconds;
                })
                .Where(seconds => seconds >= 0)
                .OrderBy(s => s)
                .ToList();

            double? avgDuration = null;
            double? p90Duration = null;

            if (durations.Count > 0)
            {
                avgDuration = Math.Round(durations.Average(), 2);

                int p90Index = (int)Math.Ceiling(0.90 * durations.Count) - 1;
                p90Index = Math.Max(0, Math.Min(durations.Count - 1, p90Index));
                p90Duration = Math.Round(durations[p90Index], 2);
            }

            var report = new SeatHoldReportDto
            {
                ShowtimeId = showtimeId,
                EventTitle = ev.Title,
                StartTime = showtime.StartTime,
                TotalHoldCount = totalHoldCount,
                ConvertedHoldCount = convertedCount,
                ExpiredHoldCount = expiredCount,
                ReleasedHoldCount = releasedCount,
                UnsuccessfulHoldCount = unsuccessfulHoldCount,
                ConversionRate = conversionRate,
                AveragePaymentDurationSeconds = avgDuration,
                P90PaymentDurationSeconds = p90Duration
            };

            return Ok(ApiResponse<SeatHoldReportDto>.SuccessResult(report, "Lấy báo cáo giữ chỗ thành công."));
        }

        #endregion
    }
}
