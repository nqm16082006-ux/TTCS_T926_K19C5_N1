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

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ nhắc người mua trước 24 giờ qua email và thông báo cập nhật đổi giờ diễn (Story S-61).
    /// </summary>
    public class ShowtimeReminderService : IShowtimeReminderService
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IOrderAuditService? _orderAuditService;
        private readonly ILogger<ShowtimeReminderService> _logger;

        public ShowtimeReminderService(
            AppDbContext context,
            IEmailService emailService,
            ILogger<ShowtimeReminderService> logger,
            IOrderAuditService? orderAuditService = null)
        {
            _context = context;
            _emailService = emailService;
            _logger = logger;
            _orderAuditService = orderAuditService;
        }

        /// <inheritdoc/>
        public async Task<int> SendUpcomingShowtimeRemindersAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var upcomingThreshold = now.AddHours(24);

            var eligibleOrders = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.Showtime)
                    .ThenInclude(s => s.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Ticket)
                .Where(o => o.Status == OrderStatus.Paid &&
                            o.Showtime.Status != ShowtimeStatus.Cancelled &&
                            o.Showtime.StartTime > now &&
                            o.Showtime.StartTime <= upcomingThreshold &&
                            o.ReminderSentAt == null)
                .ToListAsync(cancellationToken);

            if (eligibleOrders.Count == 0)
            {
                return 0;
            }

            _logger.LogInformation("Tìm thấy {Count} đơn hàng cần gửi email nhắc trước 24 giờ.", eligibleOrders.Count);
            int sentCount = 0;
            var tz = GetVietnamTimeZone();

            foreach (var order in eligibleOrders)
            {
                if (cancellationToken.IsCancellationRequested) break;

                bool sent = await SendReminderEmailForOrderAsync(order, tz, cancellationToken);
                if (sent)
                {
                    sentCount++;
                }
            }

            if (sentCount > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            return sentCount;
        }

        /// <inheritdoc/>
        public async Task<int> SendReminderForShowtimeAsync(Guid showtimeId, bool force = false, CancellationToken cancellationToken = default)
        {
            var query = _context.Orders
                .Include(o => o.User)
                .Include(o => o.Showtime)
                    .ThenInclude(s => s.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Ticket)
                .Where(o => o.ShowtimeId == showtimeId &&
                            o.Status == OrderStatus.Paid &&
                            o.Showtime.Status != ShowtimeStatus.Cancelled);

            if (!force)
            {
                query = query.Where(o => o.ReminderSentAt == null);
            }

            var orders = await query.ToListAsync(cancellationToken);
            if (orders.Count == 0)
            {
                return 0;
            }

            int sentCount = 0;
            var tz = GetVietnamTimeZone();

            foreach (var order in orders)
            {
                if (cancellationToken.IsCancellationRequested) break;

                bool sent = await SendReminderEmailForOrderAsync(order, tz, cancellationToken);
                if (sent)
                {
                    sentCount++;
                }
            }

            if (sentCount > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            return sentCount;
        }

        /// <inheritdoc/>
        public async Task<int> SendShowtimeRescheduledNotificationsAsync(
            Guid showtimeId,
            DateTime oldStartTime,
            DateTime oldEndTime,
            DateTime newStartTime,
            DateTime newEndTime,
            CancellationToken cancellationToken = default)
        {
            var paidOrders = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.Showtime)
                    .ThenInclude(s => s.Event)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                .Where(o => o.ShowtimeId == showtimeId && o.Status == OrderStatus.Paid)
                .ToListAsync(cancellationToken);

            if (paidOrders.Count == 0)
            {
                return 0;
            }

            var tz = GetVietnamTimeZone();
            string oldShowtimeStr = $"{FormatLocalTime(oldStartTime, tz)} - {FormatLocalTime(oldEndTime, tz)}";
            string newShowtimeStr = $"{FormatLocalTime(newStartTime, tz)} - {FormatLocalTime(newEndTime, tz)}";

            _logger.LogInformation("Bắt đầu gửi thông báo đổi giờ diễn cho {Count} đơn hàng của suất {ShowtimeId}. Cũ: {OldTime} -> Mới: {NewTime}",
                paidOrders.Count, showtimeId, oldShowtimeStr, newShowtimeStr);

            int sentCount = 0;
            var now = DateTime.UtcNow;

            foreach (var order in paidOrders)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var seatNames = order.OrderItems
                        .Select(oi => oi.Seat != null ? $"{oi.Seat.Row}{oi.Seat.SeatNumber}" : "Ghế tự do")
                        .ToList();

                    var recipientEmail = order.User?.Email;
                    if (string.IsNullOrWhiteSpace(recipientEmail)) continue;

                    var recipientName = order.User?.FullName ?? order.User?.Username ?? "Quý khách";
                    var eventTitle = order.Showtime?.Event?.Title ?? "Sự kiện";
                    var location = order.Showtime?.Event?.Location ?? "Địa điểm thông báo sau";

                    await _emailService.SendShowtimeRescheduledEmailAsync(
                        toEmail: recipientEmail,
                        toName: recipientName,
                        eventTitle: eventTitle,
                        location: location,
                        oldShowtime: oldShowtimeStr,
                        newShowtime: newShowtimeStr,
                        seatNames: seatNames,
                        orderId: order.Id.ToString());

                    // Nếu giờ mới diễn ra sau hơn 24 giờ tính từ hiện tại, đặt lại ReminderSentAt = null
                    // để người mua tiếp tục nhận email nhắc nhở 24h trước giờ diễn mới!
                    if (newStartTime.ToUniversalTime() > now.AddHours(24))
                    {
                        order.ReminderSentAt = null;
                    }

                    _orderAuditService?.Record(
                        orderId: order.Id,
                        entityType: "ORDER",
                        entityId: order.Id.ToString(),
                        action: "SHOWTIME_RESCHEDULED",
                        oldStatus: order.Status.ToString(),
                        newStatus: order.Status.ToString(),
                        actorType: "ORGANIZER",
                        actor: "Organizer",
                        actorUserId: null,
                        note: $"Đổi giờ diễn từ {oldShowtimeStr} sang {newShowtimeStr}. Đã gửi email thông báo cập nhật.");

                    sentCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi gửi email thông báo đổi giờ diễn cho đơn hàng {OrderId}", order.Id);
                    await LogFailureAsync(order.Id, order.User?.Email ?? "Unknown", ex.Message, 1, cancellationToken);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            return sentCount;
        }

        private async Task<bool> SendReminderEmailForOrderAsync(Order order, TimeZoneInfo tz, CancellationToken cancellationToken)
        {
            try
            {
                var recipientEmail = order.User?.Email;
                if (string.IsNullOrWhiteSpace(recipientEmail)) return false;

                var recipientName = order.User?.FullName ?? order.User?.Username ?? "Quý khách";
                var eventTitle = order.Showtime?.Event?.Title ?? "Sự kiện";
                var location = order.Showtime?.Event?.Location ?? "Địa điểm thông báo sau";
                var showtimeStr = order.Showtime != null
                    ? $"{FormatLocalTime(order.Showtime.StartTime, tz)} - {FormatLocalTime(order.Showtime.EndTime, tz)}"
                    : "Chưa xác định";

                var seatNames = order.OrderItems
                    .Select(oi => oi.Seat != null ? $"{oi.Seat.Row}{oi.Seat.SeatNumber}" : "Ghế tự do")
                    .ToList();

                var ticketCodes = order.OrderItems
                    .Select(oi => oi.Ticket?.TicketCode ?? oi.Id.ToString()[..8].ToUpper())
                    .ToList();

                await _emailService.SendShowtimeReminderEmailAsync(
                    toEmail: recipientEmail,
                    toName: recipientName,
                    eventTitle: eventTitle,
                    location: location,
                    showtime: showtimeStr,
                    seatNames: seatNames,
                    ticketCodes: ticketCodes,
                    orderId: order.Id.ToString());

                order.ReminderSentAt = DateTimeOffset.UtcNow;

                _orderAuditService?.Record(
                    orderId: order.Id,
                    entityType: "ORDER",
                    entityId: order.Id.ToString(),
                    action: "EMAIL_REMINDER_SENT",
                    oldStatus: order.Status.ToString(),
                    newStatus: order.Status.ToString(),
                    actorType: "SYSTEM",
                    actor: "ShowtimeReminderWorker",
                    actorUserId: null,
                    note: "Đã gửi email nhắc nhở 24h trước giờ diễn kèm vé và chỉ dẫn địa điểm.");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi email nhắc nhở cho đơn hàng {OrderId}", order.Id);
                await LogFailureAsync(order.Id, order.User?.Email ?? "Unknown", ex.Message, 1, cancellationToken);
                return false;
            }
        }

        private async Task LogFailureAsync(Guid orderId, string targetEmail, string errorMessage, int attempts, CancellationToken cancellationToken)
        {
            try
            {
                var failureLog = new EmailFailureLog
                {
                    OrderId = orderId,
                    TargetEmail = targetEmail,
                    ErrorMessage = errorMessage,
                    Attempts = attempts,
                    Resolved = false,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                _context.EmailFailureLogs.Add(failureLog);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "Không thể ghi nhận EmailFailureLog cho đơn hàng {OrderId}", orderId);
            }
        }

        private static TimeZoneInfo GetVietnamTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Ho_Chi_Minh");
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }

        private static string FormatLocalTime(DateTime dt, TimeZoneInfo tz)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("HH:mm dd/MM/yyyy");
        }
    }
}
