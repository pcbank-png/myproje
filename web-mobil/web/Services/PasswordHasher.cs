using System.Security.Cryptography;
using System.Text;

namespace NSYazilim.Web.Services
{
    public static class PasswordHasher
    {
        public static string Hash(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                return string.Empty;

            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(bytes);
        }

        public static bool Verify(string password, string passwordHash)
        {
            return Hash(password) == passwordHash;
        }
    }
}
