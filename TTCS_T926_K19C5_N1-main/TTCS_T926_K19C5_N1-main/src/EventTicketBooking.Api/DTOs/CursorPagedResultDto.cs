using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public class CursorPagedResultDto<T>
    {
        public List<T> Items { get; set; } = new List<T>();

        public string? NextCursor { get; set; }

        public bool HasMore { get; set; }
    }
}
