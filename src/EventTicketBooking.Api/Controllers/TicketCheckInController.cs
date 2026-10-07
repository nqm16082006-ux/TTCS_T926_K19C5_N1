using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/checkin")]
    [Authorize] // Yêu cầu đăng nhập. Thực tế có thể đổi thành [RequireRole] như dự án đang có.
    public class TicketCheckInController : ControllerBase
    {
        private readonly AppDbContext _context;

        public TicketCheckInController(AppDbContext context)
        {
            _context = context;
        }

        // 1. Lấy suất diễn hôm nay
        [HttpGet("today-shows")]
        public async Task<IActionResult> GetTodayShows()
        {
            // Xác định Timezone Việt Nam
            TimeZoneInfo vnTimeZone;
            try
            {
                vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); // Windows
            }
            catch (TimeZoneNotFoundException)
            {
                vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); // Linux/Docker
            }

            var nowUtc = DateTime.UtcNow;
            var nowVn = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, vnTimeZone);
            var todayVnStartUtc = TimeZoneInfo.ConvertTimeToUtc(nowVn.Date, vnTimeZone);
            var tomorrowVnStartUtc = todayVnStartUtc.AddDays(1);

            // Lọc các suất diễn bắt đầu trong ngày hôm nay VÀ chưa kết thúc ở thời điểm hiện tại
            var shows = await _context.Showtimes
                .Include(s => s.Event)
                .Where(s => s.StartTime >= todayVnStartUtc
                         && s.StartTime < tomorrowVnStartUtc
                         && s.EndTime >= nowUtc)
                .Select(s => new
                {
                    s.Id,
                    EventTitle = s.Event.Title,
                    s.StartTime,
                    s.EndTime
                })
                .ToListAsync();

            return Ok(shows);
        }

        // 2. Soát vé (Scan)
        [HttpPost("scan")]
        public async Task<IActionResult> ScanTicket([FromBody] TicketScanRequestDto request)
        {
            // Kiểm tra phân quyền: Chỉ Admin hoặc Staff/Scanner mới được quét vé
            var isStaff = User.IsInRole("Admin") || User.HasClaim(c => (c.Type == "role" || c.Type == System.Security.Claims.ClaimTypes.Role) && (c.Value == "Admin" || c.Value == "Staff"));
            if (!isStaff)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Bạn không có quyền soát vé." });
            }

            // 1. Tìm vé (OrderItem)
            var ticket = await _context.OrderItems
                .Include(t => t.Order)
                .Include(t => t.Seat)
                .FirstOrDefaultAsync(t => t.Id == request.TicketId);

            if (ticket == null)
                return NotFound(new { Message = "Mã vé không tồn tại hoặc không hợp lệ." });

            // 2. Vé đã thanh toán chưa? (Dựa theo OrderStatus.Paid)
            if (ticket.Order.Status != OrderStatus.Paid)
                return BadRequest(new { Message = $"Cảnh báo: Vé này thuộc đơn hàng đang ở trạng thái {ticket.Order.Status}, chưa thanh toán thành công!" });

            // 3. Đúng suất không?
            if (ticket.Order.ShowtimeId != request.SelectedShowtimeId)
                return BadRequest(new { Message = "Cảnh báo: Vé này KHÔNG thuộc về suất diễn đang được chọn tại cổng!" });

            // 4. Đã soát chưa? (Kiểm tra đọc lần 1 để báo lỗi rõ ràng)
            if (ticket.IsCheckedIn)
            {
                var localCheckInTime = ticket.CheckInTime?.ToLocalTime().ToString("HH:mm dd/MM/yyyy") ?? "không rõ";
                return BadRequest(new { Message = $"Vé đã được sử dụng lúc {localCheckInTime} tại cửa {ticket.CheckInGate}." });
            }

            // 5. Cập nhật chống Race Condition bằng ExecuteUpdateAsync (Atomic update)
            var now = DateTimeOffset.UtcNow;
            var rowsAffected = await _context.OrderItems
                .Where(t => t.Id == request.TicketId && !t.IsCheckedIn)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.IsCheckedIn, true)
                    .SetProperty(t => t.CheckInGate, request.GateName)
                    .SetProperty(t => t.CheckInTime, now)
                );

            if (rowsAffected == 0)
            {
                // Có transaction khác vừa cập nhật thành công ngay sau lần đọc trên
                return BadRequest(new { Message = "Vé vừa được quét tại một thiết bị khác ngay tức thì." });
            }

            return Ok(new
            {
                Message = "Check-in thành công.",
                SeatInfo = $"{ticket.Seat.Row}{ticket.Seat.SeatNumber}",
                Gate = request.GateName
            });
        }
    }

    public class TicketScanRequestDto
    {
        public Guid TicketId { get; set; }
        public Guid SelectedShowtimeId { get; set; }
        public string GateName { get; set; } = string.Empty;
    }
}
