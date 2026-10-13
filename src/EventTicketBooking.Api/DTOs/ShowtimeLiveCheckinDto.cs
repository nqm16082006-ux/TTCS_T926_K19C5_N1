using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// Story S-52: DTO phản hồi số liệu khách vào cửa thời gian thực cho Ban tổ chức.
    /// </summary>
    public class ShowtimeLiveCheckinDto
    {
        public Guid ShowtimeId { get; set; }
        public Guid EventId { get; set; }
        public string EventTitle { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        // 1. Số liệu tổng quan (Overview)
        public int TotalSoldTickets { get; set; }
        public int CheckedInCount { get; set; }
        public int RemainingCount { get; set; }
        public double CheckInPercentage { get; set; }

        // 2. Vận hành theo thời gian (Velocity / Throughput)
        public double CurrentVelocityPerMinute { get; set; }
        public List<CheckinTimelinePointDto> Timeline { get; set; } = new();

        // 3. Thống kê theo Cổng / Lối vào (Gate Breakdown)
        public List<GateCheckinStatDto> GateBreakdown { get; set; } = new();

        // 4. Trạng thái đồng bộ ngoại tuyến (Offline Handling)
        public int OfflineSyncedCount { get; set; }
        public DateTimeOffset? LastSyncTime { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
    }

    public class GateCheckinStatDto
    {
        public string GateName { get; set; } = "Cổng chính";
        public int CheckedInCount { get; set; }
        public double PercentageOfTotal { get; set; }
    }

    public class CheckinTimelinePointDto
    {
        public DateTime MinuteBucket { get; set; }
        public int Count { get; set; }
    }
}
