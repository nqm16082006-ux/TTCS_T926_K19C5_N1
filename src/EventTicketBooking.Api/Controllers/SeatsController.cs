using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SeatsController : ControllerBase
{
    private readonly AppDbContext _context;

    public SeatsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("available/{showtimeId}")]
    public async Task<IActionResult> GetAvailableSeats(int showtimeId, [FromQuery] DateTime? nowOverride = null)
    {
        // Nhận thời điểm giả lập từ query string (nếu có) để test không phải chờ thật
        var currentTime = nowOverride ?? DateTime.UtcNow;

        // LINQ Query: Lấy ghế có trạng thái AVAILABLE
        // HOẶC ghế đang bị HELD (ACTIVE) nhưng đã quá hạn (ExpiresAt <= currentTime)
        var availableSeats = await _context.Seats
            .Where(s => s.ShowtimeId == showtimeId)
            .Where(s => s.Status == "AVAILABLE" ||
                        _context.SeatHold.Any(h => h.SeatId == s.Id &&
                                                    h.Status == "ACTIVE" &&
                                                    h.ExpiresAt <= currentTime))
            .ToListAsync();

        return Ok(availableSeats);
    }
}