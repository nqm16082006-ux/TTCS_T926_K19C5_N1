using System;

namespace EventTicketBooking.Api.DTOs
{
    public class WaitingRoomJoinRequestDto
    {
        public string? ClientSessionId { get; set; }
    }

    public class WaitingRoomHeartbeatRequestDto
    {
        public string QueueToken { get; set; } = string.Empty;
    }

    public class WaitingRoomLeaveRequestDto
    {
        public string? QueueToken { get; set; }
    }

    public class WaitingRoomReleaseRequestDto
    {
        public string? PassToken { get; set; }
    }

    public class WaitingRoomActivePingRequestDto
    {
        public string? PassToken { get; set; }
        public string? ClientSessionId { get; set; }
    }

    public class WaitingRoomStatusDto
    {
        public string QueueToken { get; set; } = string.Empty;
        public Guid ShowtimeId { get; set; }

        /// <summary>
        /// Trạng thái: WAITING, ADMITTED, EXPIRED, CANCELLED
        /// </summary>
        public string Status { get; set; } = "WAITING";

        /// <summary>
        /// Vị trí thứ mấy trong hàng (1-based, 0 nếu đã ADMITTED).
        /// </summary>
        public int Position { get; set; }

        /// <summary>
        /// Số người đang đứng phía trước trong hàng.
        /// </summary>
        public int PeopleAhead { get; set; }

        public int ActiveCount { get; set; }
        public int Threshold { get; set; }
        public int? EstimatedWaitSeconds { get; set; }

        /// <summary>
        /// Token thông hành khi đã đến lượt (ADMITTED) để truy cập sơ đồ và giữ ghế.
        /// </summary>
        public string? AccessPassToken { get; set; }

        /// <summary>
        /// Thời điểm hết hạn của lượt chọn ghế.
        /// </summary>
        public DateTime? PassExpiresAt { get; set; }

        public DateTime LastHeartbeat { get; set; }
    }

    public class WaitingRoomStatsDto
    {
        public Guid ShowtimeId { get; set; }
        public bool IsWaitingRoomActive { get; set; }
        public int ActiveCount { get; set; }
        public int QueueLength { get; set; }
        public int Threshold { get; set; }
    }

    public class UpdateThresholdRequestDto
    {
        public int Threshold { get; set; }
    }
}
