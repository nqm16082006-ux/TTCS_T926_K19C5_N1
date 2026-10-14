using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs;

public class DuplicateShowtimeDto
{
    [StringLength(200, ErrorMessage = "Tên suất diễn không được vượt quá 200 ký tự.")]
    public string? Name { get; set; }
    public DateTime? SalesEndTime { get; set; }

    [Required]
    public DateTime? StartTime { get; set; }
    [Required]
    public DateTime? EndTime { get; set; }
}
