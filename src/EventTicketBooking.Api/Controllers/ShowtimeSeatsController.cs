using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/showtimes/{showtimeId:guid}/seats")]
    public class ShowtimeSeatsController : ControllerBase
    {
        private readonly SeatImportService _seatImportService;
        private readonly ISeatHoldService _seatHoldService;
        private readonly ILogger<ShowtimeSeatsController> _logger;

        public ShowtimeSeatsController(
            SeatImportService seatImportService,
            ISeatHoldService seatHoldService,
            ILogger<ShowtimeSeatsController> logger)
        {
            _seatImportService = seatImportService;
            _seatHoldService = seatHoldService;
            _logger = logger;
        }

        /// <summary>
        /// API Giữ ghế cho người dùng đăng nhập (Task T-23 / S-10).
        /// POST /api/showtimes/{showtimeId}/seats/hold
        /// </summary>
        [HttpPost("hold")]
        [ProducesResponseType(typeof(ApiResponse<HoldSeatsResponseDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> HoldSeats(
            Guid showtimeId,
            [FromBody] HoldSeatsRequestDto request,
            CancellationToken cancellationToken)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện giữ chỗ."));
            }

            var result = await _seatHoldService.HoldSeatsAsync(
                showtimeId,
                request?.SeatIds ?? new List<Guid>(),
                userId.Value,
                cancellationToken);

            return result.Status switch
            {
                HoldSeatsResultStatus.Success => Ok(ApiResponse<HoldSeatsResponseDto>.SuccessResult(result.Data!, result.Message)),
                HoldSeatsResultStatus.NotFound => NotFound(ApiResponse<object>.FailureResult(result.Message)),
                HoldSeatsResultStatus.InvalidRequest => BadRequest(ApiResponse<object>.FailureResult(result.Message)),
                HoldSeatsResultStatus.Conflict => StatusCode(StatusCodes.Status409Conflict, ApiResponse<object>.FailureResult(result.Message)),
                _ => StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.FailureResult(result.Message))
            };
        }

        [HttpPost("import")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(SeatImportResultDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SeatImportResultDto>> Import(
            Guid showtimeId,
            [FromForm] IFormFile? file,
            CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid seat import file",
                    detail: "A non-empty JSON file is required.");
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var result = await _seatImportService.ImportAsync(showtimeId, stream, cancellationToken);
                return Ok(result);
            }
            catch (EventTicketBooking.Api.Exceptions.SeatValidationException ex)
            {
                return BadRequest(new { errors = ex.Errors });
            }
            catch (InvalidDataException ex)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid seat import file",
                    detail: ex.Message);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Showtime not found",
                    detail: ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Seat import conflict",
                    detail: ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import seats for showtime {ShowtimeId}.", showtimeId);

                return Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Seat import failed",
                    detail: "The seat import could not be completed.");
            }
        }

        [HttpPost("preview")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(List<SeatImportItemDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<SeatImportItemDto>>> Preview(
            [FromForm] IFormFile? file,
            CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid seat import file",
                    detail: "A non-empty JSON file is required.");
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var result = await _seatImportService.PreviewAsync(stream, cancellationToken);
                return Ok(result);
            }
            catch (EventTicketBooking.Api.Exceptions.SeatValidationException ex)
            {
                return BadRequest(new { errors = ex.Errors });
            }
            catch (InvalidDataException ex)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid seat import file",
                    detail: ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to preview seats.");

                return Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Seat preview failed",
                    detail: "The seat preview could not be completed.");
            }
        }

        #region Private Helpers

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

        #endregion
    }
}
