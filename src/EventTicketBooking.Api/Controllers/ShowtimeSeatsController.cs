using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/showtimes/{showtimeId:guid}/seats")]
    public class ShowtimeSeatsController : ControllerBase
    {
        private readonly SeatImportService _seatImportService;
        private readonly ILogger<ShowtimeSeatsController> _logger;

        public ShowtimeSeatsController(
            SeatImportService seatImportService,
            ILogger<ShowtimeSeatsController> logger)
        {
            _seatImportService = seatImportService;
            _logger = logger;
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
    }
}
