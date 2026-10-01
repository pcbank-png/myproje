using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NSYazilim.Web.Services
{
    public static partial class LocalizationTextKey
    {
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var decoded = WebUtility.HtmlDecode(value);
            return WhitespaceRegex().Replace(decoded, " ").Trim();
        }

        public static string Create(string? value)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0)
                return string.Empty;

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
        private static partial Regex WhitespaceRegex();
    }
}
