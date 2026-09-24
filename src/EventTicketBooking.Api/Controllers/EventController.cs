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
    [RequireRole]
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

            var events = await _context.Events
                .AsNoTracking()
                .Include(e => e.Showtimes)
                .Where(e => e.OwnerId == currentUserId.Value)
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

            var newEvent = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = currentUserId.Value,
                Title = dto.Title.Trim(),
                Description = dto.Description?.Trim(),
                Location = dto.Location.Trim(),
                StartTime = dto.StartTime,
                EndTime = dto.EndTime,
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
                .Include(e => e.Showtimes)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            if (ev.OwnerId != currentUserId.Value)
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

            if (ev.OwnerId != currentUserId.Value)
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
            ev.Title = dto.Title.Trim();
            ev.Description = dto.Description?.Trim();
            ev.Location = dto.Location.Trim();
            ev.StartTime = dto.StartTime;
            ev.EndTime = dto.EndTime;
            ev.TotalSeats = dto.TotalSeats;
            ev.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var responseDto = MapToEventResponseDto(ev);
            return Ok(ApiResponse<EventResponseDto>.SuccessResult(responseDto, "Cập nhật sự kiện thành công."));
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
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Sự kiện không tồn tại."));
            }

            if (ev.OwnerId != currentUserId.Value)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Forbidden: Bạn không có quyền truy cập suất diễn của sự kiện này."));
            }

            var showtimesDto = ev.Showtimes.Select(s => new ShowtimeResponseDto
            {
                Id = s.Id,
                EventId = s.EventId,
                StartTime = s.StartTime,
                EndTime = s.EndTime,
                AvailableSeats = s.AvailableSeats
            }).ToList();

            return Ok(ApiResponse<List<ShowtimeResponseDto>>.SuccessResult(showtimesDto, "Lấy danh sách suất diễn thành công."));
        }

        #region Helper Methods

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

        private static EventResponseDto MapToEventResponseDto(Event ev)
        {
            return new EventResponseDto
            {
                Id = ev.Id,
                OwnerId = ev.OwnerId,
                Title = ev.Title,
                Description = ev.Description,
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
                    AvailableSeats = s.AvailableSeats
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
