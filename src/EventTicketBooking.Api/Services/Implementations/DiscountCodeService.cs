using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.DiscountCodes;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class DiscountCodeService : IDiscountCodeService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<DiscountCodeService> _logger;
        private static readonly object _inMemoryLock = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _inMemoryUsedCounts = new();

        public DiscountCodeService(AppDbContext context, ILogger<DiscountCodeService>? logger = null)
        {
            _context = context;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscountCodeService>.Instance;
        }

        public async Task<(bool Success, string Message, DiscountCodeDto? Data, int StatusCode)> CreateDiscountCodeAsync(
            Guid eventId, CreateDiscountCodeRequest request, Guid currentUserId, bool isAdmin = false)
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return (false, "Mã giảm giá không được để trống.", null, StatusCodes.Status400BadRequest);
            }

            var normalizedCode = request.Code.Trim().ToUpperInvariant();
            if (normalizedCode.Length > 50)
            {
                return (false, "Mã giảm giá tối đa 50 ký tự.", null, StatusCodes.Status400BadRequest);
            }

            if (request.DiscountType == DiscountType.Percent)
            {
                if (request.DiscountValue <= 0 || request.DiscountValue > 100)
                {
                    return (false, "Phần trăm giảm giá phải từ 1 đến 100.", null, StatusCodes.Status400BadRequest);
                }
            }
            else if (request.DiscountType == DiscountType.FixedAmount)
            {
                if (request.DiscountValue <= 0)
                {
                    return (false, "Số tiền giảm cố định phải lớn hơn 0.", null, StatusCodes.Status400BadRequest);
                }
            }
            else
            {
                return (false, "Loại giảm giá không hợp lệ.", null, StatusCodes.Status400BadRequest);
            }

            if (request.UsageLimit <= 0)
            {
                return (false, "Số lượt sử dụng phải lớn hơn 0.", null, StatusCodes.Status400BadRequest);
            }

            var expiresAtUtc = request.ExpiresAt.ToUniversalTime();
            if (expiresAtUtc <= DateTimeOffset.UtcNow)
            {
                return (false, "Thời hạn sử dụng phải ở tương lai.", null, StatusCodes.Status400BadRequest);
            }

            var ev = await _context.Events.FirstOrDefaultAsync(e => e.Id == eventId);
            if (ev == null)
            {
                return (false, "Sự kiện không tồn tại.", null, StatusCodes.Status404NotFound);
            }

            if (!isAdmin && ev.OwnerId != currentUserId)
            {
                return (false, "Bạn không có quyền quản lý mã giảm giá của sự kiện này.", null, StatusCodes.Status403Forbidden);
            }

            var exists = await _context.DiscountCodes
                .AnyAsync(d => d.EventId == eventId && d.Code.ToUpper() == normalizedCode);
            if (exists)
            {
                return (false, "Mã giảm giá đã tồn tại cho sự kiện này.", null, StatusCodes.Status409Conflict);
            }

            var discount = new DiscountCode
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                Code = normalizedCode,
                DiscountType = request.DiscountType,
                DiscountValue = request.DiscountValue,
                UsageLimit = request.UsageLimit,
                UsedCount = 0,
                ExpiresAt = expiresAtUtc,
                IsActive = true,
                CreatedByUserId = currentUserId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _context.DiscountCodes.Add(discount);
            await _context.SaveChangesAsync();

            if (!_context.Database.IsRelational())
            {
                _inMemoryUsedCounts[discount.Id] = 0;
            }

            var dto = new DiscountCodeDto
            {
                Id = discount.Id,
                EventId = discount.EventId,
                Code = discount.Code,
                DiscountType = discount.DiscountType.ToString(),
                DiscountValue = discount.DiscountValue,
                UsageLimit = discount.UsageLimit,
                UsedCount = discount.UsedCount,
                ExpiresAt = discount.ExpiresAt,
                IsActive = discount.IsActive,
                CreatedAt = discount.CreatedAt
            };

            return (true, "Tạo mã giảm giá thành công.", dto, StatusCodes.Status201Created);
        }

        public async Task<(bool Success, string Message, List<DiscountCodeDto>? Data, int StatusCode)> GetDiscountCodesByEventAsync(
            Guid eventId, Guid currentUserId, bool isAdmin)
        {
            var ev = await _context.Events.FirstOrDefaultAsync(e => e.Id == eventId);
            if (ev == null)
            {
                return (false, "Sự kiện không tồn tại.", null, StatusCodes.Status404NotFound);
            }

            if (!isAdmin && ev.OwnerId != currentUserId)
            {
                return (false, "Bạn không có quyền quản lý mã giảm giá của sự kiện này.", null, StatusCodes.Status403Forbidden);
            }

            var codes = await _context.DiscountCodes
                .AsNoTracking()
                .Where(d => d.EventId == eventId)
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new DiscountCodeDto
                {
                    Id = d.Id,
                    EventId = d.EventId,
                    Code = d.Code,
                    DiscountType = d.DiscountType.ToString(),
                    DiscountValue = d.DiscountValue,
                    UsageLimit = d.UsageLimit,
                    UsedCount = d.UsedCount,
                    ExpiresAt = d.ExpiresAt,
                    IsActive = d.IsActive,
                    CreatedAt = d.CreatedAt
                })
                .ToListAsync();

            return (true, "Lấy danh sách mã giảm giá thành công.", codes, StatusCodes.Status200OK);
        }

        public async Task<(bool Success, string Message, OrderDto? Order, int StatusCode)> ApplyDiscountCodeAsync(
            Guid orderId, string code, Guid currentUserId)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return (false, "Mã giảm giá không được để trống.", null, StatusCodes.Status400BadRequest);
            }

            var normalizedCode = code.Trim().ToUpperInvariant();

            var order = await _context.Orders
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .Include(o => o.Showtime)
                    .ThenInclude(s => s.Event)
                .Include(o => o.User)
                .Include(o => o.DiscountCode)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
            {
                return (false, "Không tìm thấy đơn hàng.", null, StatusCodes.Status404NotFound);
            }

            if (order.UserId != currentUserId)
            {
                return (false, "Bạn không có quyền thao tác trên đơn hàng này.", null, StatusCodes.Status403Forbidden);
            }

            if (order.Status != OrderStatus.Pending)
            {
                return (false, "Đơn hàng không ở trạng thái chờ thanh toán.", null, StatusCodes.Status400BadRequest);
            }

            if (order.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return (false, "Đơn hàng đã hết hạn thanh toán.", null, StatusCodes.Status400BadRequest);
            }

            var discount = await _context.DiscountCodes
                .FirstOrDefaultAsync(d => d.EventId == order.Showtime.EventId && d.Code.ToUpper() == normalizedCode);

            if (discount == null)
            {
                var existsOtherEvent = await _context.DiscountCodes
                    .AnyAsync(d => d.Code.ToUpper() == normalizedCode);

                if (existsOtherEvent)
                {
                    return (false, "Mã giảm giá không thuộc sự kiện này.", null, StatusCodes.Status400BadRequest);
                }

                return (false, "Mã giảm giá không tồn tại.", null, StatusCodes.Status404NotFound);
            }

            if (!discount.IsActive)
            {
                return (false, "Mã giảm giá không còn hoạt động.", null, StatusCodes.Status400BadRequest);
            }

            if (discount.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return (false, "Mã giảm giá đã hết hạn.", null, StatusCodes.Status400BadRequest);
            }

            // 1. Idempotency: nếu đơn hàng đã áp dụng chính mã này -> trả về thành công không tăng UsedCount
            if (order.DiscountCodeId == discount.Id)
            {
                return (true, "Mã giảm giá đã được áp dụng cho đơn hàng.", MapOrderToDto(order), StatusCodes.Status200OK);
            }

            // 2. Reject stacking: nếu đơn hàng đã áp dụng mã khác -> từ chối
            if (order.DiscountCodeId.HasValue && order.DiscountCodeId.Value != discount.Id)
            {
                return (false, "Đơn hàng đã có mã giảm giá khác, không thể áp dụng nhiều mã.", null, StatusCodes.Status400BadRequest);
            }

            // 3. Tính toán số tiền giảm
            int originalAmount = order.OriginalAmount > 0
                ? order.OriginalAmount
                : (order.OrderItems?.Sum(oi => oi.Price) ?? order.TotalAmount);

            int discountAmount = 0;
            if (discount.DiscountType == DiscountType.Percent)
            {
                discountAmount = (int)Math.Floor((long)originalAmount * discount.DiscountValue / 100.0);
            }
            else
            {
                discountAmount = discount.DiscountValue;
            }

            discountAmount = Math.Min(discountAmount, originalAmount);
            int finalTotal = Math.Max(0, originalAmount - discountAmount);

            // 4. Atomic concurrency guarantee
            var now = DateTimeOffset.UtcNow;
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;

            if (_context.Database.IsRelational())
            {
                transaction = await _context.Database.BeginTransactionAsync();
            }

            try
            {
                int affectedRows = 0;

                if (_context.Database.IsRelational())
                {
                    affectedRows = await _context.Database.ExecuteSqlRawAsync(
                        @"UPDATE ""DiscountCodes""
                          SET ""UsedCount"" = ""UsedCount"" + 1, ""UpdatedAt"" = {0}
                          WHERE ""Id"" = {1} AND ""IsActive"" = TRUE AND ""ExpiresAt"" > {0} AND ""UsedCount"" < ""UsageLimit""",
                        now, discount.Id);
                }
                else
                {
                    lock (_inMemoryLock)
                    {
                        int currentCount = _inMemoryUsedCounts.GetOrAdd(discount.Id, discount.UsedCount);
                        if (discount.IsActive && discount.ExpiresAt > now && currentCount < discount.UsageLimit)
                        {
                            currentCount++;
                            _inMemoryUsedCounts[discount.Id] = currentCount;
                            discount.UsedCount = currentCount;
                            discount.UpdatedAt = now;
                            affectedRows = 1;
                        }
                        else
                        {
                            affectedRows = 0;
                        }
                    }
                }

                if (affectedRows == 0)
                {
                    if (transaction != null) await transaction.RollbackAsync();
                    return (false, "Mã giảm giá đã hết lượt sử dụng hoặc đã hết hạn.", null, StatusCodes.Status400BadRequest);
                }

                // Gắn mã vào Order và snapshot
                order.DiscountCodeId = discount.Id;
                order.DiscountCodeSnapshot = $"{discount.Code}|{discount.DiscountType}|{discount.DiscountValue}|{discountAmount}";
                order.OriginalAmount = originalAmount;
                order.DiscountAmount = discountAmount;
                order.TotalAmount = finalTotal;
                order.UpdatedAt = now;

                await _context.SaveChangesAsync();

                if (transaction != null)
                {
                    await transaction.CommitAsync();
                }
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync();
                }
                _logger.LogError(ex, "Lỗi xảy ra khi áp dụng mã giảm giá {Code} cho đơn hàng {OrderId}", discount.Code, orderId);
                throw;
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }

            order.DiscountCode = discount;
            return (true, "Áp dụng mã giảm giá thành công.", MapOrderToDto(order), StatusCodes.Status200OK);
        }

        public async Task<bool> ReleaseDiscountReservationAsync(Order order, CancellationToken cancellationToken = default)
        {
            if (order == null) return false;

            // Đơn hàng đã Paid tuyệt đối không được release
            if (order.Status == OrderStatus.Paid)
            {
                return false;
            }

            if (!order.DiscountCodeId.HasValue)
            {
                return false;
            }

            var codeId = order.DiscountCodeId.Value;
            var now = DateTimeOffset.UtcNow;

            // Xóa liên kết discount khỏi Order để tránh double release
            order.DiscountCodeId = null;
            order.DiscountAmount = 0;
            if (order.OriginalAmount > 0)
            {
                order.TotalAmount = order.OriginalAmount;
            }
            order.UpdatedAt = now;

            if (_context.Database.IsRelational())
            {
                await _context.Database.ExecuteSqlRawAsync(
                    @"UPDATE ""DiscountCodes""
                      SET ""UsedCount"" = ""UsedCount"" - 1, ""UpdatedAt"" = {0}
                      WHERE ""Id"" = {1} AND ""UsedCount"" > 0",
                    now, codeId);
            }
            else
            {
                lock (_inMemoryLock)
                {
                    int currentCount = _inMemoryUsedCounts.GetOrAdd(codeId, 1);
                    if (currentCount > 0)
                    {
                        currentCount--;
                        _inMemoryUsedCounts[codeId] = currentCount;
                    }
                    var code = _context.DiscountCodes.Find(codeId);
                    if (code != null)
                    {
                        code.UsedCount = currentCount;
                        code.UpdatedAt = now;
                    }
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> ReleaseDiscountReservationAsync(Guid orderId, CancellationToken cancellationToken = default)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order == null) return false;
            return await ReleaseDiscountReservationAsync(order, cancellationToken);
        }

        private static OrderDto MapOrderToDto(Order order)
        {
            var seatNames = order.OrderItems?
                .Where(oi => oi.Seat != null)
                .Select(oi => $"{oi.Seat.Row}{oi.Seat.SeatNumber}")
                .ToList() ?? new List<string>();

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
