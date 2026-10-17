using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class TicketTransferService : ITicketTransferService
    {
        private readonly AppDbContext _context;
        private readonly ITicketService _ticketService;
        private readonly ILogger<TicketTransferService> _logger;

        public TicketTransferService(
            AppDbContext context,
            ITicketService ticketService,
            ILogger<TicketTransferService> logger)
        {
            _context = context;
            _ticketService = ticketService;
            _logger = logger;
        }

        public async Task<ApiResponse<TicketTransferResultDto>> TransferTicketAsync(
            Guid ticketId,
            Guid senderUserId,
            TransferTicketRequestDto request,
            CancellationToken cancellationToken = default)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RecipientEmail))
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Vui lòng cung cấp email người nhận.");
            }

            var recipientEmail = request.RecipientEmail.Trim().ToLowerInvariant();

            // 1. Tải vé kèm thông tin đơn hàng, suất diễn và sự kiện
            var ticket = await _context.Tickets
                .Include(t => t.OrderItem)
                    .ThenInclude(oi => oi.Order)
                        .ThenInclude(o => o.Showtime)
                            .ThenInclude(st => st.Event)
                .Include(t => t.OrderItem)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

            if (ticket == null)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Không tìm thấy thông tin vé.");
            }

            var order = ticket.OrderItem?.Order;
            var showtime = order?.Showtime;
            var @event = showtime?.Event;

            if (order == null || showtime == null || @event == null)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Dữ liệu sự kiện hoặc đơn hàng của vé không hợp lệ.");
            }

            // 2. Kiểm tra quyền sở hữu vé của người gửi
            var currentOwnerId = ticket.CurrentUserId ?? order.UserId;
            if (currentOwnerId != senderUserId)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Bạn không có quyền chuyển nhượng vé này.");
            }

            // 3. Đơn hàng phải ở trạng thái đã thanh toán (Paid)
            if (order.Status != OrderStatus.Paid)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Chỉ có thể chuyển nhượng vé thuộc đơn hàng đã thanh toán.");
            }

            // 4. Vé chưa check-in vào cửa
            if (ticket.OrderItem!.IsCheckedIn)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Vé này đã được soát vào cửa, không thể chuyển nhượng.");
            }

            // 5. Kiểm tra chính sách sự kiện
            if (!@event.AllowTicketTransfer)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Sự kiện này không cho phép chuyển nhượng vé theo chính sách của Ban tổ chức.");
            }

            // 6. Kiểm tra thời hạn chuyển nhượng (Cutoff time)
            var cutoffHours = @event.TransferCutoffHours;
            var cutoffTime = showtime.StartTime.AddHours(-cutoffHours);
            if (DateTime.UtcNow > cutoffTime)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult(
                    $"Đã hết thời hạn chuyển nhượng vé cho suất diễn này (chuyển nhượng đóng trước giờ diễn {cutoffHours} tiếng).");
            }

            // 7. Kiểm tra giới hạn số lần chuyển nhượng (MVP: 1 lần)
            if (ticket.TransferCount >= 1)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Vé này đã đạt giới hạn chuyển nhượng tối đa (tối đa 1 lần).");
            }

            // 8. Kiểm tra người nhận
            var recipient = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == recipientEmail && u.IsActive, cancellationToken);

            if (recipient == null)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult(
                    "Email người nhận không tồn tại trong hệ thống hoặc tài khoản chưa được kích hoạt. Người nhận cần đăng ký tài khoản trước.");
            }

            if (recipient.Id == senderUserId)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Không thể tự chuyển nhượng vé cho chính mình.");
            }

            var sender = await _context.Users.FindAsync(new object[] { senderUserId }, cancellationToken);

            // 9. Thực hiện chuyển nhượng nguyên tử (Atomic Transfer)
            var oldTicketCode = ticket.TicketCode;
            var newTicketCode = _ticketService.GenerateTicketCode();

            var transferRecord = new TicketTransfer
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                FromUserId = senderUserId,
                ToUserId = recipient.Id,
                FromUserEmail = sender?.Email ?? senderUserId.ToString(),
                ToUserEmail = recipient.Email,
                OldTicketCode = oldTicketCode,
                NewTicketCode = newTicketCode,
                Status = TransferStatus.Completed,
                Note = request.Note?.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Cập nhật vé
            ticket.CurrentUserId = recipient.Id;
            ticket.TicketCode = newTicketCode;
            ticket.TransferCount += 1;
            ticket.LastTransferredAt = DateTimeOffset.UtcNow;

            var isRelational = _context.Database.IsRelational();
            await using var tx = isRelational
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken)
                : null;

            // Kiểm tra lại trạng thái IsCheckedIn trước khi lưu để phòng ngừa race condition khi soát vé đồng thời
            var currentCheckedIn = await _context.OrderItems
                .Where(oi => oi.Id == ticket.OrderItemId)
                .Select(oi => oi.IsCheckedIn)
                .FirstOrDefaultAsync(cancellationToken);

            if (currentCheckedIn)
            {
                return ApiResponse<TicketTransferResultDto>.FailureResult("Vé này đã được soát vào cửa ngay trước thời điểm chuyển nhượng.");
            }

            var auditLog = new OrderAuditLog
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                EntityType = "TICKET",
                EntityId = ticket.Id.ToString(),
                Action = "TICKET_TRANSFERRED",
                OldStatus = oldTicketCode,
                NewStatus = newTicketCode,
                ActorType = "USER",
                Actor = sender?.Email ?? senderUserId.ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Note = $"Chuyển nhượng vé cho {recipient.Email}. Ghi chú: {request.Note?.Trim()}"
            };

            _context.TicketTransfers.Add(transferRecord);
            _context.OrderAuditLogs.Add(auditLog);

            await _context.SaveChangesAsync(cancellationToken);

            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Chuyển nhượng vé thành công: TicketId={TicketId}, From={FromEmail}, To={ToEmail}, OldCode={OldCode}, NewCode={NewCode}",
                ticket.Id, sender?.Email, recipient.Email, oldTicketCode, newTicketCode);

            var result = new TicketTransferResultDto
            {
                TransferId = transferRecord.Id,
                TicketId = ticket.Id,
                OldTicketCode = oldTicketCode,
                NewTicketCode = newTicketCode,
                FromEmail = transferRecord.FromUserEmail,
                ToEmail = transferRecord.ToUserEmail,
                RecipientName = recipient.FullName ?? recipient.Username,
                TransferredAt = transferRecord.CreatedAt,
                Message = $"Chuyển nhượng vé thành công tới tài khoản {recipient.Email}."
            };

            return ApiResponse<TicketTransferResultDto>.SuccessResult(result, result.Message);
        }

        public async Task<ApiResponse<List<TicketTransferHistoryDto>>> GetTicketTransferHistoryAsync(
            Guid ticketId,
            Guid requesterUserId,
            bool isAdmin,
            CancellationToken cancellationToken = default)
        {
            var ticket = await _context.Tickets
                .Include(t => t.OrderItem)
                    .ThenInclude(oi => oi.Order)
                .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

            if (ticket == null)
            {
                return ApiResponse<List<TicketTransferHistoryDto>>.FailureResult("Không tìm thấy thông tin vé.");
            }

            var currentOwnerId = ticket.CurrentUserId ?? ticket.OrderItem.Order.UserId;

            // Kiểm tra quyền xem: Admin, chủ hiện tại, hoặc người từng chuyển
            var transfers = await _context.TicketTransfers
                .Where(tt => tt.TicketId == ticketId)
                .OrderByDescending(tt => tt.CreatedAt)
                .ToListAsync(cancellationToken);

            var hasInvolvement = isAdmin ||
                                 currentOwnerId == requesterUserId ||
                                 ticket.OrderItem.Order.UserId == requesterUserId ||
                                 transfers.Any(tt => tt.FromUserId == requesterUserId || tt.ToUserId == requesterUserId);

            if (!hasInvolvement)
            {
                return ApiResponse<List<TicketTransferHistoryDto>>.FailureResult("Bạn không có quyền xem lịch sử chuyển nhượng của vé này.");
            }

            var dtos = transfers.Select(t => new TicketTransferHistoryDto
            {
                TransferId = t.Id,
                TicketId = t.TicketId,
                FromEmail = t.FromUserEmail,
                ToEmail = t.ToUserEmail,
                OldTicketCode = t.OldTicketCode,
                NewTicketCode = t.NewTicketCode,
                Note = t.Note,
                TransferredAt = t.CreatedAt
            }).ToList();

            return ApiResponse<List<TicketTransferHistoryDto>>.SuccessResult(dtos, "Lấy lịch sử chuyển nhượng thành công.");
        }

        public async Task<ApiResponse<List<TicketDto>>> GetUserTicketsAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            // Lấy danh sách vé mà user hiện là chủ sở hữu HOẶC vé user mua nhưng đã chuyển đi (để hiển thị trạng thái)
            var tickets = await _context.Tickets
                .AsNoTracking()
                .Include(t => t.OrderItem)
                    .ThenInclude(oi => oi.Order)
                        .ThenInclude(o => o.Showtime)
                            .ThenInclude(st => st.Event)
                .Include(t => t.OrderItem)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .Where(t =>
                    // Trường hợp 1: Được chuyển nhượng tới user
                    t.CurrentUserId == userId ||
                    // Trường hợp 2: User là người mua ban đầu và đơn đã thanh toán
                    (t.OrderItem.Order.UserId == userId && t.OrderItem.Order.Status == OrderStatus.Paid))
                .ToListAsync(cancellationToken);

            var ticketIds = tickets.Select(t => t.Id).ToList();
            var transferRecords = await _context.TicketTransfers
                .AsNoTracking()
                .Where(tt => ticketIds.Contains(tt.TicketId))
                .OrderByDescending(tt => tt.CreatedAt)
                .ToListAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var result = new List<TicketDto>();

            foreach (var t in tickets)
            {
                var oi = t.OrderItem;
                var order = oi.Order;
                var showtime = order.Showtime;
                var @event = showtime.Event;

                var isCurrentOwner = (t.CurrentUserId == userId) || (t.CurrentUserId == null && order.UserId == userId);
                var isTransferredAway = !isCurrentOwner && order.UserId == userId && t.CurrentUserId != null;

                var lastTransfer = transferRecords.FirstOrDefault(tr => tr.TicketId == t.Id);

                var cutoffHours = @event?.TransferCutoffHours ?? 2;
                var allowTransfer = @event?.AllowTicketTransfer ?? true;
                var isBeforeCutoff = showtime != null && now <= showtime.StartTime.AddHours(-cutoffHours);

                var canTransfer = isCurrentOwner &&
                                  !oi.IsCheckedIn &&
                                  order.Status == OrderStatus.Paid &&
                                  t.TransferCount < 1 &&
                                  allowTransfer &&
                                  isBeforeCutoff;

                string qrPayload = "";
                if (isCurrentOwner && showtime != null)
                {
                    qrPayload = _ticketService.GenerateQrPayload(t.TicketCode, showtime.Id);
                }

                result.Add(new TicketDto
                {
                    Id = t.Id,
                    OrderItemId = oi.Id,
                    OrderId = order.Id,
                    ShowtimeId = showtime?.Id,
                    TicketCode = isTransferredAway ? "ĐÃ CHUYỂN NHƯỢNG" : t.TicketCode,
                    QrPayload = qrPayload,
                    EventTitle = @event?.Title,
                    EventLocation = @event?.Location,
                    EventImageUrl = @event?.ImageUrl,
                    ShowtimeStartTime = showtime?.StartTime,
                    ShowtimeEndTime = showtime?.EndTime,
                    CategoryName = oi.Seat?.SeatCategory?.Name,
                    SeatRow = oi.Seat?.Row,
                    SeatNumber = oi.Seat?.SeatNumber ?? 0,
                    Price = oi.Price,
                    CreatedAt = t.CreatedAt,
                    IsCheckedIn = oi.IsCheckedIn,
                    IsTransferred = isTransferredAway,
                    TransferredToEmail = isTransferredAway ? lastTransfer?.ToUserEmail : null,
                    TransferredFromEmail = (t.CurrentUserId == userId && lastTransfer != null) ? lastTransfer.FromUserEmail : null,
                    TransferCount = t.TransferCount,
                    CanTransfer = canTransfer
                });
            }

            return ApiResponse<List<TicketDto>>.SuccessResult(result, "Lấy danh sách vé thành công.");
        }
    }
}
