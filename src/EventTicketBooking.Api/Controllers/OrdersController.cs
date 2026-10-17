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

using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using EventTicketBooking.Api.Hubs;
using EventTicketBooking.Api.Services;
using Order = EventTicketBooking.Api.Models.Order;

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
        private readonly EventTicketBooking.Api.Services.Interfaces.IRefundService? _refundService;
        private readonly RefundPolicyOptions _refundPolicy;
        private readonly IHubContext<SeatStatusHub>? _hubContext;
        private readonly IConnectionMultiplexer? _redis;
        private readonly EventTicketBooking.Api.Services.Interfaces.IDiscountCodeService _discountCodeService;

        public OrdersController(
            AppDbContext context,
            ILogger<OrdersController> logger,
            EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue? ticketEmailQueue = null,
            EventTicketBooking.Api.Services.Interfaces.ITicketService? ticketService = null,
            EventTicketBooking.Api.Services.Interfaces.IOrderAuditService? orderAuditService = null,
            EventTicketBooking.Api.Services.Interfaces.IPaymentGateway? paymentGateway = null,
            IOptions<RefundPolicyOptions>? refundPolicy = null,
            IHubContext<SeatStatusHub>? hubContext = null,
            IConnectionMultiplexer? redis = null,
            EventTicketBooking.Api.Services.Interfaces.IRefundService? refundService = null,
            EventTicketBooking.Api.Services.Interfaces.IDiscountCodeService? discountCodeService = null)
        {
            _context = context;
            _logger = logger;
            _ticketEmailQueue = ticketEmailQueue;
            _ticketService = ticketService ?? new EventTicketBooking.Api.Services.Implementations.TicketService();
            _orderAuditService = orderAuditService ?? new EventTicketBooking.Api.Services.Implementations.OrderAuditService(
                context, Microsoft.Extensions.Logging.Abstractions.NullLogger<EventTicketBooking.Api.Services.Implementations.OrderAuditService>.Instance);
            _paymentGateway = paymentGateway;
            _refundPolicy = refundPolicy?.Value ?? new RefundPolicyOptions();
            _hubContext = hubContext;
            _redis = redis;
            _refundService = refundService ?? new EventTicketBooking.Api.Services.Implementations.RefundService(
                context, _orderAuditService, paymentGateway, Microsoft.Extensions.Logging.Abstractions.NullLogger<EventTicketBooking.Api.Services.Implementations.RefundService>.Instance, hubContext, redis);
            _discountCodeService = discountCodeService ?? new EventTicketBooking.Api.Services.Implementations.DiscountCodeService(context);
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
            if (showtime.IsSaleExpired(DateTime.UtcNow))
                return (false, "Suất diễn đã hết thời gian bán vé.", null, StatusCodes.Status409Conflict);
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
                var showEnd = new DateTimeOffset(DateTime.SpecifyKind(showtime.EndTime, DateTimeKind.Utc));
                var paymentExpiry = now.AddMinutes(15) < showEnd ? now.AddMinutes(15) : showEnd;

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
                    OrderItems = orderItems,
                    OriginalAmount = (int)totalPrice,
                    DiscountAmount = 0,
                    TotalAmount = (int)totalPrice
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
                .Include(o => o.DiscountCode)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            // T-39 & S-48: Chủ đơn hoặc Auditor/Admin mở được
            var isPrivileged = User.IsInRole("Admin") || User.IsInRole("Auditor") ||
                               User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && (c.Value == "Admin" || c.Value == "Auditor"));
            if (!isPrivileged && order.UserId != userId)
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Order", orderId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập đơn hàng này."));
            }

            return Ok(ApiResponse<OrderDto>.SuccessResult(MapToDto(order, isPrivileged), "Lấy thông tin đơn hàng thành công."));
        }

        /// <summary>
        /// Story S-48: Lấy thông tin chi tiết lệnh hoàn tiền của đơn hàng.
        /// Khách hàng chỉ xem được đơn của mình dạng customer-safe (CustomerRefundDto).
        /// Admin/Auditor xem được mọi đơn với đầy đủ thông số kỹ thuật (RefundDetailDto).
        /// </summary>
        [HttpGet("{orderId:guid}/refund")]
        [RequireRole]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetOrderRefund(Guid orderId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var order = await _context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));

            var isPrivileged = User.IsInRole("Admin") || User.IsInRole("Auditor") ||
                               User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && (c.Value == "Admin" || c.Value == "Auditor"));

            if (!isPrivileged && order.UserId != userId)
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "OrderRefund", orderId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập thông tin hoàn tiền của đơn hàng này."));
            }

            var refundReq = await _context.RefundRequests.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == orderId);
            if (refundReq == null)
            {
                var legacyRefund = await _context.Refunds.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == orderId);
                if (legacyRefund == null)
                    return NotFound(ApiResponse<object>.FailureResult("Đơn hàng này chưa có yêu cầu hoàn tiền."));

                if (!isPrivileged)
                {
                    return Ok(ApiResponse<CustomerRefundDto>.SuccessResult(new CustomerRefundDto
                    {
                        Id = legacyRefund.Id,
                        OrderId = legacyRefund.OrderId,
                        Amount = legacyRefund.Amount,
                        Status = legacyRefund.Status,
                        CustomerStatusText = MapToCustomerStatusText(legacyRefund.Status),
                        CreatedAt = legacyRefund.CreatedAt,
                        UpdatedAt = legacyRefund.UpdatedAt
                    }, "Lấy thông tin hoàn tiền thành công."));
                }

                return Ok(ApiResponse<RefundDetailDto>.SuccessResult(new RefundDetailDto
                {
                    Id = legacyRefund.Id,
                    OrderId = legacyRefund.OrderId,
                    Amount = legacyRefund.Amount,
                    Status = legacyRefund.Status,
                    CreatedAt = legacyRefund.CreatedAt,
                    UpdatedAt = legacyRefund.UpdatedAt
                }, "Lấy thông tin hoàn tiền thành công."));
            }

            if (!isPrivileged)
            {
                return Ok(ApiResponse<CustomerRefundDto>.SuccessResult(new CustomerRefundDto
                {
                    Id = refundReq.Id,
                    OrderId = refundReq.OrderId,
                    Amount = refundReq.Amount,
                    Currency = refundReq.Currency,
                    Status = refundReq.Status.ToString(),
                    CustomerStatusText = MapToCustomerStatusText(refundReq.Status.ToString()),
                    CreatedAt = refundReq.CreatedAt,
                    UpdatedAt = refundReq.UpdatedAt
                }, "Lấy thông tin hoàn tiền thành công."));
            }

            var dto = new RefundDetailDto
            {
                Id = refundReq.Id,
                OrderId = refundReq.OrderId,
                OrderCode = refundReq.OrderCode,
                TransactionId = refundReq.TransactionId,
                RefundTransactionId = refundReq.RefundTransactionId,
                Amount = refundReq.Amount,
                Currency = refundReq.Currency,
                Reason = refundReq.Reason,
                Status = refundReq.Status.ToString(),
                AttemptCount = refundReq.AttemptCount,
                RetryCount = refundReq.RetryCount,
                MaxRetries = refundReq.MaxRetries,
                LastAttemptedAt = refundReq.LastAttemptedAt,
                NextRetryAt = refundReq.NextRetryAt,
                FailedAt = refundReq.FailedAt,
                ErrorCode = refundReq.ErrorCode,
                ErrorMessage = refundReq.ErrorMessage,
                IsRetryable = refundReq.IsRetryable,
                AccountingNotified = refundReq.AccountingNotified,
                AccountingNotifiedAt = refundReq.AccountingNotifiedAt,
                AccountingNotificationError = refundReq.AccountingNotificationError,
                CreatedAt = refundReq.CreatedAt,
                UpdatedAt = refundReq.UpdatedAt
            };

            return Ok(ApiResponse<RefundDetailDto>.SuccessResult(dto, "Lấy thông tin hoàn tiền thành công."));
        }

        private static string MapToCustomerStatusText(string? status) => status switch
        {
            "Succeeded" or "AccountingResolved" => "Đã hoàn tiền",
            "RetryScheduled" => "Đang thử lại",
            "ManualReviewRequired" or "AccountingProcessing" or "RequiresManualReview" or "ManualPending" => "Cần bộ phận hỗ trợ kiểm tra",
            "Pending" or "Processing" or "Requested" => "Đang xử lý hoàn tiền",
            _ => "Đang xử lý"
        };

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
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "Order", orderId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập đơn hàng này."));
            }

            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";

            return Ok(ApiResponse<OrderStatusResponseDto>.SuccessResult(new OrderStatusResponseDto { Status = order.Status.ToString() }, "Lấy trạng thái thành công."));
        }

        /// <summary>
        /// S-56: Áp dụng mã giảm giá theo sự kiện cho đơn hàng chờ thanh toán.
        /// POST /api/v1/orders/{orderId}/apply-discount
        /// </summary>
        [HttpPost("{orderId:guid}/apply-discount")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<OrderDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ApplyDiscount(Guid orderId, [FromBody] EventTicketBooking.Api.DTOs.DiscountCodes.ApplyDiscountRequest request)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var (success, message, orderDto, statusCode) = await _discountCodeService.ApplyDiscountCodeAsync(orderId, request?.Code ?? string.Empty, userId);
            if (!success)
            {
                return statusCode switch
                {
                    StatusCodes.Status404NotFound => NotFound(ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status403Forbidden => StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status400BadRequest => BadRequest(ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status409Conflict => Conflict(ApiResponse<object>.FailureResult(message)),
                    _ => StatusCode(statusCode, ApiResponse<object>.FailureResult(message))
                };
            }

            return Ok(ApiResponse<OrderDto>.SuccessResult(orderDto!, message));
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
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "OrderRefundPreview", orderId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền thao tác trên đơn hàng này."));
            }

            var preview = BuildRefundPreview(order);
            return Ok(ApiResponse<RefundPreviewDto>.SuccessResult(preview, "Lấy thông tin chính sách huỷ và hoàn tiền thành công."));
        }

        /// <summary>
        /// S-45 & S-48: Khách huỷ đơn đã thanh toán trước hạn và tạo yêu cầu hoàn tiền.
        /// Hỗ trợ cờ simulateFailure để mô phỏng hoàn tiền thất bại và chuyển tiếp kế toán (Story S-48).
        /// POST /api/v1/orders/{orderId}/cancel
        /// </summary>
        [HttpPost("{orderId:guid}/cancel")]
        [RequireRole]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CancelOrderAndRefund(
            Guid orderId,
            [FromQuery] bool simulateFailure = false,
            [FromBody] CancelOrderRequestDto? body = null)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("id")?.Value ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            var userDisplayName = User.Identity?.Name ?? User.FindFirst("name")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst("email")?.Value ?? userId.ToString();
            var isStaffOrAdmin = User.IsInRole("Admin") || User.IsInRole("Auditor") ||
                                 User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && (c.Value == "Admin" || c.Value == "Auditor"));

            bool isSimulateFailure = simulateFailure || (body?.SimulateFailure == true);

            // Xử lý kịch bản giả lập lỗi hoàn tiền Story S-48
            if (isSimulateFailure && _refundService != null)
            {
                var simResult = await _refundService.SimulateRefundFailureForOrderAsync(
                    orderId,
                    "USER",
                    userDisplayName,
                    userId,
                    HttpContext.RequestAborted);

                if (!simResult.Success && simResult.Message == "Không tìm thấy đơn hàng.")
                {
                    return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy đơn hàng."));
                }

                var updatedOrder = await _context.Orders
                    .AsNoTracking()
                    .Include(o => o.Showtime)
                        .ThenInclude(s => s.Event)
                    .Include(o => o.OrderItems)
                        .ThenInclude(oi => oi.Seat)
                    .Include(o => o.Refunds)
                    .Include(o => o.PaymentTransaction)
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                var simRefundEntity = await _context.Refunds.FirstOrDefaultAsync(r => r.OrderId == orderId);

                return Ok(new ApiResponse<object>
                {
                    Success = true,
                    Message = isStaffOrAdmin ? simResult.Message : "Đơn hàng đã được huỷ. Yêu cầu hoàn tiền đang được hệ thống xử lý.",
                    Data = isStaffOrAdmin
                        ? (object)new
                        {
                            order = updatedOrder != null ? MapToDto(updatedOrder, isStaffOrAdmin) : null,
                            refund = simRefundEntity != null ? MapRefundToDto(simRefundEntity, isStaffOrAdmin) : null,
                            retryCount = simResult.RetryCount,
                            isManualReview = simResult.IsManualReview
                        }
                        : new
                        {
                            order = updatedOrder != null ? MapToDto(updatedOrder, false) : null,
                            refund = simRefundEntity != null ? MapRefundToDto(simRefundEntity, false) : null
                        }
                });
            }

            // ===== Bước 1: Kiểm tra điều kiện và chuyển trạng thái đơn trong transaction ngắn =====
            Refund newRefund;
            Order orderForDto;
            List<Seat> seatsToRelease = new();

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
                {
                    SecurityAuditLogger.LogOwnershipDenied(HttpContext, "OrderCancel", orderId.ToString());
                    return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền thao tác trên đơn hàng này."));
                }

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

                // ===== Story S-46: Ghế của vé đã huỷ quay lại kho bán =====
                var seatIds = order.OrderItems != null
                    ? order.OrderItems.Select(oi => oi.SeatId).Distinct().ToList()
                    : new List<Guid>();

                seatsToRelease = await _context.Seats
                    .Where(s => seatIds.Contains(s.Id))
                    .ToListAsync();

                foreach (var seat in seatsToRelease)
                {
                    var oldSeatStatus = seat.Status;
                    seat.Status = "AVAILABLE";

                    var seatLabel = $"{seat.Row}{seat.SeatNumber}";
                    _orderAuditService.Record(
                        order.Id,
                        "SEAT",
                        seat.Id.ToString(),
                        "SEAT_RELEASED",
                        oldSeatStatus,
                        "AVAILABLE",
                        "USER",
                        $"user:{userId}",
                        userId,
                        $"Ghế {seatLabel} của vé đã huỷ quay lại kho bán (S-46).");
                }

                // Cập nhật các bản ghi giữ chỗ liên quan sang RELEASED
                var holdsToRelease = await _context.SeatHold
                    .Where(sh => seatIds.Contains(sh.SeatId) && (sh.Status == "CONVERTED" || sh.Status == "ACTIVE"))
                    .ToListAsync();

                foreach (var hold in holdsToRelease)
                {
                    var oldHoldStatus = hold.Status;
                    hold.Status = "RELEASED";

                    _orderAuditService.Record(
                        order.Id,
                        "SEAT_HOLD",
                        hold.SeatId.ToString(),
                        "SEAT_HOLD_RELEASED",
                        oldHoldStatus,
                        "RELEASED",
                        "USER",
                        $"user:{userId}",
                        userId,
                        "Giải phóng giữ chỗ do vé đã huỷ quay lại kho bán (S-46).");
                }

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

            // Story S-46: Dọn dẹp cache Redis phân tán nếu có
            if (_redis != null && _redis.IsConnected && seatsToRelease.Count > 0)
            {
                try
                {
                    var redisDb = _redis.GetDatabase();
                    var eventId = orderForDto.Showtime?.EventId;
                    var keys = new List<RedisKey>();
                    foreach (var s in seatsToRelease)
                    {
                        if (eventId.HasValue)
                        {
                            keys.Add((RedisKey)$"seat_hold:{eventId.Value}:{s.Id}");
                        }
                        keys.Add((RedisKey)$"seat_hold:{s.Id}");
                    }
                    if (keys.Count > 0)
                    {
                        await redisDb.KeyDeleteAsync(keys.ToArray());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi xóa Redis hold keys cho đơn hàng {OrderId} (S-46)", orderId);
                }
            }

            // Story S-46: Phát thông báo SignalR SeatReleased để sơ đồ ghế cập nhật thời gian thực
            if (_hubContext != null && _hubContext.Clients != null && seatsToRelease.Count > 0)
            {
                try
                {
                    var groupClient = _hubContext.Clients.Group(orderForDto.ShowtimeId.ToString());
                    if (groupClient != null)
                    {
                        await groupClient.SendAsync("SeatReleased", new
                        {
                            ShowtimeId = orderForDto.ShowtimeId,
                            SeatIds = seatsToRelease.Select(s => s.Id).ToList()
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi gửi SignalR SeatReleased cho suất chiếu {ShowtimeId} (S-46)", orderForDto.ShowtimeId);
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

            // ===== Bước 3: Cập nhật trạng thái yêu cầu hoàn tiền và đồng bộ cơ chế Retry/Kế toán (Story S-48) =====
            var refundEntity = await _context.Refunds.FirstAsync(r => r.OrderId == orderId);
            refundEntity.UpdatedAt = DateTimeOffset.UtcNow;

            string refundNewStatus;
            string auditAction;
            string responseMessage;

            var refundReq = await _context.RefundRequests.FirstOrDefaultAsync(r => r.OrderId == orderId);
            long paymentOrderCode = orderForDto.PaymentTransaction?.OrderCode ??
                Math.Abs(BitConverter.ToInt64(orderId.ToByteArray(), 0) % 9000000000L) + 1000000000L;

            if (gatewayResult.Success)
            {
                refundEntity.Status = "Succeeded";
                refundNewStatus = "Succeeded";
                auditAction = "REFUND_SUCCEEDED";
                responseMessage = isStaffOrAdmin
                    ? $"Đơn hàng đã được huỷ. Hoàn tiền {refundEntity.Amount} VND thành công (mô phỏng qua cổng {(_paymentGateway?.ProviderName ?? "Mock")})."
                    : $"Đơn hàng đã được huỷ và hoàn tiền {refundEntity.Amount:N0} VND thành công.";

                if (refundReq == null)
                {
                    refundReq = new RefundRequest
                    {
                        Id = Guid.NewGuid(),
                        OrderId = orderId,
                        OrderCode = paymentOrderCode,
                        TransactionId = orderForDto.PaymentTransaction?.TransactionId,
                        RefundTransactionId = gatewayResult.RefundReference ?? $"REF_{paymentOrderCode}",
                        Amount = refundEntity.Amount,
                        Currency = "VND",
                        Reason = "CustomerCancelAndRefund",
                        Status = RefundStatus.Succeeded,
                        AttemptCount = 1,
                        RetryCount = 0,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    _context.RefundRequests.Add(refundReq);
                }
                else
                {
                    refundReq.Status = RefundStatus.Succeeded;
                    refundReq.RefundTransactionId = gatewayResult.RefundReference ?? refundReq.RefundTransactionId;
                    refundReq.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                bool isRetryable = gatewayResult.IsRetryable && gatewaySupportsRefund && !gatewayResult.ManualRequired;

                if (refundReq == null)
                {
                    refundReq = new RefundRequest
                    {
                        Id = Guid.NewGuid(),
                        OrderId = orderId,
                        OrderCode = paymentOrderCode,
                        TransactionId = orderForDto.PaymentTransaction?.TransactionId,
                        Amount = refundEntity.Amount,
                        Currency = "VND",
                        Reason = "CustomerCancelAndRefund",
                        Status = RefundStatus.Pending,
                        AttemptCount = 1,
                        RetryCount = 1,
                        MaxRetries = 3,
                        ErrorCode = gatewayResult.ErrorCode ?? "GATEWAY_ERROR",
                        ErrorMessage = gatewayResult.ErrorMessage ?? "Cổng thanh toán từ chối hoàn tiền.",
                        IsRetryable = isRetryable,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    _context.RefundRequests.Add(refundReq);
                }
                else
                {
                    refundReq.AttemptCount++;
                    refundReq.RetryCount++;
                    refundReq.ErrorCode = gatewayResult.ErrorCode ?? "GATEWAY_ERROR";
                    refundReq.ErrorMessage = gatewayResult.ErrorMessage ?? "Cổng thanh toán từ chối hoàn tiền.";
                    refundReq.IsRetryable = isRetryable;
                    refundReq.UpdatedAt = DateTimeOffset.UtcNow;
                }

                if (!gatewaySupportsRefund || gatewayResult.ManualRequired)
                {
                    refundReq.Status = RefundStatus.ManualReviewRequired;
                    refundReq.NextRetryAt = null;

                    refundEntity.Status = "ManualPending";
                    refundNewStatus = "ManualPending";
                    auditAction = "REFUND_MANUAL_PENDING";
                    refundEntity.FailureReason = gatewayResult.ErrorMessage ?? "Cổng thanh toán không hỗ trợ hoàn tự động - Chờ xử lý thủ công.";
                    responseMessage = isStaffOrAdmin
                        ? "Đơn hàng đã được huỷ. Yêu cầu hoàn tiền đang chờ xử lý thủ công (cổng thanh toán không hỗ trợ hoàn tự động)."
                        : "Đơn hàng đã được huỷ. Yêu cầu hoàn tiền đang chờ xử lý thủ công (chờ bộ phận hỗ trợ kiểm tra).";

                    var accountingNotifService = HttpContext?.RequestServices?.GetService<EventTicketBooking.Api.Services.Interfaces.IAccountingNotificationService>();
                    if (accountingNotifService != null)
                    {
                        await accountingNotifService.NotifyFailedRefundAsync(refundReq);
                    }
                }
                else if (isRetryable && refundReq.RetryCount < 3)
                {
                    // Lần 1 hoặc 2 thất bại: Lên lịch retry với khoảng cách (1 phút cho lần 1)
                    var delay = TimeSpan.FromMinutes(1);
                    refundReq.Status = RefundStatus.RetryScheduled;
                    refundReq.NextRetryAt = DateTimeOffset.UtcNow.Add(delay);

                    refundEntity.Status = "Failed";
                    refundNewStatus = "Failed";
                    auditAction = "REFUND_RETRY_SCHEDULED";
                    refundEntity.FailureReason = $"Hoàn tiền thất bại lần {refundReq.RetryCount}/3 ({refundReq.ErrorMessage}). Hệ thống tự động thử lại vào {refundReq.NextRetryAt:HH:mm:ss}.";
                    responseMessage = isStaffOrAdmin
                        ? $"Đơn hàng đã được huỷ nhưng hoàn tiền THẤT BẠI tạm thời ({refundReq.ErrorMessage}), hệ thống đã lên lịch tự động thử lại lần {refundReq.RetryCount}/3."
                        : "Đơn hàng đã được huỷ nhưng hoàn tiền THẤT BẠI tạm thời. Hệ thống đang tự động xếp lịch xử lý lại.";
                }
                else
                {
                    // Thất bại đủ 3 lần hoặc lỗi vĩnh viễn: Tự động chuyển danh sách chờ xử lý kế toán
                    refundReq.Status = RefundStatus.ManualReviewRequired;
                    refundReq.NextRetryAt = null;

                    refundEntity.Status = "RequiresManualReview";
                    refundNewStatus = "RequiresManualReview";
                    auditAction = "REFUND_MANUAL_REVIEW_REQUIRED";
                    refundEntity.FailureReason = isRetryable
                        ? "Hoàn tiền thất bại 3 lần - Đã chuyển thông tin cho kế toán xử lý thủ công"
                        : $"Lỗi không thể thử lại ({refundReq.ErrorCode}: {refundReq.ErrorMessage}) - Đã chuyển cho kế toán xử lý thủ công";
                    responseMessage = isStaffOrAdmin
                        ? "Đơn hàng đã được huỷ nhưng hoàn tiền THẤT BẠI 3 lần. Yêu cầu hoàn tiền đã tự động được chuyển sang danh sách chờ xử lý của kế toán."
                        : "Đơn hàng đã được huỷ nhưng hoàn tiền THẤT BẠI. Yêu cầu hoàn tiền đã được chuyển sang danh sách chờ xử lý thủ công.";

                    var accountingNotifService = HttpContext?.RequestServices?.GetService<EventTicketBooking.Api.Services.Interfaces.IAccountingNotificationService>();
                    if (accountingNotifService != null)
                    {
                        await accountingNotifService.NotifyFailedRefundAsync(refundReq);
                    }
                }
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
                    order = MapToDto(orderForDto, isStaffOrAdmin),
                    refund = MapRefundToDto(refundEntity, isStaffOrAdmin)
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

        private RefundDto MapRefundToDto(Refund refund, bool isStaffOrAdmin = false)
        {
            var refundReq = _context.RefundRequests.AsNoTracking().FirstOrDefault(r => r.OrderId == refund.OrderId);
            return new()
            {
                Id = refund.Id,
                OrderId = refund.OrderId,
                Amount = refund.Amount,
                Status = refund.Status,
                FailureReason = isStaffOrAdmin ? (refund.FailureReason ?? refundReq?.ErrorMessage) : null,
                CreatedAt = refund.CreatedAt,
                UpdatedAt = refund.UpdatedAt,
                RetryCount = isStaffOrAdmin ? (refundReq?.RetryCount ?? 0) : null
            };
        }

        /// <summary>
        /// S-48: API mô phỏng hoàn tiền thất bại từ Payment Gateway phục vụ kiểm thử UI.
        /// </summary>
        [HttpPost("{orderId:guid}/mock-refund-failure")]
        [RequireRole]
        public async Task<IActionResult> MockRefundFailure(Guid orderId)
        {
            return await CancelOrderAndRefund(orderId, simulateFailure: true, body: null);
        }

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
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "OrderResendTicket", orderId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền gửi lại vé cho đơn hàng này."));
            }

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
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "OrderTickets", orderId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập vé của đơn hàng này."));
            }

            // AC3: Giả sử đơn chưa trả, khi mở trang vé bằng đường dẫn, thì không có vé nào.
            if (order.Status != OrderStatus.Paid)
            {
                return Ok(ApiResponse<List<TicketDto>>.SuccessResult(new List<TicketDto>(), "Đơn hàng chưa thanh toán. Không có vé nào."));
            }

            // S-25 & S-26: Đơn đã Paid -> Trả về danh sách vé kèm mã QR có chữ ký số (ECDSA P-256)
            var showtimeId = order.ShowtimeId;
            var tickets = order.OrderItems
                .Where(oi => oi.Ticket != null)
                .Select(oi =>
                {
                    var isTransferredAway = oi.Ticket!.CurrentUserId != null && oi.Ticket.CurrentUserId != order.UserId;
                    return new TicketDto
                    {
                        Id = oi.Ticket.Id,
                        OrderItemId = oi.Id,
                        OrderId = order.Id,
                        ShowtimeId = showtimeId,
                        TicketCode = isTransferredAway ? "ĐÃ CHUYỂN NHƯỢNG" : oi.Ticket.TicketCode,
                        QrPayload = isTransferredAway ? "" : _ticketService.GenerateQrPayload(oi.Ticket.TicketCode, showtimeId),
                        EventTitle = order.Showtime?.Event?.Title,
                        EventLocation = order.Showtime?.Event?.Location,
                        EventImageUrl = order.Showtime?.Event?.ImageUrl,
                        ShowtimeStartTime = order.Showtime?.StartTime,
                        ShowtimeEndTime = order.Showtime?.EndTime,
                        CategoryName = oi.Seat?.SeatCategory?.Name,
                        SeatRow = oi.Seat?.Row,
                        SeatNumber = oi.Seat?.SeatNumber ?? 0,
                        Price = oi.Price,
                        CreatedAt = oi.Ticket.CreatedAt,
                        IsCheckedIn = oi.IsCheckedIn,
                        IsTransferred = isTransferredAway,
                        TransferCount = oi.Ticket.TransferCount,
                        CanTransfer = !isTransferredAway && !oi.IsCheckedIn && oi.Ticket.TransferCount < 1
                    };
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
            {
                SecurityAuditLogger.LogOwnershipDenied(HttpContext, "OrderAuditLogs", orderId.ToString());
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult("Bạn không có quyền truy cập nhật ký của đơn hàng này."));
            }

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

            var isStaffOrAdmin = User.IsInRole("Admin") || User.IsInRole("Auditor") ||
                                 User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") && (c.Value == "Admin" || c.Value == "Auditor"));
            var orderDtos = orders.Select(o => MapToDto(o, isStaffOrAdmin)).ToList();

            var result = new PagedResultDto<OrderDto>
            {
                Items = orderDtos,
                Page = page,
                PageSize = pageSize,
                TotalItems = totalItems
            };

            return Ok(ApiResponse<PagedResultDto<OrderDto>>.SuccessResult(result, "Lấy lịch sử đơn hàng thành công."));
        }

        private OrderDto MapToDto(Order order, bool isStaffOrAdmin = false)
        {
            var seatNames = order.OrderItems?
                .Where(oi => oi.Seat != null)
                .Select(oi => $"{oi.Seat.Row}{oi.Seat.SeatNumber}")
                .ToList() ?? new List<string>();

            // Story S-45 & S-48: hiển thị trạng thái hoàn tiền (ưu tiên bản ghi S-48 RefundRequest)
            var latestRefund = order.Refunds?.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            var refundReq = _context.RefundRequests.AsNoTracking().FirstOrDefault(r => r.OrderId == order.Id);

            var refundStatus = refundReq != null ? refundReq.Status.ToString() : latestRefund?.Status;
            if (refundReq != null && (refundReq.Status == RefundStatus.ManualReviewRequired || refundReq.RetryCount >= 3))
            {
                refundStatus = "RequiresManualReview";
            }
            else if (latestRefund != null && (latestRefund.Status == "RequiresManualReview" || latestRefund.Status == "ManualReviewRequired"))
            {
                refundStatus = "RequiresManualReview";
            }

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
                OriginalAmount = order.OriginalAmount > 0 ? order.OriginalAmount : order.TotalAmount,
                DiscountAmount = order.DiscountAmount,
                DiscountCode = order.DiscountCode?.Code ?? (order.DiscountCodeSnapshot != null ? order.DiscountCodeSnapshot.Split('|')[0] : null),
                DiscountCodeId = order.DiscountCodeId,
                SeatCount = order.OrderItems?.Count ?? 0,
                SeatNames = seatNames,
                CreatedAt = order.CreatedAt,
                ExpiresAt = order.ExpiresAt,
                HasFailedPayment = order.PaymentTransaction != null &&
                    (order.PaymentTransaction.Status == "FAILED" || order.PaymentTransaction.Status == "CANCELLED"),
                IsPaymentProcessing = order.PaymentTransaction != null && order.PaymentTransaction.Status == "PENDING",
                CancelReason = order.CancelReason,
                RefundStatus = refundStatus,
                RefundAmount = latestRefund?.Amount ?? refundReq?.Amount,
                RefundUpdatedAt = latestRefund?.UpdatedAt ?? refundReq?.UpdatedAt,
                RefundRequestId = refundReq?.Id ?? latestRefund?.Id,
                RefundCreatedAt = refundReq?.CreatedAt ?? latestRefund?.CreatedAt,

                // Các trường kỹ thuật S-48: CHỈ cấp cho Auditor/Admin, Customer luôn nhận null (và bị loại bỏ khỏi raw JSON)
                RefundFailureReason = isStaffOrAdmin ? (latestRefund?.FailureReason ?? refundReq?.ErrorMessage) : null,
                RefundRetryCount = isStaffOrAdmin ? (refundReq?.RetryCount ?? 0) : null,
                RefundAttemptCount = isStaffOrAdmin ? refundReq?.AttemptCount : null,
                RefundLastAttemptedAt = isStaffOrAdmin ? refundReq?.LastAttemptedAt : null,
                RefundNextRetryAt = isStaffOrAdmin ? refundReq?.NextRetryAt : null,
                RefundErrorCode = isStaffOrAdmin ? refundReq?.ErrorCode : null,
                RefundErrorMessage = isStaffOrAdmin ? refundReq?.ErrorMessage : null,
                ReminderSentAt = order.ReminderSentAt,
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

    /// <summary>
    /// DTO yêu cầu huỷ đơn hàng kèm cờ giả lập lỗi (Story S-48).
    /// </summary>
    public class CancelOrderRequestDto
    {
        public bool SimulateFailure { get; set; }
    }
}

