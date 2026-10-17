using System;

namespace EventTicketBooking.Api.Models
{
    /// <summary>
    /// Phiên đối soát giao dịch hệ thống với sao kê cổng thanh toán (Story S-51).
    /// Lưu trữ snapshot lịch sử bất biến theo kỳ đối soát.
    /// </summary>
    public class ReconciliationSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Mã phiên đối soát, ví dụ: REC_20261017_A1B2C3</summary>
        public string SessionCode { get; set; } = string.Empty;

        /// <summary>Thời gian bắt đầu kỳ đối soát</summary>
        public DateTimeOffset PeriodFrom { get; set; }

        /// <summary>Thời gian kết thúc kỳ đối soát</summary>
        public DateTimeOffset PeriodTo { get; set; }

        /// <summary>Nguồn sao kê: "USER_FILE_CSV" hoặc "USER_TEXT_CSV" (Kế toán nạp tệp CSV)</summary>
        public string SourceType { get; set; } = "USER_FILE_CSV";

        /// <summary>Trạng thái phiên: "COMPLETED", "IN_PROGRESS"</summary>
        public string Status { get; set; } = "COMPLETED";

        /// <summary>Tổng số giao dịch nội bộ hợp lệ trong kỳ</summary>
        public int TotalInternalCount { get; set; }

        /// <summary>Tổng tiền nội bộ hợp lệ</summary>
        public long TotalInternalAmount { get; set; }

        /// <summary>Tổng số giao dịch sao kê cổng</summary>
        public int TotalGatewayCount { get; set; }

        /// <summary>Tổng tiền sao kê cổng</summary>
        public long TotalGatewayAmount { get; set; }

        /// <summary>Số lượng khớp hoàn toàn</summary>
        public int MatchedCount { get; set; }

        /// <summary>Số lượng chỉ có ở hệ thống</summary>
        public int SystemOnlyCount { get; set; }

        /// <summary>Số lượng chỉ có ở cổng</summary>
        public int GatewayOnlyCount { get; set; }

        /// <summary>Số lượng lệch tiền</summary>
        public int AmountMismatchCount { get; set; }

        /// <summary>Số lượng giao dịch nội bộ chưa đủ điều kiện (Pending/Failed/lacks ID)</summary>
        public int IneligibleCount { get; set; }

        /// <summary>Nội dung tệp sao kê CSV snapshot dùng cho phiên này (Bất biến)</summary>
        public string RawCsv { get; set; } = string.Empty;

        /// <summary>Báo cáo đối soát chi tiết dạng JSON (ReconciliationSummaryDto)</summary>
        public string SummaryJson { get; set; } = string.Empty;

        /// <summary>Thời điểm thực hiện đối soát</summary>
        public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Tên hoặc định danh người thực hiện (Auditor/Admin)</summary>
        public string? ExecutedBy { get; set; }
    }
}
