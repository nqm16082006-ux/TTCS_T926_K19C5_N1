using System;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// Tham số truy vấn xuất danh sách soát vé theo suất diễn (Story S-62).
    /// </summary>
    public class ExportCheckinListQuery
    {
        /// <summary>
        /// ID suất diễn cần xuất danh sách (ShowId / Suất diễn ID).
        /// </summary>
        public Guid ShowId { get; set; }
        public bool IncludeScanLog { get; set; }

        /// <summary>
        /// Bí danh cho ShowId (ShowtimeId).
        /// </summary>
        public Guid? ShowtimeId
        {
            get => ShowId;
            set
            {
                if (value.HasValue && value.Value != Guid.Empty)
                {
                    ShowId = value.Value;
                }
            }
        }

        /// <summary>
        /// Tùy chọn lọc theo tên cửa (GateName).
        /// </summary>
        public string? GateName { get; set; }

        /// <summary>
        /// Tùy chọn lọc theo tên cửa (DoorName - tương đương GateName).
        /// </summary>
        public string? DoorName
        {
            get => GateName;
            set => GateName = value;
        }

        /// <summary>
        /// Tùy chọn lọc theo mã cửa/cổng (GateId).
        /// </summary>
        public string? GateId { get; set; }

        /// <summary>
        /// Tùy chọn lọc theo mã cửa/cổng (DoorId - tương đương GateId).
        /// </summary>
        public string? DoorId
        {
            get => GateId;
            set => GateId = value;
        }

        /// <summary>
        /// Tùy chọn chỉ xuất các bản ghi có cờ xung đột.
        /// </summary>
        public bool? OnlyConflicts { get; set; }

        /// <summary>
        /// Tùy chọn xuất cả các vé chưa check-in (mặc định false - chỉ xuất khách đã vào cửa).
        /// </summary>
        public bool IncludeNotCheckedIn { get; set; } = false;

        /// <summary>
        /// Lấy giá trị lọc cửa/cổng hiệu lực từ GateName, DoorName, GateId hoặc DoorId.
        /// </summary>
        public string? EffectiveGateFilter
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(GateName)) return GateName.Trim();
                if (!string.IsNullOrWhiteSpace(GateId)) return GateId.Trim();
                return null;
            }
        }
    }
}
