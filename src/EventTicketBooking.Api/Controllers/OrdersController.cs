using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Authorization;
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

        public OrdersController(AppDbContext context, ILogger<OrdersController> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// T-37: Tạo đơn hàng từ giữ chỗ và gia hạn giữ chỗ
        /// </summary>
        [HttpPost("showtimes/{showtimeId:guid}")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<OrderDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateOrderFromHolds(Guid showtimeId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId))
                return Unauthorized(ApiResponse<object>.FailureResult("Vui lòng đăng nhập."));

            // Kiểm tra suất diễn
            var showtime = await _context.Showtimes.FirstOrDefaultAsync(s => s.Id == showtimeId);
            if (showtime == null)
                return NotFound(ApiResponse<object>.FailureResult("Không tìm thấy suất diễn."));

            // 1. Chống bấm đúp: Trả về đơn chờ hiện tại nếu đã có
            var existingOrder = await _context.Orders
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Seat)
                .FirstOrDefaultAsync(o => o.UserId == userId && o.ShowtimeId == showtimeId && o.Status == OrderStatus.Pending);

            if (existingOrder != null)
            {
                return Ok(ApiResponse<OrderDto>.SuccessResult(MapToDto(existingOrder), "Đã tồn tại đơn hàng đang chờ thanh toán."));
            }

            var now = DateTimeOffset.UtcNow;
            
            using var transaction = await _context.Database.BeginTransactionAsync();

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

                var expiredHolds = holds.Where(h => h.ExpiresAt <= now.UtcDateTime).ToList();
                if (expiredHolds.Any())
                {
                    var expiredSeatNames = string.Join(", ", expiredHolds.Select(h => $"{h.Seat.Row}{h.Seat.SeatNumber}"));
                    return Conflict(ApiResponse<object>.FailureResult($"Các ghế sau đã hết hạn giữ chỗ: {expiredSeatNames}. Vui lòng đặt lại."));
                }

                // 3. Tính tổng và tạo đơn hàng
                int totalAmount = 0;
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
                    totalAmount += price;

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
                    TotalAmount = totalAmount,
                    ExpiresAt = paymentExpiry,
                    OrderItems = orderItems
                };

                _context.Orders.Add(order);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                return Ok(ApiResponse<OrderDto>.SuccessResult(MapToDto(order), "Tạo đơn hàng thành công."));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Lỗi khi tạo đơn hàng từ giữ chỗ cho User {UserId}", userId);
                return StatusCode(500, ApiResponse<object>.FailureResult("Đã xảy ra lỗi hệ thống."));
            }
        }

        private OrderDto MapToDto(Order order)
        {
            return new OrderDto
            {
                Id = order.Id,
                ShowtimeId = order.ShowtimeId,
                Status = order.Status.ToString(),
                TotalAmount = order.TotalAmount,
                ExpiresAt = order.ExpiresAt,
                Items = order.OrderItems.Select(oi => new OrderItemDto
                {
                    Id = oi.Id,
                    SeatId = oi.SeatId,
                    Price = oi.Price,
                    SeatName = oi.Seat != null ? $"{oi.Seat.Row}{oi.Seat.SeatNumber}" : null
                }).ToList()
            };
        }
    }
}
