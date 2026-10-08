using System;
using System.Collections.Generic;
using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Giao diện dịch vụ ký và xác thực chữ ký mã QR vé điện tử (Story S-26).
    /// Sử dụng mật mã bất đối xứng ECDSA NIST P-256 (SHA-256).
    /// </summary>
    public interface IQrSignatureService
    {
        /// <summary>
        /// Ký số mã QR cho vé sử dụng khoá đang kích hoạt (ActiveKeyVersion).
        /// </summary>
        string SignTicket(string ticketCode, Guid showtimeId);

        /// <summary>
        /// Ký số với phiên bản khoá chỉ định (dùng cho testing hoặc mô phỏng vé cũ).
        /// </summary>
        string SignTicket(string ticketCode, Guid showtimeId, string keyVersion);

        /// <summary>
        /// Xác thực tính hợp lệ của mã QR (AC1, AC2, AC3).
        /// </summary>
        QrVerificationResult VerifyTicket(string qrPayload);

        /// <summary>
        /// Lấy toàn bộ khoá công khai (Public Keys) của hệ thống.
        /// </summary>
        IReadOnlyDictionary<string, string> GetPublicKeys();

        /// <summary>
        /// Lấy phiên bản khoá đang kích hoạt.
        /// </summary>
        string GetActiveKeyVersion();
    }
}
