using System;
using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// Yêu cầu chuyển nhượng vé cho người khác (Story S-57).
    /// </summary>
    public class TransferTicketRequestDto
    {
        [Required(ErrorMessage = "Email người nhận là bắt buộc.")]
        [EmailAddress(ErrorMessage = "Email người nhận không hợp lệ.")]
        [MaxLength(150, ErrorMessage = "Email tối đa 150 ký tự.")]
        public string RecipientEmail { get; set; } = string.Empty;

        [MaxLength(500, ErrorMessage = "Ghi chú tối đa 500 ký tự.")]
        public string? Note { get; set; }
    }

    /// <summary>
    /// Kết quả chuyển nhượng vé thành công (Story S-57).
    /// </summary>
    public class TicketTransferResultDto
    {
        public Guid TransferId { get; set; }
        public Guid TicketId { get; set; }
        public string OldTicketCode { get; set; } = string.Empty;
        public string NewTicketCode { get; set; } = string.Empty;
        public string FromEmail { get; set; } = string.Empty;
        public string ToEmail { get; set; } = string.Empty;
        public string? RecipientName { get; set; }
        public DateTimeOffset TransferredAt { get; set; }
        public string Message { get; set; } = "Chuyển nhượng vé thành công.";
    }

    /// <summary>
    /// Thông tin lịch sử chuyển nhượng vé (Story S-57).
    /// </summary>
    public class TicketTransferHistoryDto
    {
        public Guid TransferId { get; set; }
        public Guid TicketId { get; set; }
        public string FromEmail { get; set; } = string.Empty;
        public string ToEmail { get; set; } = string.Empty;
        public string OldTicketCode { get; set; } = string.Empty;
        public string NewTicketCode { get; set; } = string.Empty;
        public string? Note { get; set; }
        public DateTimeOffset TransferredAt { get; set; }
    }
}
