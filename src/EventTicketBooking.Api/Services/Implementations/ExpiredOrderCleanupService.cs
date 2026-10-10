using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using EventTicketBooking.Api.Hubs;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Service quét huỷ đơn hàng quá hạn thanh toán và nhả ghế trong một giao dịch (Task T-52 / S-23).
    /// Khoá dòng đơn hàng để tranh chấp trực tiếp với webhook thanh toán (T-46).
    /// </summary>
    public class ExpiredOrderCleanupService : IExpiredOrderCleanupService
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<ExpiredOrderCleanupService> _logger;
        private readonly IHubContext<SeatStatusHub>? _hubContext;
        private readonly IOrderAuditService _orderAuditService;

        public ExpiredOrderCleanupService(
            AppDbContext dbContext,
            ILogger<ExpiredOrderCleanupService> logger,
            IHubContext<SeatStatusHub>? hubContext = null,
            IOrderAuditService? orderAuditService = null)
        {
            _dbContext = dbContext;
            _logger = logger;
            _hubContext = hubContext;
            _orderAuditService = orderAuditService ?? new OrderAuditService(dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderAuditService>.Instance);
        }

        public async Task<int> CleanupExpiredOrdersAsync(DateTimeOffset? fakeNow = null, CancellationToken cancellationToken = default)
        {
            var now = fakeNow ?? DateTimeOffset.UtcNow;

            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
            if (_dbContext.Database.IsRelational())
            {
                transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            }
            else
            {
                try
                {
                    transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // InMemory DB provider does not support real transactions
                }
            }

            try
            {
                List<Order> expiredOrders;

                if (_dbContext.Database.IsNpgsql())
                {
                    // Khoá các dòng đơn quá hạn trong PostgreSQL (FOR UPDATE) để ngăn webhook đua lệnh cùng lúc
                    expiredOrders = await _dbContext.Orders
                        .FromSqlRaw("SELECT * FROM orders WHERE \"Status\" = 'Pending' AND \"ExpiresAt\" <= {0} FOR UPDATE", now)
                        .Include(o => o.OrderItems)
                        .ToListAsync(cancellationToken);
                }
                else
                {
                    expiredOrders = await _dbContext.Orders
                        .Include(o => o.OrderItems)
                        .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
                        .ToListAsync(cancellationToken);
                }

                if (!expiredOrders.Any())
                {
                    _logger.LogDebug("Job T-52: Không phát hiện đơn hàng nào quá hạn.");
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                    return 0;
                }

                int cancelledCount = 0;
                var seatIdsToRelease = new List<Guid>();

                foreach (var order in expiredOrders)
                {
                    var oldStatus = order.Status.ToString();
                    order.Status = OrderStatus.Expired;
                    order.UpdatedAt = now;
                    cancelledCount++;

                    // Story S-49: Ghi nhận nhật ký huỷ đơn hàng do quá hạn bởi Background Job
                    _orderAuditService.Record(
                        order.Id,
                        "ORDER",
                        order.Id.ToString(),
                        "ORDER_EXPIRED",
                        oldStatus,
                        OrderStatus.Expired.ToString(),
                        "BACKGROUND_JOB",
                        "job:ExpiredOrderCleanupWorker",
                        null,
                        "Job nền quét và huỷ đơn hàng do quá hạn thời gian thanh toán.");

                    if (order.OrderItems != null && order.OrderItems.Any())
                    {
                        seatIdsToRelease.AddRange(order.OrderItems.Select(oi => oi.SeatId));
                    }
                }

                seatIdsToRelease = seatIdsToRelease.Distinct().ToList();

                if (seatIdsToRelease.Any())
                {
                    // 1. Chuyển giữ chỗ của các ghế tương ứng sang EXPIRED
                    var activeHolds = await _dbContext.SeatHold
                        .Where(sh => seatIdsToRelease.Contains(sh.SeatId) && sh.Status == "ACTIVE" && sh.ExpiresAt <= now.UtcDateTime)
                        .ToListAsync(cancellationToken);

                    foreach (var hold in activeHolds)
                    {
                        hold.Status = "EXPIRED";

                        // Story S-49: Ghi nhận nhật ký giải phóng giữ chỗ cho từng đơn hàng tương ứng
                        var relatedOrder = expiredOrders.FirstOrDefault(o => o.OrderItems.Any(oi => oi.SeatId == hold.SeatId));
                        if (relatedOrder != null)
                        {
                            _orderAuditService.Record(
                                relatedOrder.Id,
                                "SEAT_HOLD",
                                hold.SeatId.ToString(),
                                "SEAT_HOLD_EXPIRED",
                                "ACTIVE",
                                "EXPIRED",
                                "BACKGROUND_JOB",
                                "job:ExpiredOrderCleanupWorker",
                                null,
                                "Job nền giải phóng giữ chỗ của đơn hàng đã hết hạn.");
                        }
                    }

                    // 2. Chuyển trạng thái ghế từ HELD về AVAILABLE
                    var heldSeats = await _dbContext.Seats
                        .Where(s => seatIdsToRelease.Contains(s.Id) && s.Status == "HELD")
                        .ToListAsync(cancellationToken);

                    foreach (var seat in heldSeats)
                    {
                        seat.Status = "AVAILABLE";
                    }
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    if (_hubContext != null && _hubContext.Clients != null)
                    {
                        var grouped = heldSeats.GroupBy(s => s.ShowtimeId);
                        foreach (var group in grouped)
                        {
                            var groupClient = _hubContext.Clients.Group(group.Key.ToString());
                            if (groupClient != null)
                            {
                                await groupClient.SendAsync("SeatReleased", new { ShowtimeId = group.Key, SeatIds = group.Select(s => s.Id).ToList() }, cancellationToken);
                            }
                        }
                    }
                }
                else
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger.LogInformation("Đã huỷ thành công {Count} đơn hàng quá hạn.", cancelledCount);
                return cancelledCount;
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                _logger.LogError(ex, "Lỗi xảy ra trong quá trình quét huỷ đơn quá hạn.");
                throw;
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }
        }
    }
}
