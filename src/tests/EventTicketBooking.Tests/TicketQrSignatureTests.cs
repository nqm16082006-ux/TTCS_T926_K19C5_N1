using System;
using System.Collections.Generic;
using System.Linq;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử chấp nhận (Acceptance Tests) cho Story S-26:
    /// "Mã QR trên vé có chữ ký, không tạo giả được ngoài hệ thống"
    /// 
    /// Tiêu chuẩn nghiệm thu:
    /// - AC1: Nội dung QR gồm mã vé, mã suất và chữ ký; máy quét kiểm bằng khoá công khai; vé thật qua, sửa 1 ký tự bị từ chối.
    /// - AC2: Khoá ký bị đổi (Key Rotation), vé đã phát trước đó vẫn hợp lệ nhờ phiên bản khoá ghi trong QR.
    /// - AC3: Người tự tạo QR với mã vé hợp lệ nhưng không có chữ ký bị từ chối.
    /// - NFR: Khoá riêng chỉ nằm trên máy chủ; máy quét chỉ giữ khoá công khai.
    /// </summary>
    public class TicketQrSignatureTests
    {
        private readonly IQrSignatureService _service;

        public TicketQrSignatureTests()
        {
            _service = new QrSignatureService();
        }

        #region AC1: QR Payload có chữ ký số, vé thật hợp lệ, sửa 1 ký tự bị từ chối

        [Fact]
        public void S26_AC1_AuthenticTicket_VerifiesSuccessfully()
        {
            // Arrange
            var ticketCode = "TK-7H4K-92MB-PQ8X-W3ER";
            var showtimeId = Guid.NewGuid();

            // Act: Máy chủ ký mã QR cho vé
            var qrPayload = _service.SignTicket(ticketCode, showtimeId);

            // Assert: Kiểm tra định dạng và tính hợp lệ của vé thật
            Assert.NotNull(qrPayload);
            Assert.StartsWith("TICKET|v1|", qrPayload);
            Assert.Contains(ticketCode, qrPayload);
            Assert.Contains(showtimeId.ToString(), qrPayload);

            // Máy quét kiểm tra bằng Public Key
            var verifyResult = _service.VerifyTicket(qrPayload);
            Assert.True(verifyResult.IsValid);
            Assert.Equal(ticketCode, verifyResult.TicketCode);
            Assert.Equal(showtimeId, verifyResult.ShowtimeId);
            Assert.Equal("v1", verifyResult.KeyVersion);
            Assert.Null(verifyResult.ErrorReason);
        }

        [Fact]
        public void S26_AC1_TamperTicketCodeByOneCharacter_Rejected()
        {
            // Arrange
            var ticketCode = "TK-7H4K-92MB-PQ8X-W3ER";
            var showtimeId = Guid.NewGuid();
            var qrPayload = _service.SignTicket(ticketCode, showtimeId);

            // Giả sử kẻ gian sửa đúng 1 ký tự trong mã vé: '7' -> '8'
            var tamperedCode = "TK-8H4K-92MB-PQ8X-W3ER";
            var tamperedQrPayload = qrPayload.Replace(ticketCode, tamperedCode);

            // Act: Máy quét xác thực
            var verifyResult = _service.VerifyTicket(tamperedQrPayload);

            // Assert: Bị từ chối vì chữ ký không khớp
            Assert.False(verifyResult.IsValid);
            Assert.Equal("INVALID_SIGNATURE", verifyResult.ErrorReason);
        }

        [Fact]
        public void S26_AC1_TamperShowtimeIdByOneCharacter_Rejected()
        {
            // Arrange
            var ticketCode = "TK-7H4K-92MB-PQ8X-W3ER";
            var showtimeId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var qrPayload = _service.SignTicket(ticketCode, showtimeId);

            // Giả sử kẻ gian cố ý dùng vé của suất này cho suất khác (sửa 1 ký tự trong showtimeId: '1' -> '9')
            var tamperedShowtime = "91111111-2222-3333-4444-555555555555";
            var tamperedQrPayload = qrPayload.Replace(showtimeId.ToString(), tamperedShowtime);

            // Act: Máy quét xác thực
            var verifyResult = _service.VerifyTicket(tamperedQrPayload);

            // Assert: Bị từ chối vì chữ ký không khớp
            Assert.False(verifyResult.IsValid);
            Assert.Equal("INVALID_SIGNATURE", verifyResult.ErrorReason);
        }

        [Fact]
        public void S26_AC1_TamperSignatureByOneCharacter_Rejected()
        {
            // Arrange
            var ticketCode = "TK-7H4K-92MB-PQ8X-W3ER";
            var showtimeId = Guid.NewGuid();
            var qrPayload = _service.SignTicket(ticketCode, showtimeId);

            var parts = qrPayload.Split('|');
            var originalSig = parts[4];

            // Sửa 1 ký tự ở giữa chuỗi chữ ký (đảm bảo thay đổi bit dữ liệu thực của chữ ký ECDSA, tránh bit đệm cuối base64)
            var charArray = originalSig.ToCharArray();
            charArray[10] = charArray[10] == 'A' ? 'B' : 'A';
            var tamperedSig = new string(charArray);

            parts[4] = tamperedSig;
            var tamperedQrPayload = string.Join('|', parts);

            // Act: Máy quét xác thực
            var verifyResult = _service.VerifyTicket(tamperedQrPayload);

            // Assert: Bị từ chối ngay lập tức
            Assert.False(verifyResult.IsValid);
            Assert.Equal("INVALID_SIGNATURE", verifyResult.ErrorReason);
        }

        #endregion

        #region AC2: Key Rotation - Đổi khoá ký, vé phát trước đó vẫn hợp lệ nhờ Key Version

        [Fact]
        public void S26_AC2_KeyRotation_OldTicketRemainsValidWithOldKeyVersion()
        {
            // Arrange: Hệ thống đang ở version v1
            var service = new QrSignatureService();
            Assert.Equal("v1", service.GetActiveKeyVersion());

            var oldTicketCode = "TK-OLD1-TICK-ET25-0001";
            var oldShowtimeId = Guid.NewGuid();

            // Phát hành vé ở version v1
            var oldQrPayload = service.SignTicket(oldTicketCode, oldShowtimeId);
            Assert.StartsWith("TICKET|v1|", oldQrPayload);

            // Act: Ban tổ chức thực hiện Key Rotation (đổi khoá sang v2)
            service.SetActiveKeyVersion("v2");
            Assert.Equal("v2", service.GetActiveKeyVersion());

            // Phát hành vé mới ở version v2
            var newTicketCode = "TK-NEW2-TICK-ET26-0002";
            var newShowtimeId = Guid.NewGuid();
            var newQrPayload = service.SignTicket(newTicketCode, newShowtimeId);
            Assert.StartsWith("TICKET|v2|", newQrPayload);

            // Assert 1: Quét lại vé cũ (phát trước khi đổi khoá) -> Vẫn hợp lệ nhờ version v1 ghi trong QR
            var oldVerifyResult = service.VerifyTicket(oldQrPayload);
            Assert.True(oldVerifyResult.IsValid, "Vé cũ phát trước khi đổi khoá phải vẫn hợp lệ!");
            Assert.Equal(oldTicketCode, oldVerifyResult.TicketCode);
            Assert.Equal("v1", oldVerifyResult.KeyVersion);

            // Assert 2: Quét vé mới -> Hợp lệ với version v2
            var newVerifyResult = service.VerifyTicket(newQrPayload);
            Assert.True(newVerifyResult.IsValid, "Vé mới phát với khoá v2 phải hợp lệ!");
            Assert.Equal(newTicketCode, newVerifyResult.TicketCode);
            Assert.Equal("v2", newVerifyResult.KeyVersion);
        }

        [Fact]
        public void S26_AC2_UnknownKeyVersion_Rejected()
        {
            // Arrange: Kẻ gian tạo QR mạo nhận phiên bản khoá chưa từng có (v99)
            var ticketCode = "TK-FAKE-KEYV-ER99-0001";
            var showtimeId = Guid.NewGuid();
            var fakePayload = $"TICKET|v99|{ticketCode}|{showtimeId}|MEQCIDz3FakeSig123456789";

            // Act
            var verifyResult = _service.VerifyTicket(fakePayload);

            // Assert: Bị từ chối vì không tìm thấy khoá công khai tương ứng
            Assert.False(verifyResult.IsValid);
            Assert.Equal("UNKNOWN_KEY_VERSION", verifyResult.ErrorReason);
        }

        #endregion

        #region AC3: Mã QR không có chữ ký bị từ chối

        [Fact]
        public void S26_AC3_PlainTicketCodeWithoutSignature_Rejected()
        {
            // Arrange: Kẻ gian tự tạo QR chỉ in mã vé "TK-7H4K-92MB-PQ8X-W3ER" hợp lệ từ đơn hàng
            var plainQrPayload = "TK-7H4K-92MB-PQ8X-W3ER";

            // Act
            var verifyResult = _service.VerifyTicket(plainQrPayload);

            // Assert: Bị từ chối vì thiếu chữ ký số
            Assert.False(verifyResult.IsValid);
            Assert.Equal("MISSING_SIGNATURE", verifyResult.ErrorReason);
        }

        [Fact]
        public void S26_AC3_PipeFormatMissingSignatureField_Rejected()
        {
            // Arrange: Định dạng TICKET nhưng chỉ có mã vé và mã suất, không có trường chữ ký
            var showtimeId = Guid.NewGuid();
            var qrWithoutSig = $"TICKET|v1|TK-7H4K-92MB-PQ8X-W3ER|{showtimeId}";

            // Act
            var verifyResult = _service.VerifyTicket(qrWithoutSig);

            // Assert
            Assert.False(verifyResult.IsValid);
            Assert.Equal("MISSING_SIGNATURE", verifyResult.ErrorReason);
        }

        [Fact]
        public void S26_AC3_JsonFormatWithoutSignature_Rejected()
        {
            // Arrange: Định dạng JSON nhưng không có thuộc tính sig
            var showtimeId = Guid.NewGuid();
            var jsonWithoutSig = $"{{\"ticketCode\":\"TK-7H4K-92MB-PQ8X-W3ER\",\"showtimeId\":\"{showtimeId}\",\"keyVersion\":\"v1\"}}";

            // Act
            var verifyResult = _service.VerifyTicket(jsonWithoutSig);

            // Assert
            Assert.False(verifyResult.IsValid);
            Assert.Equal("MISSING_SIGNATURE", verifyResult.ErrorReason);
        }

        [Fact]
        public void S26_AC3_EmptyOrWhitespacePayload_Rejected()
        {
            Assert.False(_service.VerifyTicket("").IsValid);
            Assert.False(_service.VerifyTicket("   ").IsValid);
            Assert.False(_service.VerifyTicket(null!).IsValid);
        }

        #endregion

        #region NFR: Khoá riêng chỉ nằm trên máy chủ; máy quét chỉ giữ khoá công khai

        [Fact]
        public void S26_NFR_ScannerWithOnlyPublicKeys_CanVerifyTickets_CannotSign()
        {
            // Arrange 1: Lấy danh sách Public Keys từ máy chủ (chỉ public keys, không có private keys)
            var publicKeys = _service.GetPublicKeys();
            Assert.NotEmpty(publicKeys);

            // Khởi tạo một verifier độc lập đại diện cho máy quét ngoại tuyến (chỉ nạp Public Key)
            var scannerVerifier = QrSignatureService.CreateVerifierOnly(publicKeys);

            // Máy chủ ký một vé thật bằng Private Key
            var ticketCode = "TK-OFFL-INES-CAN2-6001";
            var showtimeId = Guid.NewGuid();
            var qrPayload = _service.SignTicket(ticketCode, showtimeId);

            // Act 1: Máy quét kiểm chữ ký vé thật bằng Public Key
            var verifyResult = scannerVerifier.VerifyTicket(qrPayload);

            // Assert 1: Máy quét xác thực thành công chỉ bằng Public Key
            Assert.True(verifyResult.IsValid);
            Assert.Equal(ticketCode, verifyResult.TicketCode);

            // Act 2: Kẻ gian can thiệp vào máy quét, cố dùng máy quét để ký vé giả mạo
            // Assert 2: Máy quét không có Private Key -> không thể sinh chữ ký giả mạo
            Assert.Throws<InvalidOperationException>(() =>
            {
                scannerVerifier.SignTicket("TK-FAKE-TICK-ET00-0000", Guid.NewGuid());
            });
        }

        #endregion

        #region Controller Integration Tests: TicketsController

        [Fact]
        public void S26_TicketsController_GetQrPublicKeys_ReturnsPublicKeys()
        {
            // Arrange
            var controller = new TicketsController(_service);

            // Act
            var actionResult = controller.GetQrPublicKeys();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<QrPublicKeysResponseDto>>(okResult.Value);

            Assert.True(apiResponse.Success);
            Assert.NotNull(apiResponse.Data);
            Assert.Equal("v1", apiResponse.Data.ActiveVersion);
            Assert.True(apiResponse.Data.PublicKeys.ContainsKey("v1"));
            Assert.True(apiResponse.Data.PublicKeys.ContainsKey("v2"));
        }

        [Fact]
        public void S26_TicketsController_VerifyQr_ValidPayload_ReturnsOk()
        {
            // Arrange
            var controller = new TicketsController(_service);
            var ticketCode = "TK-API1-VERI-FY26-0001";
            var showtimeId = Guid.NewGuid();
            var qrPayload = _service.SignTicket(ticketCode, showtimeId);

            // Act
            var actionResult = controller.VerifyTicketQr(new QrVerificationRequestDto { QrPayload = qrPayload });

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<QrVerificationResult>>(okResult.Value);

            Assert.True(apiResponse.Success);
            Assert.True(apiResponse.Data?.IsValid);
            Assert.Equal(ticketCode, apiResponse.Data?.TicketCode);
        }

        [Fact]
        public void S26_TicketsController_VerifyQr_TamperedPayload_ReturnsBadRequest()
        {
            // Arrange
            var controller = new TicketsController(_service);
            var ticketCode = "TK-API1-VERI-FY26-0001";
            var showtimeId = Guid.NewGuid();
            var qrPayload = _service.SignTicket(ticketCode, showtimeId);
            var tampered = qrPayload.Replace(ticketCode, "TK-FAKE-CODE-HERE-0000");

            // Act
            var actionResult = controller.VerifyTicketQr(new QrVerificationRequestDto { QrPayload = tampered });

            // Assert
            var badResult = Assert.IsType<BadRequestObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<QrVerificationResult>>(badResult.Value);

            Assert.False(apiResponse.Success);
            Assert.False(apiResponse.Data?.IsValid);
            Assert.Equal("INVALID_SIGNATURE", apiResponse.Data?.ErrorReason);
        }

        [Fact]
        public void S26_TicketsController_VerifyQr_EmptyPayload_ReturnsBadRequest()
        {
            // Arrange
            var controller = new TicketsController(_service);

            // Act
            var actionResult = controller.VerifyTicketQr(new QrVerificationRequestDto { QrPayload = "" });

            // Assert
            Assert.IsType<BadRequestObjectResult>(actionResult);
        }

        [Fact]
        public void S26_EndToEnd_TicketGenerationToQrVerification_Success()
        {
            // 1. Arrange & Generate via TicketService
            var ticketService = new TicketService(_service);
            var ticketCode = ticketService.GenerateTicketCode();
            var showtimeId = Guid.NewGuid();

            // 2. Generate signed QR Payload
            var qrPayload = ticketService.GenerateQrPayload(ticketCode, showtimeId);
            Assert.NotNull(qrPayload);
            Assert.StartsWith("TICKET|", qrPayload);

            // 3. Verify via TicketsController API
            var ticketsController = new TicketsController(_service);
            var result = ticketsController.VerifyTicketQr(new QrVerificationRequestDto { QrPayload = qrPayload });
            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<QrVerificationResult>>(okResult.Value);

            Assert.True(apiResponse.Success);
            Assert.True(apiResponse.Data?.IsValid);
            Assert.Equal(ticketCode, apiResponse.Data?.TicketCode);
            Assert.Equal(showtimeId, apiResponse.Data?.ShowtimeId);
            Assert.Equal("v1", apiResponse.Data?.KeyVersion);
        }

        #endregion
    }
}
