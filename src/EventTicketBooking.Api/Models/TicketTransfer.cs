using System;

namespace EventTicketBooking.Api.Models
{
    public enum TransferStatus
    {
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Story S-57: Bản ghi lịch sử chuyển nhượng vé cho người khác.
    /// </summary>
    public class TicketTransfer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TicketId { get; set; }
        public Guid FromUserId { get; set; }
        public Guid ToUserId { get; set; }
        public string FromUserEmail { get; set; } = string.Empty;
        public string ToUserEmail { get; set; } = string.Empty;
        public string OldTicketCode { get; set; } = string.Empty;
        public string NewTicketCode { get; set; } = string.Empty;
        public TransferStatus Status { get; set; } = TransferStatus.Completed;
        public string? Note { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Ticket Ticket { get; set; } = null!;
        public User FromUser { get; set; } = null!;
        public User ToUser { get; set; } = null!;
    }
}
