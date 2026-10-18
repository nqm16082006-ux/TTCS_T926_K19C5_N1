using System;
using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    public class JoinWaitlistRequestDto
    {
        [Required(ErrorMessage = "Mã suất diễn là bắt buộc.")]
        public Guid ShowtimeId { get; set; }

        [Required(ErrorMessage = "Email là bắt buộc.")]
        [EmailAddress(ErrorMessage = "Định dạng email không hợp lệ.")]
        [MaxLength(255)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(255)]
        public string? FullName { get; set; }

        [MaxLength(50)]
        public string? PhoneNumber { get; set; }
    }

    public class WaitlistRegistrationResultDto
    {
        public Guid Id { get; set; }
        public Guid ShowtimeId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public int QueuePosition { get; set; }
        public int TotalWaiting { get; set; }
        public string Status { get; set; } = "WAITING";
        public bool IsExisting { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class WaitlistStatusResponseDto
    {
        public Guid ShowtimeId { get; set; }
        public bool IsSoldOut { get; set; }
        public int AvailableSeats { get; set; }
        public int TotalWaiting { get; set; }
        public bool IsUserWaiting { get; set; }
        public int? UserQueuePosition { get; set; }
        public string? Message { get; set; }
    }

    public class WaitlistEntryDto
    {
        public Guid Id { get; set; }
        public Guid ShowtimeId { get; set; }
        public Guid? UserId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? PhoneNumber { get; set; }
        public string Status { get; set; } = "WAITING";
        public int QueuePosition { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? NotifiedAt { get; set; }
        public int NotificationCount { get; set; }
    }
}
