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

    /// <summary>
    /// DTO yêu cầu đồng bộ lô các lượt soát vé ngoại tuyến (Story S-35).
    /// </summary>
    public class OfflineSyncBatchRequestDto
    {
        public List<OfflineScanBatchItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// Mỗi phần tử lượt quét ngoại tuyến được đẩy lên từ thiết bị.
    /// qrPayload là bắt buộc để server xác thực lại chữ ký số (S-26/S-35).
    /// </summary>
    public class OfflineScanBatchItemDto
    {
        public string OfflineScanId { get; set; } = string.Empty;
        public string TicketCode { get; set; } = string.Empty;
        public Guid ShowtimeId { get; set; }
        public string GateName { get; set; } = string.Empty;
        public DateTimeOffset ScannedAt { get; set; }
        public string QrPayload { get; set; } = string.Empty;
    }

    /// <summary>
    /// DTO phản hồi kết quả đồng bộ theo lô (Story S-35).
    /// </summary>
    public class OfflineSyncBatchResponseDto
    {
        public int TotalSubmitted { get; set; }
        public int SyncedCount { get; set; }
        public int DuplicateCount { get; set; }
        public int ConflictCount { get; set; }
        public int RejectedCount { get; set; }
        public List<OfflineSyncItemResultDto> Results { get; set; } = new();
    }

    /// <summary>
    /// Kết quả xử lý cho từng lần quét ngoại tuyến trong lô.
    /// </summary>
    public class OfflineSyncItemResultDto
    {
        public string OfflineScanId { get; set; } = string.Empty;
        public string TicketCode { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "synced", "duplicate", "conflict", "rejected"
        public string? Reason { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool IsAckTerminal { get; set; }

        public string? SeatInfo { get; set; }
        public string? CategoryName { get; set; }
        public string? Gate { get; set; }
        public DateTimeOffset? CheckInTime { get; set; }
    }
}
