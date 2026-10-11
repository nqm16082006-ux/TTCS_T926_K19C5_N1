using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/orders")]
    public class OrdersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<OrdersController> _logger;
        private readonly EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue? _ticketEmailQueue;
        private readonly EventTicketBooking.Api.Services.Interfaces.ITicketService _ticketService;
        private readonly EventTicketBooking.Api.Services.Interfaces.IOrderAuditService _orderAuditService;
        private readonly EventTicketBooking.Api.Services.Interfaces.IPaymentGateway? _paymentGateway;
        private readonly RefundPolicyOptions _refundPolicy;

        public OrdersController(
            AppDbContext context,
            ILogger<OrdersController> logger,
            EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue? ticketEmailQueue = null,
            EventTicketBooking.Api.Services.Interfaces.ITicketService? ticketService = null,
            EventTicketBooking.Api.Services.Interfaces.IOrderAuditService? orderAuditService = null,
            EventTicketBooking.Api.Services.Interfaces.IPaymentGateway? paymentGateway = null,
            IOptions<RefundPolicyOptions>? refundPolicy = null)
        {
            _context = context;
            _logger = logger;
            _ticketEmailQueue = ticketEmailQueue;
            _ticketService = ticketService ?? new EventTicketBooking.Api.Services.Implementations.TicketService();
            _orderAuditService = orderAuditService ?? new EventTicketBooking.Api.Services.Implementations.OrderAuditService(
                context, Microsoft.Extensions.Logging.Abstractions.NullLogger<EventTicketBooking.Api.Services.Implementations.OrderAuditService>.Instance);
            _paymentGateway = paymentGateway;
            _refundPolicy = refundPolicy?.Value ?? new RefundPolicyOptions();
        }

        /// <summary>
        /// T-37: Tạo đơn hàng từ giữ chỗ và gia hạn giữ chỗ
        /// </summary>
        [HttpPost("showtimes/{showtimeId:guid}")]
        [TicketRateLimit("order-create")]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
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

            var userDisplayName = User.Identity?.Name ?? User.FindFirst("name")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst("email")?.Value ?? userId.ToString();

            var (success, message, order, statusCode) = await CreateOrderFromHoldsCoreAsync(userId, showtimeId, userDisplayName);
            if (!success)
            {
                return statusCode switch
                {
                    StatusCodes.Status404NotFound => NotFound(ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status409Conflict => Conflict(ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status400BadRequest => BadRequest(ApiResponse<object>.FailureResult(message)),
                    _ => StatusCode(statusCode, ApiResponse<object>.FailureResult(message))
                };
            }

            return Ok(ApiResponse<OrderDto>.SuccessResult(order!, message));
        }

        private async Task<(bool Success, string Message, OrderDto? Order, int StatusCode)> CreateOrderFromHoldsCoreAsync(Guid userId, Guid showtimeId, string userDisplayName)
        {
            // Kiểm tra suất diễn
            var showtime = await _context.Showtimes.Include(s => s.Event).FirstOrDefaultAsync(s => s.Id == showtimeId);
            if (showtime == null)
                return (false, "Không tìm thấy suất diễn.", null, StatusCodes.Status404NotFound);
            if (showtime.Status != ShowtimeStatus.OnSale)
                return (false, "Suất diễn không đang mở bán.", null, StatusCodes.Status409Conflict);

            // 1. Chống bấm đúp: Trả về đơn chờ hiện tại nếu đã có
            var existingOrder = await _context.Orders
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .FirstOrDefaultAsync(o => o.UserId == userId && o.ShowtimeId == showtimeId && o.Status == OrderStatus.Pending);

            if (existingOrder != null)
            {
                if (existingOrder.ExpiresAt <= DateTimeOffset.UtcNow)
                    return (false, "Đơn hàng trước đã hết hạn, vui lòng chờ hệ thống giải phóng và đặt lại.", null, StatusCodes.Status409Conflict);
                return (true, "Đã tồn tại đơn hàng đang chờ thanh toán.", MapToDto(existingOrder), StatusCodes.Status200OK);
            }

            var now = DateTimeOffset.UtcNow;

            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;

            try
            {
                if (_context.Database.IsRelational())
                {
                    transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                }

                // 2. Lấy giữ chỗ (Không nhận danh sách ghế từ client)
                var holds = await _context.SeatHold
                    .Include(sh => sh.Seat)
                    .ThenInclude(s => s.SeatCategory)
                    .Where(sh => sh.UserId == userId && sh.Seat.ShowtimeId == showtimeId && sh.Status == "ACTIVE")
                    .ToListAsync();

                if (!holds.Any())
                {
                    return (false, "Bạn không giữ chỗ nào hoặc giữ chỗ đã hết hạn.", null, StatusCodes.Status400BadRequest);
                }

                // S-42: Kiểm tra giới hạn số lượng vé tối đa mỗi người mua cho suất diễn
                int maxTickets = showtime.MaxTicketsPerUser > 0
                    ? showtime.MaxTicketsPerUser
                    : (showtime.Event != null && showtime.Event.MaxTicketsPerUser > 0 ? showtime.Event.MaxTicketsPerUser : 10);

                var purchasedCount = await _context.OrderItems
                    .AsNoTracking()
                    .CountAsync(oi => oi.Order.ShowtimeId == showtimeId &&
                                      oi.Order.UserId == userId &&
                                      oi.Order.Status != OrderStatus.Cancelled &&
                                      oi.Order.Status != OrderStatus.Expired);

                if (holds.Count + purchasedCount > maxTickets)
                {
                    return (false, $"Bạn không thể đặt thêm vé. Tổng số ghế đang giữ ({holds.Count}) cộng đã mua ({purchasedCount}) của tài khoản vượt giới hạn {maxTickets} vé trong suất này (Tối đa {maxTickets} vé).", null, StatusCodes.Status400BadRequest);
                }

                if (holds.Any(h => h.Seat.Status == "SOLD" || h.Seat.SeatCategory.ShowtimeId != showtimeId || h.Seat.SeatCategory.Price < 0))
                    return (false, "Ghế hoặc giá ghế không còn hợp lệ.", null, StatusCodes.Status409Conflict);
                var totalPrice = holds.Sum(h => (long)(h.Seat.SeatCategory.Price ?? 0));
                if (totalPrice > int.MaxValue)
                    return (false, "Tổng tiền đơn hàng vượt giới hạn thanh toán.", null, StatusCodes.Status400BadRequest);

                var expiredHolds = holds.Where(h => h.ExpiresAt <= now.UtcDateTime).ToList();
                if (expiredHolds.Any())
                {
                    var expiredSeatNames = string.Join(", ", expiredHolds.Select(h => $"{h.Seat.Row}{h.Seat.SeatNumber}"));
                    return (false, $"Các ghế sau đã hết hạn giữ chỗ: {expiredSeatNames}. Vui lòng đặt lại.", null, StatusCodes.Status409Conflict);
                }

                // 3. Tính tổng và tạo đơn hàng (T-38: Tính ở máy chủ)
                var orderItems = new List<OrderItem>();

                // Gia hạn giữ chỗ bằng thời hạn thanh toán (giả sử 15 phút - E-05)
                var paymentExpiry = now.AddMinutes(15);

                foreach (var hold in holds)
                {
                    if (hold.Seat.SeatCategory.Price == null)
                    {
                        return (false, $"Ghế {hold.Seat.Row}{hold.Seat.SeatNumber} chưa được định giá.", null, StatusCodes.Status400BadRequest);
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

                // Story S-49: Ghi nhận nhật ký tạo đơn hàng và giữ chỗ
                _orderAuditService.Record(
                    order.Id,
                    "ORDER",
                    order.Id.ToString(),
                    "ORDER_CREATED",
                    null,
                    OrderStatus.Pending.ToString(),
                    "USER",
                    userDisplayName,
                    userId,
                    $"Tạo đơn hàng gồm {orderItems.Count} vé. Hạn thanh toán: {paymentExpiry:HH:mm:ss dd/MM/yyyy}.");

                foreach (var hold in holds)
                {
                    var seatLabel = hold.Seat != null ? $"{hold.Seat.Row}{hold.Seat.SeatNumber}" : hold.SeatId.ToString();
                    _orderAuditService.Record(
                        order.Id,
                        "SEAT_HOLD",
                        seatLabel,
                        "SEAT_HOLD_ATTACHED",
                        "ACTIVE",
                        "ACTIVE",
                        "USER",
                        userDisplayName,
                        userId,
                        $"Gia hạn giữ chỗ ghế {seatLabel} theo hạn thanh toán đơn hàng ({paymentExpiry:HH:mm:ss}).");
                }

                if (order.TotalAmount == 0)
                {
                    order.Status = OrderStatus.Paid;
                    _orderAuditService.Record(
                        order.Id,
                        "ORDER",
                        order.Id.ToString(),
                        "ORDER_PAID",
                        OrderStatus.Pending.ToString(),
                        OrderStatus.Paid.ToString(),
                        "SYSTEM",
                        "system:ZeroAmountAutoPay",
                        userId,
                        "Đơn hàng 0 VNĐ, hệ thống tự động xác nhận thanh toán.");

                    foreach (var hold in holds)
                    {
                        hold.Status = "CONVERTED";
                        if (hold.Seat != null)
                        {
                            hold.Seat.Status = "SOLD";
                        }
                        var seatLabel = hold.Seat != null ? $"{hold.Seat.Row}{hold.Seat.SeatNumber}" : hold.SeatId.ToString();
                        _orderAuditService.Record(
                            order.Id,
                            "SEAT_HOLD",
                            seatLabel,
                            "SEAT_HOLD_CONVERTED",
                            "ACTIVE",
                            "CONVERTED",
                            "SYSTEM",
                            "system:ZeroAmountAutoPay",
                            userId,
                            $"Ghế {seatLabel} chuyển sang trạng thái đã bán (SOLD).");
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

                        _orderAuditService.Record(
                            order.Id,
                            "TICKET",
                            ticket.TicketCode,
                            "TICKET_ISSUED",
                            "UNISSUED",
                            "ISSUED",
                            "SYSTEM",
                            "system:TicketService",
                            userId,
                            $"Phát hành vé điện tử mã {ticket.TicketCode}.");
                    }
                }

                _context.Orders.Add(order);
                await _context.SaveChangesAsync();

                if (transaction != null)
                {
                    await transaction.CommitAsync();
                }

                return (true, "Tạo đơn hàng thành công.", MapToDto(order), StatusCodes.Status200OK);
            }
            catch (DbUpdateException ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync();
                }
                _context.ChangeTracker.Clear();
                _logger.LogWarning(ex, "Concurrent order creation for User {UserId}", userId);
                return (false, "Trạng thái đặt vé vừa thay đổi. Vui lòng tải lại và thử lại.", null, StatusCodes.Status409Conflict);
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync();
                }
                _logger.LogError(ex, "Lỗi khi tạo đơn hàng từ giữ chỗ cho User {UserId}", userId);
                return (false, "Đã xảy ra lỗi hệ thống.", null, StatusCodes.Status500InternalServerError);
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// T-39: Lấy thông tin tóm tắt đơn hàng (ghế, hạng, đơn giá, tổng tiền, thời gian còn lại)
        /// </summary>
        [HttpGet("{orderId:guid}")]
        [RequireRole]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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
                .Include(o => o.Refunds)
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
        /// S-45: Xem trước điều kiện huỷ và số tiền hoàn dự kiến trước khi khách xác nhận.
        /// GET /api/v1/orders/{orderId}/refund-preview
        /// </summary>
        [HttpGet("{orderId:guid}/refund-preview")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<RefundPreviewDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetRefundPreview(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders
                .AsNoTracking()
                .Include(o => o.Showtime)
                .Include(o => o.OrderItems)
                .Include(o => o.Refunds)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            if (order.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền thao tác trên đơn hàng này."));

            var preview = BuildRefundPreview(order);
            return Ok(ApiResponse<RefundPreviewDto>.SuccessResult(preview, "Lấy thông tin chính sách huỷ và hoàn tiền thành công."));
        }

        /// <summary>
        /// S-45: Khách huỷ đơn đã thanh toán trước hạn và tạo yêu cầu hoàn tiền theo chính sách demo.
        /// POST /api/v1/orders/{orderId}/cancel
        /// </summary>
        [HttpPost("{orderId:guid}/cancel")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CancelOrderAndRefund(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            // ===== Bước 1: Kiểm tra điều kiện và chuyển trạng thái đơn trong transaction ngắn =====
            Refund newRefund;
            Order orderForDto;

            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? dbTransaction = null;
            if (_context.Database.IsRelational() && _context.Database.CurrentTransaction == null)
            {
                dbTransaction = await _context.Database.BeginTransactionAsync();
            }

            try
            {
                // Khóa dòng đơn hàng trên PostgreSQL để chống đua với webhook/check-in (S-45)
                IQueryable<Order> orderQuery = _context.Orders
                    .Include(o => o.Showtime)
                    .Include(o => o.OrderItems)
                    .Include(o => o.Refunds)
                    .Where(o => o.Id == orderId);

                if (_context.Database.IsNpgsql())
                {
                    orderQuery = _context.Orders
                        .FromSqlRaw("SELECT * FROM orders WHERE \"Id\" = {0} FOR UPDATE", orderId)
                        .Include(o => o.Showtime)
                        .Include(o => o.OrderItems)
                        .Include(o => o.Refunds);
                }

                var order = await orderQuery.FirstOrDefaultAsync();

                if (order == null)
                    return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

                if (order.UserId != userId)
                    return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền thao tác trên đơn hàng này."));

                // Kiểm tra lại toàn bộ điều kiện tại thời điểm khách xác nhận (S-45 AC)
                var preview = BuildRefundPreview(order);
                if (!preview.Eligible)
                {
                    return StatusCode(StatusCodes.Status409Conflict, ApiResponse<object>.FailureResult(preview.Message));
                }

                var now = DateTimeOffset.UtcNow;
                var oldStatus = order.Status.ToString();

                order.Status = OrderStatus.Cancelled;
                order.CancelReason = OrderCancelReasons.CustomerRefund;
                order.UpdatedAt = now;

                newRefund = new Refund
                {
                    OrderId = order.Id,
                    Amount = preview.EstimatedRefundAmount,
                    Status = "Requested",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.Refunds.Add(newRefund);

                _orderAuditService.Record(
                    order.Id,
                    "ORDER",
                    order.Id.ToString(),
                    "ORDER_CANCELLED",
                    oldStatus,
                    OrderStatus.Cancelled.ToString(),
                    "USER",
                    $"user:{userId}",
                    userId,
                    "Khách hàng chủ động huỷ đơn sau khi đã thanh toán (S-45).");

                _orderAuditService.Record(
                    order.Id,
                    "REFUND",
                    newRefund.Id.ToString(),
                    "REFUND_REQUESTED",
                    null,
                    "Requested",
                    "USER",
                    $"user:{userId}",
                    userId,
                    $"Tạo yêu cầu hoàn tiền {preview.EstimatedRefundAmount} VND theo chính sách demo {preview.RefundPercent}% (chưa Product duyệt).");

                await _context.SaveChangesAsync();

                if (dbTransaction != null)
                {
                    await dbTransaction.CommitAsync();
                }

                orderForDto = order;
            }
            finally
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.DisposeAsync();
                }
            }

            // ===== Bước 2: Gọi cổng hoàn tiền NGOÀI transaction (không giữ khóa DB khi chờ mạng) =====
            RefundCreationResult gatewayResult;
            bool gatewaySupportsRefund = _paymentGateway != null && _paymentGateway.SupportsRefund;

            if (_paymentGateway == null || !gatewaySupportsRefund)
            {
                gatewayResult = RefundCreationResult.CreateManualRequired(
                    "Cổng thanh toán hiện tại không hỗ trợ hoàn tiền tự động. Yêu cầu được ghi nhận chờ xử lý thủ công.");
            }
            else
            {
                try
                {
                    var paymentTx = await _context.PaymentTransactions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(pt => pt.OrderId == orderId);

                    gatewayResult = await _paymentGateway.CreateRefundAsync(new RefundCreationRequest
                    {
                        OrderId = orderId,
                        OrderCode = paymentTx?.OrderCode ?? 0,
                        Amount = newRefund.Amount,
                        Description = $"Hoan tien don {orderId}"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi gọi cổng hoàn tiền cho đơn hàng {OrderId} (S-45)", orderId);
                    gatewayResult = RefundCreationResult.CreateFailure(
                        "Không liên lạc được với cổng thanh toán. Yêu cầu hoàn tiền đang chờ xử lý.");
                }
            }

            // ===== Bước 3: Cập nhật trạng thái yêu cầu hoàn tiền =====
            // Không bao giờ ghi Succeeded nếu cổng không xác nhận thành công.
            var refundEntity = await _context.Refunds.FirstAsync(r => r.OrderId == orderId);
            refundEntity.UpdatedAt = DateTimeOffset.UtcNow;

            string refundNewStatus;
            string auditAction;
            string responseMessage;

            if (gatewayResult.Success)
            {
                refundEntity.Status = "Succeeded";
                refundNewStatus = "Succeeded";
                auditAction = "REFUND_SUCCEEDED";
                responseMessage = $"Đơn hàng đã được huỷ. Hoàn tiền {refundEntity.Amount} VND thành công (mô phỏng qua cổng {(_paymentGateway?.ProviderName ?? "Mock")}).";
            }
            else if (!gatewaySupportsRefund || gatewayResult.ManualRequired)
            {
                refundEntity.Status = "ManualPending";
                refundNewStatus = "ManualPending";
                auditAction = "REFUND_MANUAL_PENDING";
                refundEntity.FailureReason = gatewayResult.ErrorMessage;
                responseMessage = "Đơn hàng đã được huỷ. Yêu cầu hoàn tiền đang chờ xử lý thủ công (cổng thanh toán không hỗ trợ hoàn tự động).";
            }
            else
            {
                refundEntity.Status = "Failed";
                refundNewStatus = "Failed";
                auditAction = "REFUND_FAILED";
                refundEntity.FailureReason = gatewayResult.ErrorMessage ?? "Cổng thanh toán trả về kết quả hoàn tiền thất bại.";
                responseMessage = "Đơn hàng đã được huỷ nhưng hoàn tiền THẤT BẠI — cần xử lý tiếp. Vui lòng liên hệ hỗ trợ.";
            }

            _orderAuditService.Record(
                orderId,
                "REFUND",
                refundEntity.Id.ToString(),
                auditAction,
                "Requested",
                refundNewStatus,
                "SYSTEM",
                $"gateway:{(_paymentGateway?.ProviderName ?? "none")}",
                null,
                refundEntity.FailureReason ?? $"Hoàn tiền {refundEntity.Amount} VND.");

            await _context.SaveChangesAsync();

            _logger.LogInformation("S-45: Đơn hàng {OrderId} đã huỷ; yêu cầu hoàn tiền {RefundId} ở trạng thái {Status}.", orderId, refundEntity.Id, refundEntity.Status);

            return Ok(new ApiResponse<object>
            {
                Success = true,
                Message = responseMessage,
                Data = new
                {
                    order = MapToDto(orderForDto),
                    refund = MapRefundToDto(refundEntity)
                }
            });
        }

        /// <summary>
        /// S-45: Tính điều kiện huỷ và số tiền hoàn dự kiến (server là nguồn duy nhất quyết định số tiền).
        /// CHÚ Ý: 24 giờ và 90% là chính sách DEMO chưa Product phê duyệt.
        /// </summary>
        private RefundPreviewDto BuildRefundPreview(Order order)
        {
            int percent = Math.Clamp(_refundPolicy.RefundPercent, 0, 100);
            var preview = new RefundPreviewDto
            {
                OrderId = order.Id,
                TotalAmount = order.TotalAmount,
                EstimatedRefundAmount = (int)Math.Floor(order.TotalAmount * (decimal)percent / 100m),
                CutoffHours = _refundPolicy.CutoffHours,
                RefundPercent = percent,
                ShowtimeStartTime = order.Showtime?.StartTime,
                Reason = "OK",
                Message = "Đơn hàng đủ điều kiện huỷ và hoàn tiền."
            };

            // 1. Đơn đã có yêu cầu hoàn tiền → không tạo trùng (chống bấm lặp)
            if (order.Refunds != null && order.Refunds.Any())
            {
                preview.Eligible = false;
                preview.Reason = "DUPLICATE_REFUND";
                preview.Message = "Đơn hàng đã có yêu cầu hoàn tiền trước đó. Không thể huỷ hoặc hoàn tiền thêm.";
                return preview;
            }

            // 2. Chỉ đơn đã thanh toán (Paid) được huỷ theo luồng này
            if (order.Status != OrderStatus.Paid)
            {
                preview.Eligible = false;
                preview.Reason = order.Status == OrderStatus.Cancelled ? "ALREADY_CANCELLED"
                    : order.Status == OrderStatus.Expired ? "ORDER_EXPIRED"
                    : "NOT_PAID";
                preview.Message = preview.Reason switch
                {
                    "ALREADY_CANCELLED" => "Đơn hàng đã được huỷ trước đó.",
                    "ORDER_EXPIRED" => "Đơn hàng đã hết hạn, không thể huỷ theo luồng hoàn tiền.",
                    _ => $"Đơn hàng không ở trạng thái chờ huỷ (trạng thái hiện tại: {order.Status}). Chỉ đơn đã thanh toán mới được huỷ và hoàn tiền."
                };
                return preview;
            }

            // 3. Không huỷ được nếu bất kỳ vé nào trong đơn đã check-in
            if (order.OrderItems != null && order.OrderItems.Any(oi => oi.IsCheckedIn))
            {
                preview.Eligible = false;
                preview.Reason = "TICKET_ALREADY_CHECKED_IN";
                preview.Message = "Đơn hàng có vé đã sử dụng để vào cửa. Không thể huỷ và hoàn tiền.";
                return preview;
            }

            // 4. Phải trước hạn chót: giờ chiếu - CutoffHours (chính sách demo)
            if (order.Showtime == null)
            {
                preview.Eligible = false;
                preview.Reason = "NO_SHOWTIME";
                preview.Message = "Không tìm thấy thông tin suất chiếu của đơn hàng.";
                return preview;
            }

            var startTime = order.Showtime.StartTime.Kind == DateTimeKind.Utc
                ? order.Showtime.StartTime
                : DateTime.SpecifyKind(order.Showtime.StartTime, DateTimeKind.Utc);
            var refundDeadline = new DateTimeOffset(startTime).AddHours(-_refundPolicy.CutoffHours);
            preview.RefundDeadline = refundDeadline;

            if (DateTimeOffset.UtcNow > refundDeadline)
            {
                preview.Eligible = false;
                preview.Reason = "AFTER_CUTOFF";
                preview.Message = $"Đã quá hạn huỷ vé. Hạn chót là trước giờ chiếu {_refundPolicy.CutoffHours} giờ (UTC: {refundDeadline:dd/MM/yyyy HH:mm}).";
                return preview;
            }

            preview.Eligible = true;
            return preview;
        }

        private static RefundDto MapRefundToDto(Refund refund) => new()
        {
            Id = refund.Id,
            OrderId = refund.OrderId,
            Amount = refund.Amount,
            Status = refund.Status,
            FailureReason = refund.FailureReason,
            CreatedAt = refund.CreatedAt,
            UpdatedAt = refund.UpdatedAt
        };

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

            if (_ticketEmailQueue != null)
            {
                await _ticketEmailQueue.EnqueueAsync(orderId);
            }

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

        /// <summary>
        /// Story S-49: Xem nhật ký thao tác và thay đổi trạng thái của đơn hàng, vé và giữ chỗ
        /// GET /api/v1/orders/{orderId}/audit-logs?entityType=...
        /// </summary>
        [HttpGet("{orderId:guid}/audit-logs")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<List<OrderAuditLogDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetOrderAuditLogs(Guid orderId, [FromQuery] string? entityType = null)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            var isPrivileged = User.IsInRole("Admin") || User.IsInRole("Auditor") || User.IsInRole("Staff") || User.IsInRole("Organizer")
                || User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && (c.Value == "Admin" || c.Value == "Auditor" || c.Value == "Staff" || c.Value == "Organizer"));

            if (!isPrivileged && order.UserId != userId)
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập nhật ký của đơn hàng này."));

            var logs = await _orderAuditService.GetLogsByOrderIdAsync(orderId, entityType, HttpContext.RequestAborted);
            return Ok(ApiResponse<List<OrderAuditLogDto>>.SuccessResult(logs, "Lấy nhật ký thao tác đơn hàng thành công."));
        }

        /// <summary>
        /// S-32: Xem lịch sử đơn hàng của tôi (có phân trang, sắp xếp mới nhất trước)
        /// GET /api/v1/orders
        /// GET /api/v1/orders/my-orders
        /// </summary>
        [HttpGet]
        [HttpGet("my-orders")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<PagedResultDto<OrderDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetMyOrders(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? status = null)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 10;
            if (pageSize > 50) pageSize = 50;

            // Tự động chuyển các giữ chỗ còn hiệu lực chưa tạo đơn thành đơn Pending
            try
            {
                var nowUtc = DateTime.UtcNow;
                var activeHoldShowtimeIds = await _context.SeatHold
                    .AsNoTracking()
                    .Where(sh => sh.UserId == userId && sh.Status == "ACTIVE" && sh.ExpiresAt > nowUtc)
                    .Select(sh => sh.Seat.ShowtimeId)
                    .Distinct()
                    .ToListAsync();

                if (activeHoldShowtimeIds.Any())
                {
                    var userDisplayName = User.Identity?.Name ?? User.FindFirst("name")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst("email")?.Value ?? userId.ToString();
                    foreach (var stId in activeHoldShowtimeIds)
                    {
                        var hasPendingOrder = await _context.Orders
                            .AnyAsync(o => o.UserId == userId && o.ShowtimeId == stId && o.Status == OrderStatus.Pending);
                        if (!hasPendingOrder)
                        {
                            await CreateOrderFromHoldsCoreAsync(userId, stId, userDisplayName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi đồng bộ giữ chỗ sang đơn hàng cho user {UserId}", userId);
            }

            var query = _context.Orders
                .AsNoTracking()
                .Where(o => o.UserId == userId);

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<OrderStatus>(status, true, out var parsedStatus))
                {
                    query = query.Where(o => o.Status == parsedStatus);
                }
            }

            var totalItems = await query.CountAsync();

            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(o => o.User)
                .Include(o => o.Showtime)
                    .ThenInclude(st => st.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .Include(o => o.PaymentTransaction)
                .Include(o => o.Refunds)
                .ToListAsync();

            var orderDtos = orders.Select(MapToDto).ToList();

            var result = new PagedResultDto<OrderDto>
            {
                Items = orderDtos,
                Page = page,
                PageSize = pageSize,
                TotalItems = totalItems
            };

            return Ok(ApiResponse<PagedResultDto<OrderDto>>.SuccessResult(result, "Lấy lịch sử đơn hàng thành công."));
        }

        private OrderDto MapToDto(Order order)
        {
            var seatNames = order.OrderItems?
                .Where(oi => oi.Seat != null)
                .Select(oi => $"{oi.Seat.Row}{oi.Seat.SeatNumber}")
                .ToList() ?? new List<string>();

            // Story S-45: hiển thị trạng thái hoàn tiền (bản ghi mới nhất nếu có)
            var latestRefund = order.Refunds?.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();

            return new OrderDto
            {
                Id = order.Id,
                ShowtimeId = order.ShowtimeId,
                EventId = order.Showtime?.EventId,
                EventTitle = order.Showtime?.Event?.Title,
                EventLocation = order.Showtime?.Event?.Location,
                EventImageUrl = order.Showtime?.Event?.ImageUrl,
                ShowtimeStartTime = order.Showtime?.StartTime,
                ShowtimeEndTime = order.Showtime?.EndTime,
                CustomerName = order.User != null ? (!string.IsNullOrWhiteSpace(order.User.FullName) ? order.User.FullName : order.User.Username) : null,
                CustomerEmail = order.User?.Email,
                Status = order.Status.ToString(),
                TotalAmount = order.TotalAmount,
                SeatCount = order.OrderItems?.Count ?? 0,
                SeatNames = seatNames,
                CreatedAt = order.CreatedAt,
                ExpiresAt = order.ExpiresAt,
                HasFailedPayment = order.PaymentTransaction != null &&
                    (order.PaymentTransaction.Status == "FAILED" || order.PaymentTransaction.Status == "CANCELLED"),
                IsPaymentProcessing = order.PaymentTransaction != null && order.PaymentTransaction.Status == "PENDING",
                CancelReason = order.CancelReason,
                RefundStatus = latestRefund?.Status,
                RefundAmount = latestRefund?.Amount,
                RefundFailureReason = latestRefund?.FailureReason,
                RefundUpdatedAt = latestRefund?.UpdatedAt,
                Items = order.OrderItems?.Select(oi => new OrderItemDto
                {
                    Id = oi.Id,
                    SeatId = oi.SeatId,
                    Price = oi.Price,
                    SeatName = oi.Seat != null ? $"{oi.Seat.Row}{oi.Seat.SeatNumber}" : null,
                    CategoryName = oi.Seat?.SeatCategory?.Name
                }).ToList() ?? new List<OrderItemDto>()
            };
        }
    }
}

