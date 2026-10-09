using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/checkin")]
    [RequireRole("Admin", "Staff")]
    public class TicketCheckInController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IQrSignatureService _qrSignatureService;

        public TicketCheckInController(AppDbContext context, IQrSignatureService? qrSignatureService = null)
        {
            _context = context;
            _qrSignatureService = qrSignatureService ?? new QrSignatureService();
        }

        // 1. Lấy suất diễn hôm nay (S-29)
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

        /// <summary>
        /// Story S-33: Tải trước danh sách vé của suất diễn xuống máy quét phục vụ soát vé ngoại tuyến.
        /// Hỗ trợ Full sync (since == null) và Delta sync (since != null) với cơ chế race mitigation lùi 15s.
        /// Chỉ trả về dữ liệu tối thiểu (TicketCode, IsCheckedIn), tuyệt đối không trả PII.
        /// </summary>
        [HttpGet("showtimes/{showtimeId}/offline-tickets")]
        public async Task<IActionResult> GetOfflineTickets(Guid showtimeId, [FromQuery] DateTimeOffset? since = null)
        {
            var showtimeExists = await _context.Showtimes.AnyAsync(s => s.Id == showtimeId);
            if (!showtimeExists)
            {
                return NotFound(new { Message = "Suất diễn không tồn tại." });
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var query = _context.Tickets
                .AsNoTracking()
                .Where(t => t.OrderItem.Order.ShowtimeId == showtimeId);

            bool isDelta = since.HasValue;
            if (since.HasValue)
            {
                var threshold = since.Value.AddSeconds(-15);
                query = query.Where(t => t.CreatedAt > threshold
                                      || (t.OrderItem.CheckInTime != null && t.OrderItem.CheckInTime > threshold));
            }

            var rawTickets = await query
                .Select(t => new
                {
                    TicketCode = t.TicketCode,
                    IsCheckedIn = t.OrderItem.IsCheckedIn,
                    OrderStatus = t.OrderItem.Order.Status
                })
                .ToListAsync();

            var tickets = rawTickets
                .Where(t => t.OrderStatus == OrderStatus.Paid)
                .Select(t => new OfflineTicketItemDto
                {
                    TicketCode = t.TicketCode,
                    IsCheckedIn = t.IsCheckedIn
                })
                .ToList();

            return Ok(new OfflineTicketSyncResponseDto
            {
                ShowtimeId = showtimeId,
                ServerTime = nowUtc,
                IsDelta = isDelta,
                TotalCount = tickets.Count,
                Tickets = tickets
            });
        }

        // 2. Soát vé (Scan) - Story S-29 & S-30
        [HttpPost("scan")]
        public async Task<IActionResult> ScanTicket([FromBody] TicketScanRequestDto request)
        {
            // Kiểm tra phân quyền: Chỉ Admin hoặc Staff/Scanner mới được quét vé
            var isStaff = User.IsInRole("Admin") || User.HasClaim(c => (c.Type == "role" || c.Type == System.Security.Claims.ClaimTypes.Role) && (c.Value == "Admin" || c.Value == "Staff"));
            if (!isStaff)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Bạn không có quyền soát vé." });
            }

            if (request == null)
            {
                return BadRequest(new { Message = "Yêu cầu soát vé không hợp lệ." });
            }

            string? verifiedTicketCode = null;
            Guid? qrShowtimeId = null;

            // 1. Nếu có QrPayload (Story S-30 / S-26): Xác thực chữ ký số mã QR
            if (!string.IsNullOrWhiteSpace(request.QrPayload))
            {
                var qrResult = _qrSignatureService.VerifyTicket(request.QrPayload.Trim());
                if (!qrResult.IsValid)
                {
                    return BadRequest(new
                    {
                        Message = $"Mã QR không hợp lệ hoặc chữ ký giả mạo: {qrResult.Message}",
                        Reason = qrResult.ErrorReason ?? "INVALID_QR_SIGNATURE"
                    });
                }

                verifiedTicketCode = qrResult.TicketCode;
                qrShowtimeId = qrResult.ShowtimeId;

                // Kiểm tra vé thuộc đúng suất diễn đang được chọn theo S-29 & S-30
                if (qrShowtimeId.HasValue && qrShowtimeId.Value != request.SelectedShowtimeId)
                {
                    return BadRequest(new
                    {
                        Message = "Cảnh báo: Vé này KHÔNG thuộc về suất diễn đang được chọn tại cổng!",
                        Reason = "WRONG_SHOWTIME",
                        TicketShowtimeId = qrShowtimeId,
                        SelectedShowtimeId = request.SelectedShowtimeId
                    });
                }
            }

            // 2. Tìm vé (OrderItem)
            OrderItem? ticket = null;

            // 2a. Nếu đã xác thực QR ra TicketCode hoặc có TicketCode truyền vào
            var codeToSearch = verifiedTicketCode ?? (!string.IsNullOrWhiteSpace(request.TicketCode) ? request.TicketCode.Trim() : null);
            if (!string.IsNullOrWhiteSpace(codeToSearch))
            {
                var ticketEntity = await _context.Tickets
                    .Include(t => t.OrderItem)
                        .ThenInclude(oi => oi.Order)
                    .Include(t => t.OrderItem)
                        .ThenInclude(oi => oi.Seat)
                            .ThenInclude(s => s.SeatCategory)
                    .FirstOrDefaultAsync(t => t.TicketCode == codeToSearch);

                if (ticketEntity != null)
                {
                    ticket = ticketEntity.OrderItem;
                    ticket.Ticket = ticketEntity;
                }
            }

            // 2b. Fallback: Nếu chưa tìm thấy và có TicketId
            if (ticket == null && request.TicketId.HasValue && request.TicketId.Value != Guid.Empty)
            {
                ticket = await _context.OrderItems
                    .Include(t => t.Order)
                    .Include(t => t.Seat)
                    .FirstOrDefaultAsync(t => t.Id == request.TicketId.Value);

                if (ticket == null)
                {
                    var ticketEntity = await _context.Tickets
                        .Include(t => t.OrderItem)
                            .ThenInclude(oi => oi.Order)
                        .Include(t => t.OrderItem)
                            .ThenInclude(oi => oi.Seat)
                        .FirstOrDefaultAsync(t => t.Id == request.TicketId.Value);

                    if (ticketEntity != null)
                    {
                        ticket = ticketEntity.OrderItem;
                        ticket.Ticket = ticketEntity;
                    }
                }
            }

            if (ticket == null)
            {
                return NotFound(new { Message = "Mã vé không tồn tại hoặc không hợp lệ." });
            }

            // 3. Vé đã thanh toán chưa? (Dựa theo OrderStatus.Paid)
            if (ticket.Order.Status != OrderStatus.Paid)
            {
                return BadRequest(new { Message = $"Cảnh báo: Vé này thuộc đơn hàng đang ở trạng thái {ticket.Order.Status}, chưa thanh toán thành công!" });
            }

            // 4. Đúng suất không? (Kiểm tra Order.ShowtimeId)
            if (ticket.Order.ShowtimeId != request.SelectedShowtimeId)
            {
                return BadRequest(new
                {
                    Message = "Cảnh báo: Vé này KHÔNG thuộc về suất diễn đang được chọn tại cổng!",
                    Reason = "WRONG_SHOWTIME",
                    TicketShowtimeId = ticket.Order.ShowtimeId,
                    SelectedShowtimeId = request.SelectedShowtimeId
                });
            }

            // 5. Đã soát chưa? (Kiểm tra đọc lần 1 để báo lỗi rõ ràng)
            if (ticket.IsCheckedIn)
            {
                var localCheckInTime = ticket.CheckInTime?.ToLocalTime().ToString("HH:mm dd/MM/yyyy") ?? "không rõ";
                return BadRequest(new { Message = $"Vé đã được sử dụng lúc {localCheckInTime} tại cửa {ticket.CheckInGate}." });
            }

            // 6. Cập nhật chống Race Condition bằng ExecuteUpdateAsync (Atomic update)
            var now = DateTimeOffset.UtcNow;
            int rowsAffected;
            if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            {
                ticket.IsCheckedIn = true;
                ticket.CheckInGate = request.GateName;
                ticket.CheckInTime = now;
                rowsAffected = await _context.SaveChangesAsync();
            }
            else
            {
                rowsAffected = await _context.OrderItems
                    .Where(t => t.Id == ticket.Id && !t.IsCheckedIn)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.IsCheckedIn, true)
                        .SetProperty(t => t.CheckInGate, request.GateName)
                        .SetProperty(t => t.CheckInTime, now)
                    );
            }

            if (rowsAffected == 0)
            {
                // Có transaction khác vừa cập nhật thành công ngay sau lần đọc trên
                return BadRequest(new { Message = "Vé vừa được quét tại một thiết bị khác ngay tức thì." });
            }

            // 7. Vé hợp lệ (S-30): Báo vé hợp lệ, hiển thị hạng vé + số ghế, lượt soát và thời điểm
            var seatCategoryName = ticket.Seat?.SeatCategory?.Name;
            if (string.IsNullOrWhiteSpace(seatCategoryName) && ticket.Seat != null)
            {
                var cat = await _context.SeatCategories.FindAsync(ticket.Seat.SeatCategoryId);
                if (cat != null)
                {
                    seatCategoryName = cat.Name;
                }
            }
            seatCategoryName ??= "Tiêu chuẩn";

            var seatRow = ticket.Seat?.Row ?? "";
            var seatNumber = ticket.Seat?.SeatNumber ?? 0;
            var seatInfo = !string.IsNullOrEmpty(seatRow) ? $"{seatRow}{seatNumber}" : $"{seatNumber}";

            var resultTicketCode = ticket.Ticket?.TicketCode ?? codeToSearch;
            if (string.IsNullOrWhiteSpace(resultTicketCode))
            {
                var ticketRec = await _context.Tickets.FirstOrDefaultAsync(t => t.OrderItemId == ticket.Id);
                resultTicketCode = ticketRec?.TicketCode;
            }

            return Ok(new
            {
                Message = "Check-in thành công.",
                SeatInfo = seatInfo,
                SeatRow = seatRow,
                SeatNumber = seatNumber,
                CategoryName = seatCategoryName,
                Gate = request.GateName,
                CheckInTime = now,
                TicketCode = resultTicketCode
            });
        }
    }

    public class TicketScanRequestDto
    {
        public Guid? TicketId { get; set; }
        public string? QrPayload { get; set; }
        public string? TicketCode { get; set; }
        public Guid SelectedShowtimeId { get; set; }
        public string GateName { get; set; } = string.Empty;
    }
}
