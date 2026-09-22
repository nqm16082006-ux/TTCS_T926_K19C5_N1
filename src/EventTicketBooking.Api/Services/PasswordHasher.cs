using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace EventTicketBooking.Api.Services
{
    public interface IPasswordHasher
    {
        string HashPassword(string password);
        bool VerifyPassword(string password, string hash);
    }

    public class PasswordHasher : IPasswordHasher
    {
        public string HashPassword(string password)
        {
            byte[] salt = CreateSalt();
            byte[] hash = HashPassword(password, salt);
            return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
        }

        public bool VerifyPassword(string password, string hashString)
        {
            var parts = hashString.Split(':');
            if (parts.Length != 2)
            {
                return false;
            }

            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] expectedHash = Convert.FromBase64String(parts[1]);
            byte[] actualHash = HashPassword(password, salt);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }

        private byte[] CreateSalt()
        {
            var buffer = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            return buffer;
        }

        private byte[] HashPassword(string password, byte[] salt)
        {
            var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
            argon2.Salt = salt;
            argon2.DegreeOfParallelism = 4;
            argon2.Iterations = 4;
            argon2.MemorySize = 1024 * 64; // 64 MB

            return argon2.GetBytes(16);
        }
    }
}
