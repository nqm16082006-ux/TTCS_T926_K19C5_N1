using System;

namespace EventTicketBooking.Api.Models
{
    public class Showtime
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid EventId { get; set; }

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        public int AvailableSeats { get; set; }

        public ShowtimeStatus Status { get; private set; } = ShowtimeStatus.Draft;

        public Event Event { get; set; } = null!;

        /// <summary>
        /// Phương thức duy nhất chịu trách nhiệm chuyển trạng thái và kiểm tra điều kiện cho suất diễn (Task T-15).
        /// </summary>
        public void ChangeStatus(ShowtimeStatus newStatus)
        {
            if (Status == newStatus)
            {
                throw new InvalidOperationException($"Suất diễn đã ở trạng thái {Status}.");
            }

            if (Status == ShowtimeStatus.Draft && newStatus == ShowtimeStatus.OnSale)
            {
                if (AvailableSeats <= 0)
                {
                    throw new InvalidOperationException("Không thể chuyển sang trạng thái Đang bán vì suất diễn chưa có ghế.");
                }
                Status = ShowtimeStatus.OnSale;
                return;
            }

            if (Status == ShowtimeStatus.OnSale && newStatus == ShowtimeStatus.Closed)
            {
                Status = ShowtimeStatus.Closed;
                return;
            }

            throw new InvalidOperationException($"Không thể chuyển trạng thái suất diễn từ {Status} sang {newStatus}.");
        }
    }
}
