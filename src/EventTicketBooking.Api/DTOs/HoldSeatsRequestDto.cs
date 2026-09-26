using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// DTO yêu cầu giữ chỗ danh sách ghế (Task T-23).
    /// </summary>
    public class HoldSeatsRequestDto
    {
        [Required(ErrorMessage = "Danh sách ghế không được để trống.")]
        public List<Guid> SeatIds { get; set; } = new List<Guid>();
    }
}
