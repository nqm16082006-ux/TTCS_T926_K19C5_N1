using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO phản hồi kết quả giữ chỗ ghế thành công kèm thời điểm hết hạn và thời gian máy chủ (Task T-23 / T-24 / T-32).
    /// </summary>
    public class HoldSeatsResponseDto
    {
        public Guid? ShowtimeId { get; set; }
        public List<Guid> SeatIds { get; set; } = new List<Guid>();
        public DateTime ExpiresAt { get; set; }
        public DateTime ServerTime { get; set; } = DateTime.UtcNow;
        public List<UserSeatHoldItemDto>? Holds { get; set; }
        public int MaxTicketsPerUser { get; set; } = 10;
        public int PurchasedCount { get; set; }
        public int ActiveHoldCount { get; set; }
        public int RemainingAllowance => Math.Max(0, MaxTicketsPerUser - (PurchasedCount + ActiveHoldCount));
    }

    public class UserSeatHoldItemDto
    {
        public Guid SeatId { get; set; }
        public string? Row { get; set; }
        public int? SeatNumber { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
