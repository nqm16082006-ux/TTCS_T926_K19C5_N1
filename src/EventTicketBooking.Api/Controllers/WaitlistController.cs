using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    /// <summary>
    /// Controller xử lý đăng ký danh sách chờ khi suất diễn hết vé (Story S-59).
    /// </summary>
    [ApiController]
    [Route("api/waitlist")]
    public class WaitlistController : ControllerBase
    {
        private readonly IWaitlistService _waitlistService;

        public WaitlistController(IWaitlistService waitlistService)
        {
            _waitlistService = waitlistService;
        }

        private Guid? GetCurrentUserId()
        {
            if (User == null) return null;

            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier)
                            ?? User.FindFirst("id")?.Value
                            ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

            return Guid.TryParse(userIdStr, out var id) ? id : null;
        }

        /// <summary>
        /// Đăng ký vào danh sách chờ cho suất diễn hết vé (Story S-59).
        /// </summary>
        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> JoinWaitlist(
            [FromBody] JoinWaitlistRequestDto request,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Dữ liệu đăng ký không hợp lệ.", ModelState));
            }

            try
            {
                var userId = GetCurrentUserId();
                var result = await _waitlistService.JoinWaitlistAsync(request, userId, cancellationToken);
                return Ok(ApiResponse<WaitlistRegistrationResultDto>.SuccessResult(result, result.Message));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<object>.FailureResult(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.FailureResult(ex.Message));
            }
        }

        /// <summary>
        /// Kiểm tra trạng thái danh sách chờ của suất diễn (số người chờ, vị trí hiện tại nếu có email).
        /// </summary>
        [HttpGet("status")]
        [AllowAnonymous]
        public async Task<IActionResult> GetStatus(
            [FromQuery] Guid showtimeId,
            [FromQuery] string? email,
            CancellationToken cancellationToken)
        {
            if (showtimeId == Guid.Empty)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Mã suất diễn không hợp lệ."));
            }

            try
            {
                var userId = GetCurrentUserId();
                var result = await _waitlistService.GetStatusAsync(showtimeId, email, userId, cancellationToken);
                return Ok(ApiResponse<WaitlistStatusResponseDto>.SuccessResult(result, "Lấy thông tin danh sách chờ thành công."));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.FailureResult(ex.Message));
            }
        }

        /// <summary>
        /// Ban tổ chức / Quản trị viên xem toàn bộ danh sách chờ theo thứ tự đăng ký của một suất diễn.
        /// </summary>
        [HttpGet("showtime/{showtimeId}")]
        [RequireRole("Organizer", "Admin", "Staff")]
        public async Task<IActionResult> GetShowtimeWaitlist(
            [FromRoute] Guid showtimeId,
            CancellationToken cancellationToken)
        {
            var result = await _waitlistService.GetWaitlistForShowtimeAsync(showtimeId, cancellationToken);
            return Ok(ApiResponse<List<WaitlistEntryDto>>.SuccessResult(result, "Lấy danh sách chờ của suất diễn thành công."));
        }

        /// <summary>
        /// Ban tổ chức chủ động kích hoạt gửi email thông báo cho người trong danh sách chờ khi mở thêm vé.
        /// </summary>
        [HttpPost("showtime/{showtimeId}/notify")]
        [RequireRole("Organizer", "Admin")]
        public async Task<IActionResult> NotifyWaitlist(
            [FromRoute] Guid showtimeId,
            [FromQuery] int seatsCount = 1,
            CancellationToken cancellationToken = default)
        {
            int notified = await _waitlistService.ProcessAvailableSeatsNotificationAsync(showtimeId, seatsCount, cancellationToken);
            return Ok(ApiResponse<int>.SuccessResult(notified, $"Đã gửi email thông báo vé mở bán thành công cho {notified} người chờ."));
        }
    }
}
