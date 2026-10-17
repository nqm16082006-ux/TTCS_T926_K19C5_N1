using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Story S-52: Triển khai tính toán số liệu soát vé thời gian thực cho Ban tổ chức.
    /// Hỗ trợ bóc tách tổng quan, tốc độ qua cửa (khách/phút), phân loại theo cổng và dữ liệu đồng bộ ngoại tuyến.
    /// </summary>
    public class CheckinStatisticsService : ICheckinStatisticsService
    {
        private readonly AppDbContext _context;

        public CheckinStatisticsService(AppDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<ShowtimeLiveCheckinDto> GetLiveCheckinStatsAsync(Guid showtimeId, CancellationToken cancellationToken = default)
        {
            if (showtimeId == Guid.Empty)
            {
                throw new ArgumentException("Mã suất diễn không hợp lệ.", nameof(showtimeId));
            }

            var showtime = await _context.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null)
            {
                throw new KeyNotFoundException($"Không tìm thấy suất diễn với ID {showtimeId}.");
            }

            // 1. Lấy tất cả OrderItems của đơn hàng hợp lệ (Paid) thuộc suất diễn
            var paidOrderItems = await _context.OrderItems
                .AsNoTracking()
                .Include(oi => oi.Order)
                .Where(oi => oi.Order.ShowtimeId == showtimeId && oi.Order.Status == OrderStatus.Paid)
                .ToListAsync(cancellationToken);

            var totalSoldTickets = paidOrderItems.Count;

            // 2. Lọc vé đã check-in thành công (hợp lệ hoặc từ đồng bộ offline)
            var checkedInItems = paidOrderItems
                .Where(oi => oi.IsCheckedIn || oi.CheckInTime != null)
                .ToList();

            var checkedInCount = checkedInItems.Count;
            var remainingCount = Math.Max(0, totalSoldTickets - checkedInCount);
            var checkInPercentage = totalSoldTickets > 0
                ? Math.Round((double)checkedInCount / totalSoldTickets * 100.0, 1)
                : 0.0;

            // 3. Thống kê theo Cổng / Lối vào
            var gateBreakdown = checkedInItems
                .GroupBy(oi => string.IsNullOrWhiteSpace(oi.CheckInGate) ? "Cổng chính" : oi.CheckInGate.Trim())
                .Select(g => new GateCheckinStatDto
                {
                    GateName = g.Key,
                    CheckedInCount = g.Count(),
                    PercentageOfTotal = checkedInCount > 0
                        ? Math.Round((double)g.Count() / checkedInCount * 100.0, 1)
                        : 0.0
                })
                .OrderByDescending(g => g.CheckedInCount)
                .ToList();

            // 4. Tính tốc độ khách vào cửa (Velocity: khách/phút trung bình 5 phút gần nhất)
            var nowUtc = DateTimeOffset.UtcNow;
            var fiveMinutesAgo = nowUtc.AddMinutes(-5);
            var recent5mCount = checkedInItems.Count(oi =>
                oi.CheckInTime.HasValue &&
                oi.CheckInTime.Value >= fiveMinutesAgo &&
                oi.CheckInTime.Value <= nowUtc.AddMinutes(1)); // Chống clock skew nhẹ

            var currentVelocity = Math.Round(recent5mCount / 5.0, 1);

            // 5. Lập biểu đồ lưu lượng theo từng phút (30 phút gần nhất)
            var timeline = new List<CheckinTimelinePointDto>();
            var currentMinuteUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);

            for (int i = 29; i >= 0; i--)
            {
                var bucketStart = currentMinuteUtc.AddMinutes(-i);
                var bucketEnd = bucketStart.AddMinutes(1);

                var countInMinute = checkedInItems.Count(oi =>
                    oi.CheckInTime.HasValue &&
                    oi.CheckInTime.Value.UtcDateTime >= bucketStart &&
                    oi.CheckInTime.Value.UtcDateTime < bucketEnd);

                timeline.Add(new CheckinTimelinePointDto
                {
                    MinuteBucket = bucketStart,
                    Count = countInMinute
                });
            }

            // 6. Truy vấn số liệu đồng bộ ngoại tuyến (Story S-35/S-36)
            var offlineRecords = await _context.OfflineCheckInRecords
                .AsNoTracking()
                .Where(r => r.ShowtimeId == showtimeId && !r.IsConflict && r.Status == "SYNCED")
                .ToListAsync(cancellationToken);

            var offlineSyncedCount = offlineRecords.Count;
            DateTimeOffset? lastSyncTime = offlineRecords.Count > 0
                ? offlineRecords.Max(r => (DateTimeOffset?)r.ReceivedAtServer)
                : null;

            return new ShowtimeLiveCheckinDto
            {
                ShowtimeId = showtime.Id,
                EventId = showtime.EventId,
                EventTitle = showtime.Event?.Title ?? string.Empty,
                StartTime = showtime.StartTime,
                EndTime = showtime.EndTime,
                TotalSoldTickets = totalSoldTickets,
                CheckedInCount = checkedInCount,
                RemainingCount = remainingCount,
                CheckInPercentage = checkInPercentage,
                CurrentVelocityPerMinute = currentVelocity,
                Timeline = timeline,
                GateBreakdown = gateBreakdown,
                OfflineSyncedCount = offlineSyncedCount,
                LastSyncTime = lastSyncTime,
                GeneratedAt = nowUtc
            };
        }
    }
}
