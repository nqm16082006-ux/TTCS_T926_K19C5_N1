namespace EventTicketBooking.Api.Options
{
    public class WaitingRoomOptions
    {
        public const string SectionName = "WaitingRoom";

        /// <summary>
        /// Bật hoặc tắt tính năng phòng chờ hàng đợi toàn cục.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Ngưỡng số phiên người dùng hoạt động cùng lúc mặc định cho mỗi suất diễn.
        /// Vượt quá ngưỡng này sẽ kích hoạt phòng chờ.
        /// </summary>
        public int DefaultActiveThreshold { get; set; } = 20;

        /// <summary>
        /// Thời hạn của lượt vào chọn ghế sau khi được cấp (phút), mặc định 10 phút.
        /// </summary>
        public int PassDurationMinutes { get; set; } = 10;

        /// <summary>
        /// Thời gian quá hạn không nhận heartbeat từ client trong hàng chờ (giây).
        /// Sau khoảng thời gian này, client bị coi là đã rời bỏ và bị hủy lượt.
        /// </summary>
        public int HeartbeatTimeoutSeconds { get; set; } = 45;

        /// <summary>
        /// Sức chứa tối đa của hàng chờ cho mỗi suất chiếu (để chống tràn bộ nhớ do bot).
        /// </summary>
        public int MaxQueueCapacity { get; set; } = 2000;
    }
}
