using System;
using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    public class CreateDataDeletionRequestDto
    {
        [MaxLength(1000, ErrorMessage = "Lý do không được vượt quá 1000 ký tự.")]
        public string? Reason { get; set; }
    }

    public class ReviewDataDeletionRequestDto
    {
        [Required(ErrorMessage = "Trạng thái phê duyệt là bắt buộc.")]
        public bool Approved { get; set; }

        [MaxLength(1000, ErrorMessage = "Ghi chú không được vượt quá 1000 ký tự.")]
        public string? AdminNote { get; set; }
    }

    public class DataDeletionRequestDto
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string RequesterEmail { get; set; } = string.Empty;
        public string? RequesterName { get; set; }
        public string? Reason { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public Guid? ReviewedByUserId { get; set; }
        public string? ReviewedByAdminEmail { get; set; }
        public string? AdminNote { get; set; }
    }

    public class DataDeletionStatusDto
    {
        public bool HasPendingRequest { get; set; }
        public bool IsAnonymized { get; set; }
        public DateTime? AnonymizedAt { get; set; }
        public DataDeletionRequestDto? LatestRequest { get; set; }
    }
}
