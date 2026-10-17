using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service nghiệp vụ cho Story S-51: Đối soát giao dịch hệ thống với sao kê cổng thanh toán.
    /// </summary>
    public interface IReconciliationService
    {
        /// <summary>
        /// Nạp và chuẩn hóa dữ liệu giao dịch nội bộ từ payment_events và payment_transactions trong kỳ.
        /// Chống trùng lặp và xác minh cách lấy số tiền (RawPayload hoặc PaymentTransaction).
        /// </summary>
        Task<Dictionary<string, InternalTransactionRecord>> GetInternalTransactionsAsync(
            DateTimeOffset fromDate,
            DateTimeOffset toDate,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Đọc và phân tích cú pháp tệp CSV sao kê cổng thanh toán.
        /// </summary>
        (List<GatewayStatementItemDto> items, List<string> errors) ParseCsvStatement(string csvContent);

        /// <summary>
        /// Thực thi thuật toán so khớp đối soát giữa dữ liệu nội bộ và sao kê cổng.
        /// Phân loại 4 nhóm: Matched, SystemOnly, GatewayOnly, AmountMismatch.
        /// </summary>
        Task<ReconciliationSummaryDto> ReconcileAsync(
            DateTimeOffset fromDate,
            DateTimeOffset toDate,
            List<GatewayStatementItemDto> gatewayItems,
            List<string>? parseWarnings = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Xuất báo cáo kết quả đối soát thành định dạng CSV.
        /// </summary>
        string ExportSummaryToCsv(ReconciliationSummaryDto summary);

        /// <summary>
        /// Lưu phiên đối soát bất biến theo kỳ vào CSDL.
        /// </summary>
        Task<EventTicketBooking.Api.Models.ReconciliationSession> SaveReconciliationSessionAsync(
            DateTimeOffset fromDate,
            DateTimeOffset toDate,
            string sourceType,
            string rawCsv,
            ReconciliationSummaryDto summary,
            string? executedBy,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy danh sách các phiên đối soát gần đây.
        /// </summary>
        Task<List<ReconciliationSessionDto>> GetRecentSessionsAsync(
            int limit = 20,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lấy chi tiết một phiên đối soát theo ID.
        /// </summary>
        Task<EventTicketBooking.Api.Models.ReconciliationSession?> GetSessionByIdAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default);
    }
}
