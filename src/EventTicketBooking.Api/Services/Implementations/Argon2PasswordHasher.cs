using System;
using EventTicketBooking.Api.Services.Interfaces;
using Isopoh.Cryptography.Argon2;

namespace EventTicketBooking.Api.Services.Implementations
{
    /// <summary>
    /// Triển khai dịch vụ băm và xác thực mật khẩu bằng thuật toán Argon2id chuẩn RFC 9106.
    /// Chuỗi sinh ra có định dạng PHC chuẩn ($argon2id$v=19$m=65536,t=3,p=4$...).
    /// </summary>
    public class Argon2PasswordHasher : IPasswordHasher
    {
        public string Hash(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Mật khẩu không được để trống.", nameof(password));
            }

            return Argon2.Hash(password);
        }

        public bool Verify(string password, string passwordHash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
            {
                return false;
            }

            try
            {
                return Argon2.Verify(passwordHash, password);
            }
            catch
            {
                return false;
            }
        }
    }
}
