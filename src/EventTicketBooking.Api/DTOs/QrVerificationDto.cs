using System;
using System.Collections.Generic;

namespace EventTicketBooking.Api.DTOs
{
    /// <summary>
    /// Yêu cầu xác thực chữ ký mã QR (Story S-26).
    /// </summary>
    public class QrVerificationRequestDto
    {
        public string QrPayload { get; set; } = string.Empty;
    }

    /// <summary>
    /// Kết quả xác thực chữ ký mã QR vé (Story S-26 AC1, AC2, AC3).
    /// </summary>
    public class QrVerificationResult
    {
        public bool IsValid { get; set; }
        public string? TicketCode { get; set; }
        public Guid? ShowtimeId { get; set; }
        public string? KeyVersion { get; set; }
        public string? ErrorReason { get; set; }
        public string Message { get; set; } = string.Empty;

        public static QrVerificationResult Success(string ticketCode, Guid showtimeId, string keyVersion) =>
            new()
            {
                IsValid = true,
                TicketCode = ticketCode,
                ShowtimeId = showtimeId,
                KeyVersion = keyVersion,
                Message = "Chữ ký mã QR hợp lệ."
            };

        public static QrVerificationResult Failure(
            string errorReason,
            string message,
            string? keyVersion = null,
            string? ticketCode = null,
            Guid? showtimeId = null) =>
            new()
            {
                IsValid = false,
                ErrorReason = errorReason,
                Message = message,
                KeyVersion = keyVersion,
                TicketCode = ticketCode,
                ShowtimeId = showtimeId
            };
    }

    /// <summary>
    /// Danh sách các Public Key công khai của hệ thống để máy quét tải về lưu offline (S-26 NFR, S-34).
    /// </summary>
    public class QrPublicKeysResponseDto
    {
        public string ActiveVersion { get; set; } = string.Empty;
        public Dictionary<string, string> PublicKeys { get; set; } = new();
    }
}
