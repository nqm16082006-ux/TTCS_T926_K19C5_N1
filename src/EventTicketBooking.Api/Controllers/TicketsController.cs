using System;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/tickets")]
    public class TicketsController : ControllerBase
    {
        private readonly IQrSignatureService _qrSignatureService;

        public TicketsController(IQrSignatureService qrSignatureService)
        {
            _qrSignatureService = qrSignatureService;
        }

        /// <summary>
        /// S-26 NFR & S-33: API lấy danh sách khoá công khai (Public Keys) của hệ thống.
        /// Thiết bị quét vé lưu khoá công khai này để soát vé ngoại tuyến mà không cần kết nối máy chủ.
        /// </summary>
        [HttpGet("qr-public-keys")]
        [ProducesResponseType(typeof(ApiResponse<QrPublicKeysResponseDto>), StatusCodes.Status200OK)]
        public IActionResult GetQrPublicKeys()
        {
            var publicKeys = _qrSignatureService.GetPublicKeys();
            var activeVersion = _qrSignatureService.GetActiveKeyVersion();

            var response = new QrPublicKeysResponseDto
            {
                ActiveVersion = activeVersion,
                PublicKeys = publicKeys.ToDictionary(k => k.Key, v => v.Value)
            };

            return Ok(ApiResponse<QrPublicKeysResponseDto>.SuccessResult(response, "Lấy danh sách khoá công khai thành công."));
        }

        /// <summary>
        /// S-26: API xác thực chữ ký mã QR vé điện tử (AC1, AC2, AC3).
        /// </summary>
        [HttpPost("verify-qr")]
        [ProducesResponseType(typeof(ApiResponse<QrVerificationResult>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<QrVerificationResult>), StatusCodes.Status400BadRequest)]
        public IActionResult VerifyTicketQr([FromBody] QrVerificationRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.QrPayload))
            {
                return BadRequest(ApiResponse<QrVerificationResult>.FailureResult("Nội dung mã QR không được để trống."));
            }

            var result = _qrSignatureService.VerifyTicket(request.QrPayload);

            if (!result.IsValid)
            {
                return BadRequest(new ApiResponse<QrVerificationResult>
                {
                    Success = false,
                    Message = result.Message,
                    Data = result,
                    Errors = result.ErrorReason
                });
            }

            return Ok(ApiResponse<QrVerificationResult>.SuccessResult(result, "Xác thực mã QR thành công."));
        }
    }
}
