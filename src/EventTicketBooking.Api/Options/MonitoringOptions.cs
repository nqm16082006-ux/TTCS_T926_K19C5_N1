namespace EventTicketBooking.Api.Options
{
    public class MonitoringOptions
    {
        public const string SectionName = "Monitoring";

        public bool Enabled { get; set; } = true;
        public int RetentionHours { get; set; } = 72;
        public int AlertEvaluationMinutes { get; set; } = 1;
        public MonitoringThresholds Thresholds { get; set; } = new();
    }

    public class MonitoringThresholds
    {
        public double ErrorRatePercent { get; set; } = 5.0;
        public double P95Milliseconds { get; set; } = 1000.0;
        public double SeatConflicts15m { get; set; } = 20.0;
        public double RejectedWebhooks15m { get; set; } = 5.0;
    }
}
