using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO phản hồi kết quả giữ chỗ ghế thành công kèm thời điểm hết hạn và thời gian máy chủ (Task T-23 / T-24).
    /// </summary>
    public class HoldSeatsResponseDto
    {
        public List<Guid> SeatIds { get; set; } = new List<Guid>();
        public DateTime ExpiresAt { get; set; }
        public DateTime ServerTime { get; set; } = DateTime.UtcNow;
    }
}
