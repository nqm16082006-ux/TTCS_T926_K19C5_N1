using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Bản ghi đồng bộ soát vé ngoại tuyến (User Story S-35).
    /// Lưu trữ khóa chống trùng lặp OfflineScanId và snapshot xung đột cho Story S-36.
    /// </summary>
    public class OfflineCheckInRecord
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string OfflineScanId { get; set; } = string.Empty;
        public Guid OrderItemId { get; set; }
        public string TicketCode { get; set; } = string.Empty;
        public Guid ShowtimeId { get; set; }
        public string GateName { get; set; } = string.Empty;
        public DateTimeOffset ScannedAtDevice { get; set; }
        public DateTimeOffset ReceivedAtServer { get; set; } = DateTimeOffset.UtcNow;
        public Guid SyncedByUserId { get; set; }

        /// <summary>
        /// Trạng thái ghi nhận trong DB: "SYNCED" hoặc "CONFLICT". Tuyệt đối không lưu "DUPLICATE".
        /// </summary>
        public string Status { get; set; } = string.Empty;
        public bool IsConflict { get; set; }
        public string? ConflictReason { get; set; }

        public string? ExistingCheckInGate { get; set; }
        public DateTimeOffset? ExistingCheckInTime { get; set; }

        // Navigation properties
        public OrderItem OrderItem { get; set; } = null!;
        public Showtime Showtime { get; set; } = null!;
        public User SyncedByUser { get; set; } = null!;
    }
}
