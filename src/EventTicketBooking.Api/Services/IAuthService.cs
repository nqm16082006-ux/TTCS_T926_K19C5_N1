using EventTicketBooking.Api.DTOs;

namespace EventTicketBooking.Api.Services;

public interface IAuthService
{
    Task<LoginResponseDto> LoginAsync(LoginRequestDto request);
}