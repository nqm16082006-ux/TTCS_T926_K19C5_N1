using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    public interface ITokenService
    {
        string GenerateToken(User user);
        int GetExpiresInSeconds();
    }
}
