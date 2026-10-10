using System;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/showtimes/{showtimeId:guid}/waiting-room")]
    public class WaitingRoomController : ControllerBase
    {
        private readonly IWaitingRoomService _waitingRoomService;

        public WaitingRoomController(IWaitingRoomService waitingRoomService)
        {
            _waitingRoomService = waitingRoomService;
        }

        private Guid? GetCurrentUserId()
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub")
                   ?? User.FindFirstValue("nameid")
                   ?? User.FindFirstValue("id");
            if (Guid.TryParse(sub, out var id)) return id;

            // Nếu request có Bearer token nhưng pipeline chưa chạy xác thực role (endpoint public)
            var authHeader = Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring("Bearer ".Length).Trim();
                try
                {
                    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                    if (handler.CanReadToken(token))
                    {
                        var jwt = handler.ReadJwtToken(token);
                        var claim = jwt.Claims.FirstOrDefault(c =>
                            c.Type == ClaimTypes.NameIdentifier ||
                            c.Type == "sub" ||
                            c.Type == "nameid" ||
                            c.Type == "id" ||
                            c.Type.EndsWith("/nameidentifier"));
                        if (claim != null && Guid.TryParse(claim.Value, out var parsedId))
                        {
                            return parsedId;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private string GetClientIdentifier(string? clientSessionId)
        {
            if (!string.IsNullOrWhiteSpace(clientSessionId))
            {
                return clientSessionId.Trim();
            }

            var userId = GetCurrentUserId();
            if (userId.HasValue)
            {
                return "user_" + userId.Value;
            }

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";
            var ua = Request.Headers.UserAgent.ToString();
            return $"guest_{ip}_{ua.GetHashCode():X8}";
        }

        /// <summary>
        /// Gia nhập hàng chờ phòng chờ hoặc nhận lượt truy cập trực tiếp nếu chưa vượt ngưỡng.
        /// POST /api/showtimes/{showtimeId}/waiting-room/join
        /// </summary>
        [HttpPost("join")]
        [ProducesResponseType(typeof(ApiResponse<WaitingRoomStatusDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> JoinQueue(
            Guid showtimeId,
            [FromBody] WaitingRoomJoinRequestDto? request)
        {
            var userId = GetCurrentUserId();
            var clientIdentifier = GetClientIdentifier(request?.ClientSessionId);

            var status = await _waitingRoomService.JoinQueueAsync(showtimeId, userId, clientIdentifier);

            string message = status.Status == "ADMITTED"
                ? "Đã cấp lượt truy cập chọn ghế thành công."
                : $"Bạn đang ở vị trí #{status.Position} trong hàng chờ.";

            return Ok(ApiResponse<WaitingRoomStatusDto>.SuccessResult(status, message));
        }

        /// <summary>
        /// Gửi heartbeat định kỳ để cập nhật tiến độ và nhận vé thông hành khi đến lượt.
        /// POST /api/showtimes/{showtimeId}/waiting-room/heartbeat
        /// </summary>
        [HttpPost("heartbeat")]
        [ProducesResponseType(typeof(ApiResponse<WaitingRoomStatusDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Heartbeat(
            Guid showtimeId,
            [FromBody] WaitingRoomHeartbeatRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.QueueToken))
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thiếu mã hàng đợi (queueToken)."));
            }

            var status = await _waitingRoomService.HeartbeatAsync(showtimeId, request.QueueToken);
            if (status == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Phiên hàng chờ không tồn tại hoặc đã bị hủy do quá hạn."));
            }

            string message = status.Status == "ADMITTED"
                ? "Đã đến lượt của bạn! Chuyển hướng vào trang chọn ghế..."
                : $"Vị trí hiện tại: #{status.Position} (còn {status.PeopleAhead} người phía trước).";

            return Ok(ApiResponse<WaitingRoomStatusDto>.SuccessResult(status, message));
        }

        /// <summary>
        /// Rời khỏi hàng chờ.
        /// POST /api/showtimes/{showtimeId}/waiting-room/leave
        /// </summary>
        [HttpPost("leave")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> LeaveQueue(
            Guid showtimeId,
            [FromQuery] string? queueToken)
        {
            var token = queueToken ?? Request.Headers["X-Queue-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token) && Request.HasJsonContentType())
            {
                try
                {
                    var body = await Request.ReadFromJsonAsync<WaitingRoomLeaveRequestDto>();
                    token = body?.QueueToken;
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thiếu mã hàng đợi (queueToken)."));
            }

            var success = await _waitingRoomService.LeaveQueueAsync(showtimeId, token);
            return Ok(ApiResponse<object>.SuccessResult(new { left = success }, "Đã rời khỏi hàng chờ."));
        }

        /// <summary>
        /// Lấy trạng thái hàng chờ theo mã hàng đợi.
        /// GET /api/showtimes/{showtimeId}/waiting-room/status?token={queueToken}
        /// </summary>
        [HttpGet("status")]
        [ProducesResponseType(typeof(ApiResponse<WaitingRoomStatusDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetStatus(
            Guid showtimeId,
            [FromQuery] string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thiếu mã hàng đợi (token)."));
            }

            var status = await _waitingRoomService.HeartbeatAsync(showtimeId, token);
            if (status == null)
            {
                return NotFound(ApiResponse<object>.FailureResult("Phiên hàng chờ không tồn tại."));
            }

            return Ok(ApiResponse<WaitingRoomStatusDto>.SuccessResult(status, "Lấy trạng thái hàng chờ thành công."));
        }

        /// <summary>
        /// Kiểm tra tính hợp lệ của vé thông hành phòng chờ.
        /// GET /api/showtimes/{showtimeId}/waiting-room/validate?passToken={passToken}&clientSessionId={clientSessionId}
        /// </summary>
        [HttpGet("validate")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ValidatePass(
            Guid showtimeId,
            [FromQuery] string passToken,
            [FromQuery] string? clientSessionId)
        {
            if (string.IsNullOrWhiteSpace(passToken))
            {
                return Ok(ApiResponse<object>.SuccessResult(new { isValid = false }, "Thiếu mã vé thông hành."));
            }

            var userId = GetCurrentUserId();
            bool isValid = await _waitingRoomService.ValidatePassTokenAsync(showtimeId, passToken, clientSessionId, userId);
            return Ok(ApiResponse<object>.SuccessResult(new { isValid }, isValid ? "Vé thông hành hợp lệ." : "Vé thông hành không hợp lệ hoặc đã hết hạn."));
        }

        /// <summary>
        /// Lấy thông tin thống kê phòng chờ của suất chiếu (Active, Queue, Threshold).
        /// GET /api/showtimes/{showtimeId}/waiting-room/stats
        /// </summary>
        [HttpGet("stats")]
        [ProducesResponseType(typeof(ApiResponse<WaitingRoomStatsDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStats(Guid showtimeId)
        {
            var stats = await _waitingRoomService.GetStatsAsync(showtimeId);
            return Ok(ApiResponse<WaitingRoomStatsDto>.SuccessResult(stats, "Lấy thống kê phòng chờ thành công."));
        }

        /// <summary>
        /// Cấu hình ngưỡng số người hoạt động cùng lúc cho suất chiếu này.
        /// PUT /api/showtimes/{showtimeId}/waiting-room/threshold
        /// </summary>
        [HttpPut("threshold")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> SetThreshold(
            Guid showtimeId,
            [FromBody] UpdateThresholdRequestDto request)
        {
            if (request == null || request.Threshold <= 0)
            {
                return BadRequest(ApiResponse<object>.FailureResult("Ngưỡng (threshold) phải lớn hơn 0."));
            }

            await _waitingRoomService.SetShowtimeThresholdAsync(showtimeId, request.Threshold);
            return Ok(ApiResponse<object>.SuccessResult(new { threshold = request.Threshold }, "Cập nhật ngưỡng phòng chờ thành công."));
        }

        /// <summary>
        /// Gửi tín hiệu sống (active-ping) định kỳ từ trang chọn ghế để duy trì phiên hoạt động.
        /// POST /api/showtimes/{showtimeId}/waiting-room/active-ping
        /// </summary>
        [HttpPost("active-ping")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ActivePing(
            Guid showtimeId,
            [FromQuery] string? passToken,
            [FromQuery] string? clientSessionId)
        {
            var token = passToken ?? Request.Headers["X-Access-Pass"].ToString();
            var clientId = clientSessionId;

            if (string.IsNullOrWhiteSpace(token) && Request.HasJsonContentType())
            {
                try
                {
                    var body = await Request.ReadFromJsonAsync<WaitingRoomActivePingRequestDto>();
                    token = body?.PassToken ?? token;
                    clientId = body?.ClientSessionId ?? clientId;
                }
                catch { }
            }

            clientId ??= GetClientIdentifier(null);

            if (string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(ApiResponse<object>.FailureResult("Thiếu mã vé thông hành."));
            }

            bool alive = await _waitingRoomService.ActivePingAsync(showtimeId, token, clientId);
            return Ok(ApiResponse<object>.SuccessResult(new { alive }, alive ? "Phiên còn hiệu lực." : "Phiên không hợp lệ hoặc đã hết hạn."));
        }

        /// <summary>
        /// Giải phóng lượt truy cập khi hoàn tất chọn vé hoặc thoát trang.
        /// POST /api/showtimes/{showtimeId}/waiting-room/release
        /// </summary>
        [HttpPost("release")]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ReleasePass(
            Guid showtimeId,
            [FromQuery] string? passToken)
        {
            var token = passToken ?? Request.Headers["X-Access-Pass"].ToString();
            if (string.IsNullOrWhiteSpace(token) && Request.HasJsonContentType())
            {
                try
                {
                    var body = await Request.ReadFromJsonAsync<WaitingRoomReleaseRequestDto>();
                    token = body?.PassToken;
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                await _waitingRoomService.ReleaseActivePassAsync(showtimeId, token);
            }

            return Ok(ApiResponse<object>.SuccessResult(new { released = true }, "Đã giải phóng lượt truy cập."));
        }
    }
}
