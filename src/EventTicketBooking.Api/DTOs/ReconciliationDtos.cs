using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    public static class ReconciliationGroups
    {
        public const string Matched = "Matched";
        public const string SystemOnly = "SystemOnly";
        public const string GatewayOnly = "GatewayOnly";
        public const string AmountMismatch = "AmountMismatch";
        public const string Ineligible = "Ineligible";
    }

    /// <summary>
    /// Giao dịch sao kê từ phía cổng thanh toán.
    /// </summary>
    public class GatewayStatementItemDto
    {
        public string TransactionId { get; set; } = string.Empty;
        public long? OrderCode { get; set; }
        public int Amount { get; set; }
        public DateTimeOffset? TransactionDate { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Bản ghi giao dịch nội bộ đã chuẩn hóa từ payment_events và payment_transactions.
    /// </summary>
    public class InternalTransactionRecord
    {
        public string TransactionId { get; set; } = string.Empty;
        public int? Amount { get; set; }
        public long? OrderCode { get; set; }
        public Guid? OrderId { get; set; }
        public string? OrderStatus { get; set; }
        public DateTimeOffset EventDate { get; set; }
        public string Source { get; set; } = "Unknown"; // "PaymentEvent", "PaymentTransaction", "Both"
        public bool HasAmountWarning { get; set; }
        public string? WarningMessage { get; set; }
        public bool IsEligible { get; set; } = true;
        public string? IneligibilityReason { get; set; }
    }

    /// <summary>
    /// Từng dòng kết quả đối soát so sánh hai bên.
    /// </summary>
    public class ReconciliationItemDto
    {
        public string TransactionId { get; set; } = string.Empty;
        public long? OrderCode { get; set; }
        public Guid? OrderId { get; set; }
        public int? InternalAmount { get; set; }
        public int? GatewayAmount { get; set; }
        public int? Difference { get; set; } // GatewayAmount - InternalAmount
        public string Group { get; set; } = string.Empty; // Matched, SystemOnly, GatewayOnly, AmountMismatch, Ineligible
        public DateTimeOffset? InternalDate { get; set; }
        public DateTimeOffset? GatewayDate { get; set; }
        public string? Status { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Tổng hợp báo cáo đối soát theo kỳ.
    /// </summary>
    public class ReconciliationSummaryDto
    {
        public DateTimeOffset FromDate { get; set; }
        public DateTimeOffset ToDate { get; set; }
        public DateTimeOffset ReconciledAt { get; set; } = DateTimeOffset.UtcNow;
        public Guid? SessionId { get; set; }
        public string? SessionCode { get; set; }
        public bool IsMockStatement { get; set; }

        // Thống kê tổng quan
        public int TotalInternalCount { get; set; }
        public long TotalInternalAmount { get; set; }
        public int TotalGatewayCount { get; set; }
        public long TotalGatewayAmount { get; set; }

        // Nhóm 1: Khớp hoàn toàn
        public int MatchedCount { get; set; }
        public long MatchedInternalAmount { get; set; }
        public long MatchedGatewayAmount { get; set; }

        // Nhóm 2: Có ở hệ thống, thiếu ở cổng
        public int SystemOnlyCount { get; set; }
        public long SystemOnlyAmount { get; set; }

        // Nhóm 3: Có ở cổng, thiếu ở hệ thống
        public int GatewayOnlyCount { get; set; }
        public long GatewayOnlyAmount { get; set; }

        // Nhóm 4: Lệch số tiền
        public int AmountMismatchCount { get; set; }
        public long AmountMismatchInternalAmount { get; set; }
        public long AmountMismatchGatewayAmount { get; set; }
        public long AmountMismatchNetDifference { get; set; }

        // Nhóm 5: Giao dịch nội bộ chưa đủ điều kiện (Pending/Failed/lacks ID)
        public int IneligibleCount { get; set; }

        // Chi tiết từng dòng giao dịch
        public List<ReconciliationItemDto> Items { get; set; } = new();

        // Cảnh báo các dòng CSV không hợp lệ hoặc thông báo nghiệp vụ
        public List<string> Warnings { get; set; } = new();
    }


    /// <summary>
    /// Thông tin tóm tắt phiên đối soát lịch sử.
    /// </summary>
    public class ReconciliationSessionDto
    {
        public Guid Id { get; set; }
        public string SessionCode { get; set; } = string.Empty;
        public DateTimeOffset PeriodFrom { get; set; }
        public DateTimeOffset PeriodTo { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int TotalInternalCount { get; set; }
        public long TotalInternalAmount { get; set; }
        public int TotalGatewayCount { get; set; }
        public long TotalGatewayAmount { get; set; }
        public int MatchedCount { get; set; }
        public int SystemOnlyCount { get; set; }
        public int GatewayOnlyCount { get; set; }
        public int AmountMismatchCount { get; set; }
        public int IneligibleCount { get; set; }
        public DateTimeOffset ExecutedAt { get; set; }
        public string? ExecutedBy { get; set; }
        public bool IsMockStatement => string.Equals(SourceType, "MOCK_GATEWAY", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(SourceType, "MockStatement", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Chi tiết phiên đối soát lịch sử (kèm dữ liệu báo cáo chi tiết).
    /// </summary>
    public class ReconciliationSessionDetailDto : ReconciliationSessionDto
    {
        public string RawCsv { get; set; } = string.Empty;
        public string SummaryJson { get; set; } = string.Empty;
        public ReconciliationSummaryDto? Summary { get; set; }
    }
}
