using System;
using System.Linq;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/v1/tickets")]
    public class TicketsController : ControllerBase
    {
        private readonly IQrSignatureService _qrSignatureService;
        private readonly ITicketTransferService _ticketTransferService;

        public TicketsController(IQrSignatureService qrSignatureService)
            : this(qrSignatureService, null!)
        {
        }

        [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
        public TicketsController(
            IQrSignatureService qrSignatureService,
            ITicketTransferService ticketTransferService)
        {
            _qrSignatureService = qrSignatureService;
            _ticketTransferService = ticketTransferService;
        }

        /// <summary>
        /// S-57: API chuyển nhượng vé cho người khác qua email người nhận.
        /// POST /api/v1/tickets/{ticketId}/transfer
        /// </summary>
        [HttpPost("{ticketId:guid}/transfer")]
        [EventTicketBooking.Api.Middlewares.RequireRole]
        [ProducesResponseType(typeof(ApiResponse<TicketTransferResultDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<TicketTransferResultDto>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> TransferTicket(
            [FromRoute] Guid ticketId,
            [FromBody] TransferTicketRequestDto request)
        {
            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("id")?.Value
                ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

            if (!Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized(ApiResponse<TicketTransferResultDto>.FailureResult("Vui lòng đăng nhập để thực hiện chuyển nhượng vé."));
            }

            var result = await _ticketTransferService.TransferTicketAsync(ticketId, userId, request, HttpContext.RequestAborted);
            if (!result.Success)
            {
                if (result.Message?.Contains("không có quyền") == true)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, result);
                }
                return BadRequest(result);
            }

            return Ok(result);
        }

        /// <summary>
        /// S-57: API lấy lịch sử các lần chuyển nhượng của một vé.
        /// GET /api/v1/tickets/{ticketId}/transfers
        /// </summary>
        [HttpGet("{ticketId:guid}/transfers")]
        [EventTicketBooking.Api.Middlewares.RequireRole]
        [ProducesResponseType(typeof(ApiResponse<List<TicketTransferHistoryDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<List<TicketTransferHistoryDto>>), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetTicketTransfers([FromRoute] Guid ticketId)
        {
            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("id")?.Value
                ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

            if (!Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized(ApiResponse<List<TicketTransferHistoryDto>>.FailureResult("Vui lòng đăng nhập."));
            }

            var isAdmin = User.IsInRole("Admin") || User.HasClaim(c => (c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role") && c.Value == "Admin");

            var result = await _ticketTransferService.GetTicketTransferHistoryAsync(ticketId, userId, isAdmin, HttpContext.RequestAborted);
            if (!result.Success)
            {
                if (result.Message?.Contains("không có quyền") == true)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, result);
                }
                return BadRequest(result);
            }

            return Ok(result);
        }

        /// <summary>
        /// S-57 & S-38: API lấy toàn bộ vé của người dùng hiện tại (bao gồm vé mua và vé được nhận chuyển nhượng).
        /// GET /api/v1/tickets/my-tickets
        /// </summary>
        [HttpGet("my-tickets")]
        [EventTicketBooking.Api.Middlewares.RequireRole]
        [ProducesResponseType(typeof(ApiResponse<List<TicketDto>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetMyTickets()
        {
            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("id")?.Value
                ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

            if (!Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized(ApiResponse<List<TicketDto>>.FailureResult("Vui lòng đăng nhập."));
            }

            var result = await _ticketTransferService.GetUserTicketsAsync(userId, HttpContext.RequestAborted);
            return Ok(result);
        }

        /// <summary>
        /// S-26 NFR & S-33: API lấy danh sách khoá công khai (Public Keys) của hệ thống.
        /// Thiết bị quét vé lưu khoá công khai này để soát vé ngoại tuyến mà không cần kết nối máy chủ.
        /// </summary>
        [HttpGet("qr-public-keys")]
        [AllowAnonymous]
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
        [AllowAnonymous]
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
