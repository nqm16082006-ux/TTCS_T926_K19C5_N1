using System.Collections.Generic;
using EventTicketBooking.Api.Models;

namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service tạo JWT Access Token cho phiên đăng nhập người dùng.
    /// </summary>
    public interface ITokenService
    {
        /// <summary>
        /// Sinh chuỗi JWT Access Token chứa Claims: UserId (sub), Email, Roles.
        /// </summary>
        string GenerateAccessToken(User user, IEnumerable<string> roles);
    }
}
