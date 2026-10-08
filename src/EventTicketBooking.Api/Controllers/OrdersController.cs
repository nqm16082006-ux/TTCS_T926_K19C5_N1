using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/orders")]
    public class OrdersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<OrdersController> _logger;
        private readonly EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue _ticketEmailQueue;
        private readonly EventTicketBooking.Api.Services.Interfaces.ITicketService _ticketService;

        public OrdersController(
            AppDbContext context,
            ILogger<OrdersController> logger,
            EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue ticketEmailQueue,
            EventTicketBooking.Api.Services.Interfaces.ITicketService? ticketService = null)
        {
            _context = context;
            _logger = logger;
            _ticketEmailQueue = ticketEmailQueue;
            _ticketService = ticketService ?? new EventTicketBooking.Api.Services.Implementations.TicketService();
        }

        /// <summary>
        /// T-37: Tạo đơn hàng từ giữ chỗ và gia hạn giữ chỗ
        /// </summary>
        [HttpPost("showtimes/{showtimeId:guid}")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<OrderDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateOrderFromHolds(Guid showtimeId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            // Kiểm tra suất diễn
            var showtime = await _context.Showtimes.FirstOrDefaultAsync(s => s.Id == showtimeId);
            if (showtime == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy suất diễn."));
            if (showtime.Status != ShowtimeStatus.OnSale)
                return Conflict(ApiResponse<object>.FailureResult("Suất diễn không đang mở bán."));

            // 1. Chống bấm đúp: Trả về đơn chờ hiện tại nếu đã có
            var existingOrder = await _context.Orders
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .FirstOrDefaultAsync(o => o.UserId == userId && o.ShowtimeId == showtimeId && o.Status == OrderStatus.Pending);

            if (existingOrder != null)
            {
                if (existingOrder.ExpiresAt <= DateTimeOffset.UtcNow)
                    return Conflict(ApiResponse<object>.FailureResult("Đơn hàng trước đã hết hạn, vui lòng chờ hệ thống giải phóng và đặt lại."));
                return Ok(ApiResponse<OrderDto>.SuccessResult(MapToDto(existingOrder), "Đã tồn tại đơn hàng đang chờ thanh toán."));
            }

            var now = DateTimeOffset.UtcNow;

            using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                // 2. Lấy giữ chỗ (Không nhận danh sách ghế từ client)
                var holds = await _context.SeatHold
                    .Include(sh => sh.Seat)
                    .ThenInclude(s => s.SeatCategory)
                    .Where(sh => sh.UserId == userId && sh.Seat.ShowtimeId == showtimeId && sh.Status == "ACTIVE")
                    .ToListAsync();

                if (!holds.Any())
                {
                    return BadRequest(ApiResponse<object>.FailureResult("Bạn không giữ chỗ nào hoặc giữ chỗ đã hết hạn."));
                }

                if (holds.Any(h => h.Seat.Status == "SOLD" || h.Seat.SeatCategory.ShowtimeId != showtimeId || h.Seat.SeatCategory.Price < 0))
                    return Conflict(ApiResponse<object>.FailureResult("Ghế hoặc giá ghế không còn hợp lệ."));
                var totalPrice = holds.Sum(h => (long)(h.Seat.SeatCategory.Price ?? 0));
                if (totalPrice > int.MaxValue)
                    return BadRequest(ApiResponse<object>.FailureResult("Tổng tiền đơn hàng vượt giới hạn thanh toán."));

                var expiredHolds = holds.Where(h => h.ExpiresAt <= now.UtcDateTime).ToList();
                if (expiredHolds.Any())
                {
                    var expiredSeatNames = string.Join(", ", expiredHolds.Select(h => $"{h.Seat.Row}{h.Seat.SeatNumber}"));
                    return Conflict(ApiResponse<object>.FailureResult($"Các ghế sau đã hết hạn giữ chỗ: {expiredSeatNames}. Vui lòng đặt lại."));
                }

                // 3. Tính tổng và tạo đơn hàng (T-38: Tính ở máy chủ)
                var orderItems = new List<OrderItem>();

                // Gia hạn giữ chỗ bằng thời hạn thanh toán (giả sử 15 phút - E-05)
                var paymentExpiry = now.AddMinutes(15);

                foreach (var hold in holds)
                {
                    if (hold.Seat.SeatCategory.Price == null)
                    {
                        return BadRequest(ApiResponse<object>.FailureResult($"Ghế {hold.Seat.Row}{hold.Seat.SeatNumber} chưa được định giá."));
                    }

                    int price = hold.Seat.SeatCategory.Price.Value;

                    orderItems.Add(new OrderItem
                    {
                        SeatId = hold.SeatId,
                        Price = price
                    });

                    // Gia hạn thời gian giữ chỗ để khớp với hạn thanh toán
                    hold.ExpiresAt = paymentExpiry.UtcDateTime;
                }

                var order = new Order
                {
                    UserId = userId,
                    ShowtimeId = showtimeId,
                    Status = OrderStatus.Pending,
                    ExpiresAt = paymentExpiry,
                    OrderItems = orderItems
                };

                order.CalculateTotal();
                if (order.TotalAmount == 0)
                {
                    order.Status = OrderStatus.Paid;
                    foreach (var hold in holds)
                    {
                        hold.Status = "CONVERTED";
                        hold.Seat.Status = "SOLD";
                    }

                    // Story S-25: Sinh vé điện tử cho đơn hàng 0 VNĐ khi tự chuyển Paid
                    foreach (var item in order.OrderItems)
                    {
                        var ticket = new Ticket
                        {
                            Id = Guid.NewGuid(),
                            OrderItemId = item.Id,
                            TicketCode = _ticketService.GenerateTicketCode(),
                            CreatedAt = DateTimeOffset.UtcNow,
                            OrderItem = item
                        };
                        _context.Tickets.Add(ticket);
                        item.Ticket = ticket;
                    }
                }

                _context.Orders.Add(order);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                return Ok(ApiResponse<OrderDto>.SuccessResult(MapToDto(order), "Tạo đơn hàng thành công."));
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync();
                _context.ChangeTracker.Clear();
                _logger.LogWarning(ex, "Concurrent order creation for User {UserId}", userId);
                return Conflict(ApiResponse<object>.FailureResult("Trạng thái đặt vé vừa thay đổi. Vui lòng tải lại và thử lại."));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Lỗi khi tạo đơn hàng từ giữ chỗ cho User {UserId}", userId);
                return StatusCode(500, ApiResponse<object>.FailureResult("Đã xảy ra lỗi hệ thống."));
            }
        }

        /// <summary>
        /// T-39: Lấy thông tin tóm tắt đơn hàng (ghế, hạng, đơn giá, tổng tiền, thời gian còn lại)
        /// </summary>
        [HttpGet("{orderId:guid}")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<OrderDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetOrderSummary(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.Showtime)
                    .ThenInclude(st => st.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .Include(o => o.PaymentTransaction)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            // T-39: Chỉ chủ đơn hoặc Admin mở được
            var isAdmin = User.IsInRole("Admin") || User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == "Admin");
            if (!isAdmin && order.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập đơn hàng này."));

            return Ok(ApiResponse<OrderDto>.SuccessResult(MapToDto(order), "Lấy thông tin đơn hàng thành công."));
        }

        /// <summary>
        /// T-50: API kiểm tra trạng thái đơn hàng (polling)
        /// GET /api/v1/orders/{orderId}/status
        /// </summary>
        [HttpGet("{orderId:guid}/status")]
        [RequireRole]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> GetOrderStatus(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders
                .AsNoTracking()
                .Where(o => o.Id == orderId)
                .Select(o => new { o.UserId, o.Status })
                .FirstOrDefaultAsync();

            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            var isAdmin = User.IsInRole("Admin") || User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == "Admin");
            if (!isAdmin && order.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập đơn hàng này."));

            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";

            return Ok(ApiResponse<OrderStatusResponseDto>.SuccessResult(new OrderStatusResponseDto { Status = order.Status.ToString() }, "Lấy trạng thái thành công."));
        }

        /// <summary>
        /// S-27 Task 2: Gửi lại email vé điện tử
        /// POST /api/v1/orders/{orderId}/resend-tickets
        /// </summary>
        [HttpPost("{orderId:guid}/resend-tickets")]
        [RequireRole]
        public async Task<IActionResult> ResendTickets(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders
                .AsNoTracking()

                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            if (order.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền gửi lại vé cho đơn hàng này."));

            if (order.Status != OrderStatus.Paid)
                return BadRequest(ApiResponse<object>.FailureResult("Chỉ có thể gửi lại vé cho đơn hàng đã thanh toán."));

            await _ticketEmailQueue.EnqueueAsync(orderId);

            return Ok(ApiResponse<object>.SuccessResult(null, "Yêu cầu gửi lại vé đã được tiếp nhận."));
        }

        /// <summary>
        /// S-25: API lấy danh sách vé điện tử của đơn hàng (Read-Only).
        /// GET /api/v1/orders/{orderId}/tickets
        /// </summary>
        [HttpGet("{orderId:guid}/tickets")]
        [RequireRole]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [ProducesResponseType(typeof(ApiResponse<List<TicketDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetOrderTickets(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders
                .AsNoTracking()
                .Include(o => o.Showtime)
                    .ThenInclude(st => st.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Ticket)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            var isAdmin = User.IsInRole("Admin") || User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == "Admin");
            if (!isAdmin && order.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập vé của đơn hàng này."));

            // AC3: Giả sử đơn chưa trả, khi mở trang vé bằng đường dẫn, thì không có vé nào.
            if (order.Status != OrderStatus.Paid)
            {
                return Ok(ApiResponse<List<TicketDto>>.SuccessResult(new List<TicketDto>(), "Đơn hàng chưa thanh toán. Không có vé nào."));
            }

            // S-25 & S-26: Đơn đã Paid -> Trả về danh sách vé kèm mã QR có chữ ký số (ECDSA P-256)
            var showtimeId = order.ShowtimeId;
            var tickets = order.OrderItems
                .Where(oi => oi.Ticket != null)
                .Select(oi => new TicketDto
                {
                    Id = oi.Ticket!.Id,
                    OrderItemId = oi.Id,
                    TicketCode = oi.Ticket.TicketCode,
                    QrPayload = _ticketService.GenerateQrPayload(oi.Ticket.TicketCode, showtimeId),
                    EventTitle = order.Showtime?.Event?.Title,
                    EventLocation = order.Showtime?.Event?.Location,
                    ShowtimeStartTime = order.Showtime?.StartTime,
                    ShowtimeEndTime = order.Showtime?.EndTime,
                    CategoryName = oi.Seat?.SeatCategory?.Name,
                    SeatRow = oi.Seat?.Row,
                    SeatNumber = oi.Seat?.SeatNumber ?? 0,
                    Price = oi.Price,
                    CreatedAt = oi.Ticket.CreatedAt
                })
                .ToList();

            return Ok(ApiResponse<List<TicketDto>>.SuccessResult(tickets, "Lấy danh sách vé thành công."));
        }

        private OrderDto MapToDto(Order order)
        {
            return new OrderDto
            {
                Id = order.Id,
                ShowtimeId = order.ShowtimeId,
                EventTitle = order.Showtime?.Event?.Title,
                EventLocation = order.Showtime?.Event?.Location,
                ShowtimeStartTime = order.Showtime?.StartTime,
                ShowtimeEndTime = order.Showtime?.EndTime,
                CustomerName = order.User != null ? (!string.IsNullOrWhiteSpace(order.User.FullName) ? order.User.FullName : order.User.Username) : null,
                CustomerEmail = order.User?.Email,
                Status = order.Status.ToString(),
                TotalAmount = order.TotalAmount,
                CreatedAt = order.CreatedAt,
                ExpiresAt = order.ExpiresAt,
                HasFailedPayment = order.PaymentTransaction != null &&
                    (order.PaymentTransaction.Status == "FAILED" || order.PaymentTransaction.Status == "CANCELLED"),
                IsPaymentProcessing = order.PaymentTransaction != null && order.PaymentTransaction.Status == "PENDING",
                Items = order.OrderItems.Select(oi => new OrderItemDto
                {
                    Id = oi.Id,
                    SeatId = oi.SeatId,
                    Price = oi.Price,
                    SeatName = oi.Seat != null ? $"{oi.Seat.Row}{oi.Seat.SeatNumber}" : null,
                    CategoryName = oi.Seat?.SeatCategory?.Name
                }).ToList()
            };
        }
    }
}


