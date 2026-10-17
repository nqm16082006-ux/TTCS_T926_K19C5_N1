using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class DataDeletionService : IDataDeletionService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<DataDeletionService> _logger;

        public DataDeletionService(AppDbContext context, ILogger<DataDeletionService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<DataDeletionRequestDto> CreateRequestAsync(Guid userId, CreateDataDeletionRequestDto dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                throw new KeyNotFoundException("Không tìm thấy thông tin người dùng.");
            }

            if (user.IsAnonymized)
            {
                throw new InvalidOperationException("Tài khoản này đã được ẩn danh dữ liệu cá nhân trước đó.");
            }

            // Kiểm tra xem đã có yêu cầu nào đang chờ xử lý hay không
            var existingPending = await _context.DataDeletionRequests
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Status == DataDeletionRequestStatus.Pending);

            if (existingPending != null)
            {
                throw new InvalidOperationException("Bạn đã có một yêu cầu xoá dữ liệu cá nhân đang chờ phê duyệt. Vui lòng đợi quản trị viên xử lý.");
            }

            var request = new DataDeletionRequest
            {
                UserId = user.Id,
                RequesterEmail = user.Email,
                RequesterName = user.FullName ?? user.Username,
                Reason = string.IsNullOrWhiteSpace(dto.Reason)
                    ? "Yêu cầu xoá dữ liệu cá nhân theo Nghị định 13/2023/NĐ-CP"
                    : dto.Reason.Trim(),
                Status = DataDeletionRequestStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _context.DataDeletionRequests.Add(request);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Người dùng {UserId} ({Email}) đã gửi yêu cầu xoá dữ liệu cá nhân (Request Id: {RequestId}).",
                user.Id, user.Email, request.Id);

            return MapToDto(request);
        }

        public async Task<DataDeletionStatusDto> GetUserStatusAsync(Guid userId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                throw new KeyNotFoundException("Không tìm thấy thông tin người dùng.");
            }

            var latestRequest = await _context.DataDeletionRequests
                .Include(r => r.ReviewedByUser)
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            return new DataDeletionStatusDto
            {
                HasPendingRequest = latestRequest != null && latestRequest.Status == DataDeletionRequestStatus.Pending,
                IsAnonymized = user.IsAnonymized,
                AnonymizedAt = user.AnonymizedAt,
                LatestRequest = latestRequest != null ? MapToDto(latestRequest) : null
            };
        }

        public async Task<List<DataDeletionRequestDto>> GetAllRequestsAsync(string? status = null)
        {
            var query = _context.DataDeletionRequests
                .Include(r => r.ReviewedByUser)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) &&
                Enum.TryParse<DataDeletionRequestStatus>(status, true, out var parsedStatus))
            {
                query = query.Where(r => r.Status == parsedStatus);
            }

            var list = await query
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return list.Select(MapToDto).ToList();
        }

        public async Task<DataDeletionRequestDto> ReviewRequestAsync(Guid requestId, Guid adminUserId, ReviewDataDeletionRequestDto dto)
        {
            var request = await _context.DataDeletionRequests
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.Id == requestId);

            if (request == null)
            {
                throw new KeyNotFoundException("Không tìm thấy yêu cầu xoá dữ liệu cá nhân tương ứng.");
            }

            if (request.Status != DataDeletionRequestStatus.Pending)
            {
                throw new InvalidOperationException($"Yêu cầu này đã được xử lý với trạng thái '{request.Status}'.");
            }

            var adminUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == adminUserId);

            request.ReviewedAt = DateTimeOffset.UtcNow;
            request.ReviewedByUserId = adminUserId;
            request.AdminNote = dto.AdminNote?.Trim();

            if (!dto.Approved)
            {
                // Từ chối yêu cầu
                request.Status = DataDeletionRequestStatus.Rejected;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Admin {AdminId} đã TỪ CHỐI yêu cầu xoá dữ liệu {RequestId} của User {UserId}. Lý do: {Note}",
                    adminUserId, request.Id, request.UserId, request.AdminNote);

                return MapToDto(request);
            }

            // Phê duyệt yêu cầu -> Tiến hành ẩn danh hoá (AC Story S-53)
            request.Status = DataDeletionRequestStatus.Approved;

            var targetUser = request.User ?? await _context.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
            if (targetUser != null && !targetUser.IsAnonymized)
            {
                var originalEmail = targetUser.Email;
                var anonymizedEmail = $"anonymized_{targetUser.Id:N}@privacy.deleted";
                var anonymizedUsername = $"anonymized_{targetUser.Id:N}";
                var anonymizedFullName = "[Dữ liệu đã ẩn danh]";

                // 1. Ẩn danh trong bảng Users & Vô hiệu hoá đăng nhập
                targetUser.FullName = anonymizedFullName;
                targetUser.Email = anonymizedEmail;
                targetUser.Username = anonymizedUsername;
                targetUser.PasswordHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); // Mật khẩu ngẫu nhiên không thể phục hồi
                targetUser.IsActive = false;
                targetUser.IsAnonymized = true;
                targetUser.AnonymizedAt = DateTime.UtcNow;
                targetUser.VerificationCode = null;
                targetUser.VerificationCodeExpiresAt = null;
                targetUser.MarketingEmailOptIn = false;
                targetUser.UpdatedAt = DateTime.UtcNow;

                // 2. Cập nhật thông tin trong bản ghi yêu cầu để không còn lưu dữ liệu định danh
                request.RequesterEmail = anonymizedEmail;
                request.RequesterName = anonymizedFullName;

                // 3. Ẩn danh trong nhật ký gửi email thất bại (EmailFailureLogs)
                var emailLogs = await _context.EmailFailureLogs
                    .Where(e => e.TargetEmail == originalEmail)
                    .ToListAsync();
                foreach (var elog in emailLogs)
                {
                    elog.TargetEmail = anonymizedEmail;
                }

                // 4. Ẩn danh tác nhân trong OrderAuditLogs nếu người dùng từng thao tác
                var actorLogs = await _context.OrderAuditLogs
                    .Where(l => l.ActorUserId == targetUser.Id)
                    .ToListAsync();
                foreach (var alog in actorLogs)
                {
                    alog.Actor = "[Người dùng đã ẩn danh]";
                    alog.Note = string.IsNullOrWhiteSpace(alog.Note)
                        ? "[Tác nhân đã được ẩn danh theo S-53]"
                        : $"{alog.Note} [Tác nhân đã được ẩn danh theo S-53]";
                }

                // 5. Ghi nhận kiểm toán hệ thống cho hành động ẩn danh (S-49 & S-28 Audit Trail)
                var userOrders = await _context.Orders
                    .Where(o => o.UserId == targetUser.Id)
                    .ToListAsync();

                foreach (var order in userOrders)
                {
                    _context.OrderAuditLogs.Add(new OrderAuditLog
                    {
                        OrderId = order.Id,
                        EntityType = "ORDER",
                        EntityId = order.Id.ToString(),
                        Action = "DATA_PRIVACY_ANONYMIZED",
                        OldStatus = order.Status.ToString(),
                        NewStatus = order.Status.ToString(),
                        ActorType = "ADMIN",
                        Actor = adminUser?.Email ?? "Admin",
                        ActorUserId = adminUserId,
                        Timestamp = DateTimeOffset.UtcNow,
                        Note = "Dữ liệu cá nhân người mua đã được ẩn danh theo Nghị định 13/2023/NĐ-CP (S-53). Sổ sách kế toán và doanh thu đơn hàng được giữ nguyên."
                    });
                }

                _context.AuditLogs.Add(new AuditLog
                {
                    ActorUserId = adminUserId,
                    ActorUsername = adminUser?.Email ?? "Admin",
                    TargetUserId = targetUser.Id,
                    OldRoles = string.Join(",", targetUser.UserRoles.Select(ur => ur.Role?.Name ?? "")),
                    NewRoles = "ANONYMIZED_DEACTIVATED",
                    Action = "DATA_PRIVACY_ERASURE_APPROVED",
                    CreatedAt = DateTime.UtcNow
                });

                _logger.LogInformation("Admin {AdminId} đã DUYỆT yêu cầu xoá dữ liệu {RequestId}. Tài khoản {UserId} đã được ẩn danh thành công. Sổ sách kế toán ({OrderCount} đơn) được bảo toàn nguyên vẹn.",
                    adminUserId, request.Id, targetUser.Id, userOrders.Count);
            }

            await _context.SaveChangesAsync();
            return MapToDto(request);
        }

        private static DataDeletionRequestDto MapToDto(DataDeletionRequest request)
        {
            return new DataDeletionRequestDto
            {
                Id = request.Id,
                UserId = request.UserId,
                RequesterEmail = request.RequesterEmail,
                RequesterName = request.RequesterName,
                Reason = request.Reason,
                Status = request.Status.ToString(),
                CreatedAt = request.CreatedAt,
                ReviewedAt = request.ReviewedAt,
                ReviewedByUserId = request.ReviewedByUserId,
                ReviewedByAdminEmail = request.ReviewedByUser?.Email,
                AdminNote = request.AdminNote
            };
        }
    }
}
