using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO phản hồi danh sách vé phục vụ máy quét ngoại tuyến (Story S-33).
    /// Tuyệt đối không chứa bất kỳ thông tin cá nhân khách hàng (PII) nào.
    /// </summary>
    public class OfflineTicketSyncResponseDto
    {
        public Guid ShowtimeId { get; set; }
        public DateTimeOffset ServerTime { get; set; }
        public bool IsDelta { get; set; }
        public int TotalCount { get; set; }
        public List<OfflineTicketItemDto> Tickets { get; set; } = new();
    }

    /// <summary>
    /// Thông tin vé tối thiểu cho thiết bị quét ngoại tuyến.
    /// </summary>
    public class OfflineTicketItemDto
    {
        public string TicketCode { get; set; } = string.Empty;
        public bool IsCheckedIn { get; set; }
    }
}
