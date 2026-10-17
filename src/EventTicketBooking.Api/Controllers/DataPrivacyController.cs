using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1")]
    public class DataPrivacyController : ControllerBase
    {
        private readonly IDataDeletionService _dataDeletionService;
        private readonly ILogger<DataPrivacyController> _logger;

        public DataPrivacyController(
            IDataDeletionService dataDeletionService,
            ILogger<DataPrivacyController> logger)
        {
            _dataDeletionService = dataDeletionService;
            _logger = logger;
        }

        /// <summary>
        /// Khách hàng gửi yêu cầu xoá dữ liệu cá nhân (Story S-53 / Nghị định 13/2023/NĐ-CP).
        /// </summary>
        [HttpPost("user/privacy/data-deletion")]
        [RequireRole]
        [ProducesResponseType(typeof(DataDeletionRequestDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> CreateDeletionRequest([FromBody] CreateDataDeletionRequestDto dto)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { message = "Không xác định được danh tính người dùng." });
            }

            try
            {
                var result = await _dataDeletionService.CreateRequestAsync(userId.Value, dto);
                return StatusCode(StatusCodes.Status201Created, result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo yêu cầu xoá dữ liệu cá nhân cho user {UserId}", userId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Đã xảy ra lỗi nội bộ máy chủ khi gửi yêu cầu." });
            }
        }

        /// <summary>
        /// Khách hàng xem trạng thái yêu cầu xoá dữ liệu cá nhân của mình.
        /// </summary>
        [HttpGet("user/privacy/data-deletion/status")]
        [RequireRole]
        [ProducesResponseType(typeof(DataDeletionStatusDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetDeletionStatus()
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { message = "Không xác định được danh tính người dùng." });
            }

            try
            {
                var result = await _dataDeletionService.GetUserStatusAsync(userId.Value);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy trạng thái xoá dữ liệu cho user {UserId}", userId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Đã xảy ra lỗi nội bộ máy chủ." });
            }
        }

        /// <summary>
        /// Admin lấy danh sách các yêu cầu xoá dữ liệu cá nhân (Story S-53 NFR: người có thẩm quyền phê duyệt).
        /// </summary>
        [HttpGet("admin/privacy/data-deletion-requests")]
        [RequireRole("Admin")]
        [ProducesResponseType(typeof(List<DataDeletionRequestDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetAllDeletionRequests([FromQuery] string? status)
        {
            try
            {
                var list = await _dataDeletionService.GetAllRequestsAsync(status);
                return Ok(list);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách yêu cầu xoá dữ liệu");
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Đã xảy ra lỗi nội bộ máy chủ." });
            }
        }

        /// <summary>
        /// Người có thẩm quyền (Admin) duyệt hoặc từ chối yêu cầu xoá dữ liệu cá nhân (Story S-53 AC & NFR).
        /// </summary>
        [HttpPost("admin/privacy/data-deletion-requests/{id:guid}/review")]
        [RequireRole("Admin")]
        [ProducesResponseType(typeof(DataDeletionRequestDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ReviewDeletionRequest(
            [FromRoute] Guid id,
            [FromBody] ReviewDataDeletionRequestDto dto)
        {
            var adminId = GetCurrentUserId();
            if (!adminId.HasValue)
            {
                return Unauthorized(new { message = "Không xác định được danh tính quản trị viên." });
            }

            try
            {
                var result = await _dataDeletionService.ReviewRequestAsync(id, adminId.Value, dto);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi phê duyệt yêu cầu xoá dữ liệu {RequestId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Đã xảy ra lỗi nội bộ máy chủ khi xử lý yêu cầu." });
            }
        }

        private Guid? GetCurrentUserId()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirst("id")?.Value
                ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

            if (Guid.TryParse(userIdStr, out var userId))
            {
                return userId;
            }

            return null;
        }
    }
}
