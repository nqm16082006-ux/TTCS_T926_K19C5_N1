namespace EventTicketBooking.Api.Services.Interfaces
{
    /// <summary>
    /// Service băm và xác thực mật khẩu dùng chung cho toàn bộ dự án.
    /// </summary>
    public interface IPasswordHasher
    {
        /// <summary>
        /// Băm mật khẩu bằng thuật toán Argon2id (chuẩn RFC 9106, format chuỗi PHC).
        /// </summary>
        string Hash(string password);

        /// <summary>
        /// Xác thực mật khẩu người dùng với chuỗi hash Argon2id đã lưu.
        /// </summary>
        bool Verify(string password, string passwordHash);
    }
}
