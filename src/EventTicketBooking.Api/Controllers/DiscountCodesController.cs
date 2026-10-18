using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.DTOs.DiscountCodes;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketBooking.Api.Controllers
{
    [ApiController]
    [Route("api/events/{eventId:guid}/discount-codes")]
    [RequireRole("Organizer", "Admin")]
    public class DiscountCodesController : ControllerBase
    {
        private readonly IDiscountCodeService _discountCodeService;

        public DiscountCodesController(IDiscountCodeService discountCodeService)
        {
            _discountCodeService = discountCodeService;
        }

        [HttpPost]
        [ProducesResponseType(typeof(ApiResponse<DiscountCodeDto>), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateDiscountCode(Guid eventId, [FromBody] CreateDiscountCodeRequest request)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var isAdmin = IsCurrentUserAdmin();
            var (success, message, data, statusCode) = await _discountCodeService.CreateDiscountCodeAsync(eventId, request, userId.Value, isAdmin);

            if (!success)
            {
                return statusCode switch
                {
                    StatusCodes.Status404NotFound => NotFound(ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status403Forbidden => StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status409Conflict => Conflict(ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status400BadRequest => BadRequest(ApiResponse<object>.FailureResult(message)),
                    _ => StatusCode(statusCode, ApiResponse<object>.FailureResult(message))
                };
            }

            return StatusCode(StatusCodes.Status201Created, ApiResponse<DiscountCodeDto>.SuccessResult(data!, message));
        }

        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<List<DiscountCodeDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetDiscountCodes(Guid eventId)
        {
            var userId = GetCurrentUserId();
            if (!userId.HasValue)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.FailureResult("Vui lòng đăng nhập để thực hiện thao tác này."));
            }

            var isAdmin = IsCurrentUserAdmin();
            var (success, message, data, statusCode) = await _discountCodeService.GetDiscountCodesByEventAsync(eventId, userId.Value, isAdmin);

            if (!success)
            {
                return statusCode switch
                {
                    StatusCodes.Status404NotFound => NotFound(ApiResponse<object>.FailureResult(message)),
                    StatusCodes.Status403Forbidden => StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.FailureResult(message)),
                    _ => StatusCode(statusCode, ApiResponse<object>.FailureResult(message))
                };
            }

            return Ok(ApiResponse<List<DiscountCodeDto>>.SuccessResult(data ?? new List<DiscountCodeDto>(), message));
        }

        private Guid? GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? User.FindFirst("id")?.Value
                              ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId))
            {
                return userId;
            }
            return null;
        }

        private bool IsCurrentUserAdmin()
        {
            return User.IsInRole("Admin") ||
                   User.HasClaim(c => (c.Type == ClaimTypes.Role || c.Type == "role") &&
                                      string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase));
        }
    }
}
