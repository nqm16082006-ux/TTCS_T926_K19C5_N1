using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ danh sách chờ khi suất diễn hết vé (Story S-59).
    /// Đảm bảo người đăng ký trước được nhận email thông báo trước (FIFO) khi có ghế về kho do huỷ hay hết hạn.
    /// </summary>
    public class WaitlistService : IWaitlistService
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<WaitlistService> _logger;

        public WaitlistService(
            AppDbContext context,
            IEmailService emailService,
            IConfiguration configuration,
            ILogger<WaitlistService> logger)
        {
            _context = context;
            _emailService = emailService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task<WaitlistRegistrationResultDto> JoinWaitlistAsync(
            JoinWaitlistRequestDto request,
            Guid? currentUserId = null,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.ShowtimeId == Guid.Empty) throw new ArgumentException("Mã suất diễn không hợp lệ.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@') || !request.Email.Contains('.'))
            {
                throw new ArgumentException("Địa chỉ email không hợp lệ.", nameof(request));
            }

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var showtime = await _context.Showtimes
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == request.ShowtimeId, cancellationToken);

            if (showtime == null)
            {
                throw new KeyNotFoundException("Không tìm thấy suất diễn được yêu cầu.");
            }

            // Kiểm tra xem người dùng/email này đã đăng ký và đang ở trạng thái WAITING cho suất diễn này chưa
            var existingEntry = await _context.ShowtimeWaitlists
                .Where(w => w.ShowtimeId == showtime.Id && w.Email == normalizedEmail && w.Status == "WAITING")
                .OrderBy(w => w.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingEntry != null)
            {
                // Đã có trong danh sách chờ -> Tính vị trí hiện tại
                var currentPosition = await _context.ShowtimeWaitlists
                    .CountAsync(w => w.ShowtimeId == showtime.Id && w.Status == "WAITING" && w.CreatedAt <= existingEntry.CreatedAt, cancellationToken);

                var totalWaiters = await _context.ShowtimeWaitlists
                    .CountAsync(w => w.ShowtimeId == showtime.Id && w.Status == "WAITING", cancellationToken);

                return new WaitlistRegistrationResultDto
                {
                    Id = existingEntry.Id,
                    ShowtimeId = showtime.Id,
                    Email = existingEntry.Email,
                    FullName = existingEntry.FullName,
                    QueuePosition = currentPosition,
                    TotalWaiting = totalWaiters,
                    Status = existingEntry.Status,
                    IsExisting = true,
                    Message = $"Bạn đã đăng ký trong danh sách chờ của suất diễn này rồi (Vị trí hiện tại: #{currentPosition}/{totalWaiters}). Hệ thống sẽ tự động gửi email thông báo cho bạn ngay khi có vé về kho!"
                };
            }

            // Đăng ký mới vào hàng đợi
            string? name = request.FullName?.Trim();
            if (string.IsNullOrWhiteSpace(name) && currentUserId.HasValue)
            {
                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);
                name = user?.FullName ?? user?.Username;
            }

            var entry = new ShowtimeWaitlist
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                UserId = currentUserId,
                Email = normalizedEmail,
                FullName = name,
                PhoneNumber = request.PhoneNumber?.Trim(),
                Status = "WAITING",
                CreatedAt = DateTimeOffset.UtcNow,
                NotificationCount = 0
            };

            _context.ShowtimeWaitlists.Add(entry);
            await _context.SaveChangesAsync(cancellationToken);

            // Tính số thứ tự người chờ mới nhất
            var totalWaiting = await _context.ShowtimeWaitlists
                .CountAsync(w => w.ShowtimeId == showtime.Id && w.Status == "WAITING", cancellationToken);

            _logger.LogInformation("Người dùng {Email} đã đăng ký vào danh sách chờ của suất {ShowtimeId} (Vị trí #{Position})",
                normalizedEmail, showtime.Id, totalWaiting);

            return new WaitlistRegistrationResultDto
            {
                Id = entry.Id,
                ShowtimeId = showtime.Id,
                Email = entry.Email,
                FullName = entry.FullName,
                QueuePosition = totalWaiting,
                TotalWaiting = totalWaiting,
                Status = entry.Status,
                IsExisting = false,
                Message = $"Đăng ký danh sách chờ thành công! Bạn là người thứ #{totalWaiting} trong danh sách chờ. Khi có vé được huỷ hoặc nhả do hết hạn, hệ thống sẽ ưu tiên gửi email thông báo cho bạn."
            };
        }

        /// <inheritdoc/>
        public async Task<WaitlistStatusResponseDto> GetStatusAsync(
            Guid showtimeId,
            string? email = null,
            Guid? currentUserId = null,
            CancellationToken cancellationToken = default)
        {
            var showtime = await _context.Showtimes.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null)
            {
                throw new KeyNotFoundException("Không tìm thấy suất diễn.");
            }

            // Tính số ghế còn thực tế
            var now = DateTime.UtcNow;
            var activeHoldSeatIds = await _context.SeatHold.AsNoTracking()
                .Where(sh => sh.Seat.ShowtimeId == showtimeId && sh.Status == "ACTIVE" && sh.ExpiresAt > now)
                .Select(sh => sh.SeatId)
                .ToListAsync(cancellationToken);

            var availableCount = await _context.Seats.AsNoTracking()
                .CountAsync(s => s.ShowtimeId == showtimeId && s.Status == "AVAILABLE" && !activeHoldSeatIds.Contains(s.Id), cancellationToken);

            var totalWaiting = await _context.ShowtimeWaitlists.AsNoTracking()
                .CountAsync(w => w.ShowtimeId == showtimeId && w.Status == "WAITING", cancellationToken);

            bool isUserWaiting = false;
            int? userPos = null;

            string? checkEmail = email?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(checkEmail) && currentUserId.HasValue)
            {
                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId.Value, cancellationToken);
                checkEmail = user?.Email.ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(checkEmail))
            {
                var userEntry = await _context.ShowtimeWaitlists.AsNoTracking()
                    .Where(w => w.ShowtimeId == showtimeId && w.Email == checkEmail && w.Status == "WAITING")
                    .OrderBy(w => w.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (userEntry != null)
                {
                    isUserWaiting = true;
                    userPos = await _context.ShowtimeWaitlists.AsNoTracking()
                        .CountAsync(w => w.ShowtimeId == showtimeId && w.Status == "WAITING" && w.CreatedAt <= userEntry.CreatedAt, cancellationToken);
                }
            }

            return new WaitlistStatusResponseDto
            {
                ShowtimeId = showtimeId,
                IsSoldOut = availableCount <= 0,
                AvailableSeats = availableCount,
                TotalWaiting = totalWaiting,
                IsUserWaiting = isUserWaiting,
                UserQueuePosition = userPos,
                Message = isUserWaiting
                    ? $"Bạn đang ở vị trí #{userPos} trong danh sách chờ."
                    : (availableCount <= 0 ? "Suất diễn hiện đang hết vé. Bạn có thể đăng ký vào danh sách chờ để nhận thông báo sớm nhất." : "Suất diễn còn vé trống.")
            };
        }

        /// <inheritdoc/>
        public async Task<List<WaitlistEntryDto>> GetWaitlistForShowtimeAsync(Guid showtimeId, CancellationToken cancellationToken = default)
        {
            var entries = await _context.ShowtimeWaitlists.AsNoTracking()
                .Where(w => w.ShowtimeId == showtimeId)
                .OrderBy(w => w.CreatedAt)
                .ToListAsync(cancellationToken);

            int pos = 1;
            var result = new List<WaitlistEntryDto>();
            foreach (var item in entries)
            {
                result.Add(new WaitlistEntryDto
                {
                    Id = item.Id,
                    ShowtimeId = item.ShowtimeId,
                    UserId = item.UserId,
                    Email = item.Email,
                    FullName = item.FullName,
                    PhoneNumber = item.PhoneNumber,
                    Status = item.Status,
                    QueuePosition = item.Status == "WAITING" ? pos++ : 0,
                    CreatedAt = item.CreatedAt,
                    NotifiedAt = item.NotifiedAt,
                    NotificationCount = item.NotificationCount
                });
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<int> ProcessAvailableSeatsNotificationAsync(
            Guid showtimeId,
            int seatsReleasedCount = 1,
            CancellationToken cancellationToken = default)
        {
            var showtime = await _context.Showtimes
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

            if (showtime == null || showtime.Status != ShowtimeStatus.OnSale)
            {
                return 0;
            }

            // Kiểm tra số ghế thực tế đang AVAILABLE
            var now = DateTime.UtcNow;
            var activeHoldSeatIds = await _context.SeatHold.AsNoTracking()
                .Where(sh => sh.Seat.ShowtimeId == showtimeId && sh.Status == "ACTIVE" && sh.ExpiresAt > now)
                .Select(sh => sh.SeatId)
                .ToListAsync(cancellationToken);

            var availableSeatsCountInDb = await _context.Seats.AsNoTracking()
                .CountAsync(s => s.ShowtimeId == showtimeId && s.Status == "AVAILABLE" && !activeHoldSeatIds.Contains(s.Id), cancellationToken);

            if (availableSeatsCountInDb <= 0)
            {
                return 0;
            }

            int toNotifyCount = Math.Min(Math.Max(1, seatsReleasedCount), availableSeatsCountInDb);

            // Lấy danh sách những người đang chờ theo thứ tự đăng ký trước (FIFO)
            var waitingEntries = await _context.ShowtimeWaitlists
                .Where(w => w.ShowtimeId == showtimeId && w.Status == "WAITING")
                .OrderBy(w => w.CreatedAt)
                .Take(toNotifyCount)
                .ToListAsync(cancellationToken);

            if (!waitingEntries.Any())
            {
                return 0;
            }

            var appUrl = (_configuration["App:PublicBaseUrl"] ?? _configuration["AppUrl"] ?? _configuration["PublicUrl"] ?? "http://localhost:5012").TrimEnd('/');
            var bookingUrl = $"{appUrl}/event-info.html?id={showtime.EventId}&showtimeId={showtime.Id}";
            var eventTitle = showtime.Event?.Title ?? "Sự kiện EventPulse";
            var location = showtime.Event?.Location ?? "Xem thông tin chi tiết tại trang sự kiện";
            var showtimeStr = $"{showtime.StartTime:dd/MM/yyyy HH:mm}";

            int notifiedCount = 0;
            foreach (var entry in waitingEntries)
            {
                try
                {
                    bool sent = await _emailService.SendWaitlistSeatAvailableEmailAsync(
                        entry.Email,
                        entry.FullName ?? "Quý khách",
                        eventTitle,
                        location,
                        showtimeStr,
                        availableSeatsCountInDb,
                        bookingUrl);

                    entry.Status = "NOTIFIED";
                    entry.NotifiedAt = DateTimeOffset.UtcNow;
                    entry.NotificationCount++;
                    notifiedCount++;

                    _logger.LogInformation("Đã gửi email thông báo vé về kho cho khách chờ {Email} (Waitlist #{Id}, Suất: {ShowtimeId})",
                        entry.Email, entry.Id, showtimeId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi xảy ra khi gửi email thông báo vé về kho cho {Email}", entry.Email);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            return notifiedCount;
        }

        /// <inheritdoc/>
        public async Task<int> ProcessAllPendingWaitlistsAsync(CancellationToken cancellationToken = default)
        {
            var waitingShowtimeIds = await _context.ShowtimeWaitlists.AsNoTracking()
                .Where(w => w.Status == "WAITING")
                .Select(w => w.ShowtimeId)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (!waitingShowtimeIds.Any())
            {
                return 0;
            }

            int totalNotified = 0;
            foreach (var showtimeId in waitingShowtimeIds)
            {
                try
                {
                    int notified = await ProcessAvailableSeatsNotificationAsync(showtimeId, 5, cancellationToken);
                    totalNotified += notified;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi xử lý hàng đợi danh sách chờ cho suất diễn {ShowtimeId}", showtimeId);
                }
            }

            return totalNotified;
        }
    }
}
