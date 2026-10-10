using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventTicketBooking.Api.Services.Implementations
{
    public class WaitingRoomService : IWaitingRoomService
    {
        private readonly WaitingRoomOptions _options;
        private readonly ILogger<WaitingRoomService> _logger;

        private class ActivePass
        {
            public string Token { get; set; } = string.Empty;
            public Guid? UserId { get; set; }
            public string ClientIdentifier { get; set; } = string.Empty;
            public DateTime AdmittedAt { get; set; }
            public DateTime LastSeenAt { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        private class WaitingTicket
        {
            public string QueueToken { get; set; } = string.Empty;
            public Guid? UserId { get; set; }
            public string ClientIdentifier { get; set; } = string.Empty;
            public DateTime JoinedAt { get; set; }
            public DateTime LastHeartbeat { get; set; }
            public string Status { get; set; } = "WAITING"; // WAITING, ADMITTED, EXPIRED, CANCELLED
            public string? AccessPassToken { get; set; }
            public DateTime? PassExpiresAt { get; set; }
        }

        private class ShowtimeQueueState
        {
            public readonly object LockObj = new object();
            public int? CustomThreshold;
            public readonly List<WaitingTicket> Queue = new();
            public readonly Dictionary<string, ActivePass> ActivePasses = new();
            public readonly Dictionary<string, WaitingTicket> TicketsByToken = new();
        }

        private readonly ConcurrentDictionary<Guid, ShowtimeQueueState> _states = new();

        public WaitingRoomService(
            IOptions<WaitingRoomOptions> options,
            ILogger<WaitingRoomService> logger)
        {
            _options = options.Value ?? new WaitingRoomOptions();
            _logger = logger;
        }

        private ShowtimeQueueState GetState(Guid showtimeId)
        {
            return _states.GetOrAdd(showtimeId, _ => new ShowtimeQueueState());
        }

        private int GetThreshold(ShowtimeQueueState state)
        {
            return state.CustomThreshold ?? _options.DefaultActiveThreshold;
        }

        public Task<WaitingRoomStatusDto> JoinQueueAsync(Guid showtimeId, Guid? userId, string clientIdentifier)
        {
            if (!_options.Enabled)
            {
                // Nếu phòng chờ tắt, cấp trực tiếp pass vĩnh viễn trong phiên
                var immediatePass = "PASS_DIRECT_" + Guid.NewGuid().ToString("N");
                return Task.FromResult(new WaitingRoomStatusDto
                {
                    ShowtimeId = showtimeId,
                    Status = "ADMITTED",
                    Position = 0,
                    PeopleAhead = 0,
                    ActiveCount = 1,
                    Threshold = _options.DefaultActiveThreshold,
                    AccessPassToken = immediatePass,
                    PassExpiresAt = DateTime.UtcNow.AddMinutes(_options.PassDurationMinutes),
                    LastHeartbeat = DateTime.UtcNow
                });
            }

            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                var now = DateTime.UtcNow;
                PromoteAndCleanInternal(state, now);

                int threshold = GetThreshold(state);

                // 1. Kiểm tra xem chính tài khoản (UserId) hoặc client/tab này đã có Active Pass còn hiệu lực chưa
                var existingPass = state.ActivePasses.Values.FirstOrDefault(p =>
                    (userId.HasValue && p.UserId.HasValue && p.UserId.Value == userId.Value) ||
                    (!string.IsNullOrEmpty(clientIdentifier) && p.ClientIdentifier == clientIdentifier));

                if (existingPass != null && existingPass.ExpiresAt > now)
                {
                    existingPass.LastSeenAt = now;
                    return Task.FromResult(new WaitingRoomStatusDto
                    {
                        ShowtimeId = showtimeId,
                        Status = "ADMITTED",
                        Position = 0,
                        PeopleAhead = 0,
                        ActiveCount = state.ActivePasses.Count,
                        Threshold = threshold,
                        AccessPassToken = existingPass.Token,
                        PassExpiresAt = existingPass.ExpiresAt,
                        LastHeartbeat = now
                    });
                }

                // 2. Kiểm tra xem chính tài khoản (UserId) hoặc client/tab này đã có Ticket trong Queue chưa
                var existingTicket = state.Queue.FirstOrDefault(t =>
                    t.Status == "WAITING" &&
                    ((userId.HasValue && t.UserId.HasValue && t.UserId.Value == userId.Value) ||
                     (!string.IsNullOrEmpty(clientIdentifier) && t.ClientIdentifier == clientIdentifier)));

                if (existingTicket != null)
                {
                    existingTicket.LastHeartbeat = now;
                    int pos = state.Queue.IndexOf(existingTicket) + 1;
                    return Task.FromResult(new WaitingRoomStatusDto
                    {
                        QueueToken = existingTicket.QueueToken,
                        ShowtimeId = showtimeId,
                        Status = "WAITING",
                        Position = pos,
                        PeopleAhead = Math.Max(0, pos - 1),
                        ActiveCount = state.ActivePasses.Count,
                        Threshold = threshold,
                        EstimatedWaitSeconds = EstimateWaitTime(pos),
                        LastHeartbeat = now
                    });
                }

                // 3. Nếu chưa vượt ngưỡng và không có ai đang chờ trong queue -> Vào thẳng (ADMITTED)
                if (state.ActivePasses.Count < threshold && state.Queue.Count == 0)
                {
                    string passToken = "PASS_" + Guid.NewGuid().ToString("N");
                    var passExpires = now.AddMinutes(_options.PassDurationMinutes);

                    var activePass = new ActivePass
                    {
                        Token = passToken,
                        UserId = userId,
                        ClientIdentifier = clientIdentifier,
                        AdmittedAt = now,
                        LastSeenAt = now,
                        ExpiresAt = passExpires
                    };
                    state.ActivePasses[passToken] = activePass;

                    _logger.LogInformation("Showtime {ShowtimeId}: Cấp lượt trực tiếp vào chọn ghế cho {Client} (Active: {Count}/{Threshold})",
                        showtimeId, clientIdentifier, state.ActivePasses.Count, threshold);

                    return Task.FromResult(new WaitingRoomStatusDto
                    {
                        ShowtimeId = showtimeId,
                        Status = "ADMITTED",
                        Position = 0,
                        PeopleAhead = 0,
                        ActiveCount = state.ActivePasses.Count,
                        Threshold = threshold,
                        AccessPassToken = passToken,
                        PassExpiresAt = passExpires,
                        LastHeartbeat = now
                    });
                }

                // 4. Nếu đã vượt ngưỡng hoặc đang có hàng chờ -> Thêm vào hàng đợi FIFO
                if (state.Queue.Count >= _options.MaxQueueCapacity)
                {
                    throw new InvalidOperationException("Hàng đợi phòng chờ hiện đã đạt dung lượng tối đa. Vui lòng thử lại sau.");
                }

                string queueToken = "Q_" + Guid.NewGuid().ToString("N");
                var newTicket = new WaitingTicket
                {
                    QueueToken = queueToken,
                    UserId = userId,
                    ClientIdentifier = clientIdentifier,
                    JoinedAt = now,
                    LastHeartbeat = now,
                    Status = "WAITING"
                };

                state.Queue.Add(newTicket);
                state.TicketsByToken[queueToken] = newTicket;

                int position = state.Queue.Count;
                int peopleAhead = position - 1;

                _logger.LogInformation("Showtime {ShowtimeId}: Người dùng {Client} gia nhập hàng chờ ở vị trí #{Pos}",
                    showtimeId, clientIdentifier, position);

                return Task.FromResult(new WaitingRoomStatusDto
                {
                    QueueToken = queueToken,
                    ShowtimeId = showtimeId,
                    Status = "WAITING",
                    Position = position,
                    PeopleAhead = peopleAhead,
                    ActiveCount = state.ActivePasses.Count,
                    Threshold = threshold,
                    EstimatedWaitSeconds = EstimateWaitTime(position),
                    LastHeartbeat = now
                });
            }
        }

        public Task<WaitingRoomStatusDto?> HeartbeatAsync(Guid showtimeId, string queueToken)
        {
            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                var now = DateTime.UtcNow;
                PromoteAndCleanInternal(state, now);

                if (!state.TicketsByToken.TryGetValue(queueToken, out var ticket))
                {
                    return Task.FromResult<WaitingRoomStatusDto?>(null);
                }

                ticket.LastHeartbeat = now;
                int threshold = GetThreshold(state);

                if (ticket.Status == "ADMITTED")
                {
                    return Task.FromResult<WaitingRoomStatusDto?>(new WaitingRoomStatusDto
                    {
                        QueueToken = ticket.QueueToken,
                        ShowtimeId = showtimeId,
                        Status = "ADMITTED",
                        Position = 0,
                        PeopleAhead = 0,
                        ActiveCount = state.ActivePasses.Count,
                        Threshold = threshold,
                        AccessPassToken = ticket.AccessPassToken,
                        PassExpiresAt = ticket.PassExpiresAt,
                        LastHeartbeat = now
                    });
                }

                if (ticket.Status == "WAITING")
                {
                    int pos = state.Queue.IndexOf(ticket) + 1;
                    if (pos <= 0) pos = 1;

                    return Task.FromResult<WaitingRoomStatusDto?>(new WaitingRoomStatusDto
                    {
                        QueueToken = ticket.QueueToken,
                        ShowtimeId = showtimeId,
                        Status = "WAITING",
                        Position = pos,
                        PeopleAhead = Math.Max(0, pos - 1),
                        ActiveCount = state.ActivePasses.Count,
                        Threshold = threshold,
                        EstimatedWaitSeconds = EstimateWaitTime(pos),
                        LastHeartbeat = now
                    });
                }

                return Task.FromResult<WaitingRoomStatusDto?>(new WaitingRoomStatusDto
                {
                    QueueToken = ticket.QueueToken,
                    ShowtimeId = showtimeId,
                    Status = ticket.Status,
                    Position = 0,
                    PeopleAhead = 0,
                    ActiveCount = state.ActivePasses.Count,
                    Threshold = threshold,
                    LastHeartbeat = now
                });
            }
        }

        public Task<bool> LeaveQueueAsync(Guid showtimeId, string queueToken)
        {
            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                if (state.TicketsByToken.TryGetValue(queueToken, out var ticket))
                {
                    ticket.Status = "CANCELLED";
                    state.Queue.Remove(ticket);
                    state.TicketsByToken.Remove(queueToken);

                    // Nếu ticket này đã được cấp accessPass trước đó -> giải phóng luôn pass
                    if (!string.IsNullOrEmpty(ticket.AccessPassToken))
                    {
                        state.ActivePasses.Remove(ticket.AccessPassToken);
                    }

                    // Promote tiếp người sau nếu cần
                    PromoteAndCleanInternal(state, DateTime.UtcNow);
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
        }

        public Task<bool> ActivePingAsync(Guid showtimeId, string passToken, string clientIdentifier)
        {
            if (string.IsNullOrWhiteSpace(passToken)) return Task.FromResult(false);

            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                var now = DateTime.UtcNow;
                if (state.ActivePasses.TryGetValue(passToken, out var pass))
                {
                    if (pass.ExpiresAt > now)
                    {
                        pass.LastSeenAt = now;
                        return Task.FromResult(true);
                    }
                }
                return Task.FromResult(false);
            }
        }

        public Task<bool> ValidatePassTokenAsync(Guid showtimeId, string passToken)
        {
            return ValidatePassTokenAsync(showtimeId, passToken, null, null);
        }

        public Task<bool> ValidatePassTokenAsync(Guid showtimeId, string passToken, string? clientIdentifier = null)
        {
            return ValidatePassTokenAsync(showtimeId, passToken, clientIdentifier, null);
        }

        public Task<bool> ValidatePassTokenAsync(Guid showtimeId, string passToken, string? clientIdentifier = null, Guid? userId = null)
        {
            if (!_options.Enabled) return Task.FromResult(true);
            if (string.IsNullOrWhiteSpace(passToken)) return Task.FromResult(false);

            // Bypass cho mock direct pass
            if (passToken.StartsWith("PASS_DIRECT_")) return Task.FromResult(true);

            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                var now = DateTime.UtcNow;
                if (state.ActivePasses.TryGetValue(passToken, out var pass))
                {
                    if (pass.ExpiresAt > now)
                    {
                        // 1. Nếu là cùng tài khoản đã đăng nhập (UserId), chấp nhận mọi tab của tài khoản đó
                        if (userId.HasValue && pass.UserId.HasValue && pass.UserId.Value == userId.Value)
                        {
                            pass.LastSeenAt = now;
                            return Task.FromResult(true);
                        }

                        // 2. Nếu có clientIdentifier truyền vào và pass cũng có clientIdentifier -> phải khớp phiên
                        if (!string.IsNullOrEmpty(clientIdentifier) &&
                            !string.IsNullOrEmpty(pass.ClientIdentifier) &&
                            pass.ClientIdentifier != clientIdentifier)
                        {
                            _logger.LogWarning("Pass {Token} thuộc về client {Owner}, từ chối truy cập từ client {Other}",
                                passToken, pass.ClientIdentifier, clientIdentifier);
                            return Task.FromResult(false);
                        }

                        pass.LastSeenAt = now;
                        return Task.FromResult(true);
                    }
                    // Đã hết hạn -> xóa
                    state.ActivePasses.Remove(passToken);
                    PromoteAndCleanInternal(state, now);
                }
                return Task.FromResult(false);
            }
        }

        public Task<bool> ReleaseActivePassAsync(Guid showtimeId, string passToken)
        {
            if (string.IsNullOrWhiteSpace(passToken)) return Task.FromResult(false);

            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                bool removed = state.ActivePasses.Remove(passToken);
                if (removed)
                {
                    _logger.LogInformation("Showtime {ShowtimeId}: Đã giải phóng pass {Token}", showtimeId, passToken);
                    PromoteAndCleanInternal(state, DateTime.UtcNow);
                }
                return Task.FromResult(removed);
            }
        }

        public Task<bool> IsWaitingRoomRequiredAsync(Guid showtimeId)
        {
            if (!_options.Enabled) return Task.FromResult(false);

            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                PromoteAndCleanInternal(state, DateTime.UtcNow);
                int threshold = GetThreshold(state);
                return Task.FromResult(state.ActivePasses.Count >= threshold || state.Queue.Count > 0);
            }
        }

        public Task<WaitingRoomStatsDto> GetStatsAsync(Guid showtimeId)
        {
            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                PromoteAndCleanInternal(state, DateTime.UtcNow);
                int threshold = GetThreshold(state);
                return Task.FromResult(new WaitingRoomStatsDto
                {
                    ShowtimeId = showtimeId,
                    IsWaitingRoomActive = state.ActivePasses.Count >= threshold || state.Queue.Count > 0,
                    ActiveCount = state.ActivePasses.Count,
                    QueueLength = state.Queue.Count,
                    Threshold = threshold
                });
            }
        }

        public Task SetShowtimeThresholdAsync(Guid showtimeId, int threshold)
        {
            if (threshold <= 0) throw new ArgumentException("Threshold must be greater than 0.");

            var state = GetState(showtimeId);
            lock (state.LockObj)
            {
                state.CustomThreshold = threshold;
                PromoteAndCleanInternal(state, DateTime.UtcNow);
            }
            return Task.CompletedTask;
        }

        public void CleanupStaleSessions()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _states)
            {
                var state = kvp.Value;
                lock (state.LockObj)
                {
                    PromoteAndCleanInternal(state, now);
                }
            }
        }

        private void PromoteAndCleanInternal(ShowtimeQueueState state, DateTime now)
        {
            // 1. Dọn dẹp Active Passes đã hết hạn hoặc mất kết nối quá HeartbeatTimeoutSeconds
            var staleActiveCutoff = now.AddSeconds(-_options.HeartbeatTimeoutSeconds);
            var expiredPassKeys = state.ActivePasses
                .Where(p => p.Value.ExpiresAt <= now || p.Value.LastSeenAt < staleActiveCutoff)
                .Select(p => p.Key)
                .ToList();

            foreach (var key in expiredPassKeys)
            {
                state.ActivePasses.Remove(key);
                _logger.LogInformation("Đã dọn dẹp active pass {PassToken} do hết hạn hoặc mất kết nối (quá {Seconds}s)",
                    key, _options.HeartbeatTimeoutSeconds);
            }

            // 2. Dọn dẹp các ticket bỏ dở trong Queue (không gửi heartbeat quá hạn HeartbeatTimeoutSeconds)
            var staleHeartbeatCutoff = now.AddSeconds(-_options.HeartbeatTimeoutSeconds);
            var abandonedTickets = state.Queue
                .Where(t => t.LastHeartbeat < staleHeartbeatCutoff && t.Status == "WAITING")
                .ToList();

            foreach (var t in abandonedTickets)
            {
                t.Status = "CANCELLED";
                state.Queue.Remove(t);
                state.TicketsByToken.Remove(t.QueueToken);
                _logger.LogInformation("Hủy lượt hàng chờ {QueueToken} do mất kết nối (quá {Seconds}s)",
                    t.QueueToken, _options.HeartbeatTimeoutSeconds);
            }

            // 3. Promote người tiếp theo trong hàng đợi nếu còn chỗ trống
            int threshold = GetThreshold(state);
            while (state.ActivePasses.Count < threshold && state.Queue.Count > 0)
            {
                var nextTicket = state.Queue[0];
                state.Queue.RemoveAt(0);

                string passToken = "PASS_" + Guid.NewGuid().ToString("N");
                var passExpires = now.AddMinutes(_options.PassDurationMinutes);

                nextTicket.Status = "ADMITTED";
                nextTicket.AccessPassToken = passToken;
                nextTicket.PassExpiresAt = passExpires;

                state.ActivePasses[passToken] = new ActivePass
                {
                    Token = passToken,
                    UserId = nextTicket.UserId,
                    ClientIdentifier = nextTicket.ClientIdentifier,
                    AdmittedAt = now,
                    LastSeenAt = now,
                    ExpiresAt = passExpires
                };

                _logger.LogInformation("Promoted ticket {QueueToken} to ADMITTED with pass {PassToken}",
                    nextTicket.QueueToken, passToken);
            }
        }

        private int EstimateWaitTime(int position)
        {
            // Ước tính trung bình 30 giây mỗi lượt phục vụ
            return Math.Max(5, position * 30);
        }
    }
}
