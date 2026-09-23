using System.Threading.Tasks;
using EventTicketBooking.Api.DTOs.Auth;
using EventTicketBooking.Api.DTOs.Common;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface IAuthService
    {
        Task<ApiResponse<LoginResponseDto>> LoginAsync(LoginRequestDto request);
    }
}
