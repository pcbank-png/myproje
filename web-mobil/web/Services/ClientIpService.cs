using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace NSYazilim.Web.Services
{
    public class ClientIpService
    {
        private static readonly string[] ForwardedHeaders =
        {
            "CF-Connecting-IP",
            "X-Forwarded-For",
            "X-Real-IP"
        };

        private readonly IConfiguration _configuration;

        public ClientIpService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string GetClientIp(HttpContext httpContext)
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress;
            var remoteIpText = NormalizeIp(remoteIp);

            // Forwarded header bilgileri sadece Plesk/Nginx/Apache/Cloudflare gibi güvenilir proxy arkasında okunur.
            // Böylece kullanıcı tarafının taşıdığı sahte X-Forwarded-For değeri üyeye/demo kaydına yazılmaz.
            if (ShouldTrustForwardedHeaders(remoteIp))
            {
                foreach (var header in ForwardedHeaders)
                {
                    var rawValue = httpContext.Request.Headers[header].FirstOrDefault();
                    var ip = ExtractForwardedIp(header, rawValue);
                    if (!string.IsNullOrWhiteSpace(ip))
                        return ip;
                }
            }

            return remoteIpText;
        }

        public bool IsPublicIp(string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return false;

            if (!IPAddress.TryParse(ipAddress.Trim(), out var ip))
                return false;

            return IsPublicIp(ip);
        }

        private bool ShouldTrustForwardedHeaders(IPAddress? remoteIp)
        {
            var trustForwardedHeadersText = _configuration["ClientIp:TrustForwardedHeaders"];
            if (!string.IsNullOrWhiteSpace(trustForwardedHeadersText) &&
                bool.TryParse(trustForwardedHeadersText, out var trustForwardedHeaders) &&
                !trustForwardedHeaders)
                return false;

            if (remoteIp == null)
                return false;

            // Local/Plesk reverse proxy genelde 127.0.0.1, ::1 veya private IP üzerinden gelir.
            if (IsLoopbackOrPrivateIp(remoteIp))
                return true;

            var remoteIpText = NormalizeIp(remoteIp);
            var trustedProxyIps = (_configuration["ClientIp:TrustedProxyIps"] ?? string.Empty)
                .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return trustedProxyIps.Any(x => string.Equals(x, remoteIpText, StringComparison.OrdinalIgnoreCase));
        }

        private static string ExtractForwardedIp(string headerName, string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return string.Empty;

            var candidates = rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (!candidates.Any())
                return string.Empty;

            // Güvenilir proxy arkasında X-Forwarded-For sırası genelde: müşteri, proxy1, proxy2 şeklindedir.
            // Bu yüzden soldan ilk public IP müşterinin gerçek IP'si kabul edilir.
            if (headerName.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var candidate in candidates)
                {
                    var cleanCandidate = CleanHeaderIp(candidate);
                    if (IPAddress.TryParse(cleanCandidate, out var ip) && IsPublicIp(ip))
                        return NormalizeIp(ip);
                }
            }

            var first = CleanHeaderIp(candidates.FirstOrDefault());
            if (IPAddress.TryParse(first, out var parsedIp))
                return NormalizeIp(parsedIp);

            return string.Empty;
        }


        private static string CleanHeaderIp(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim().Trim('"');

            if (value.StartsWith("[", StringComparison.Ordinal) && value.Contains(']'))
            {
                var endIndex = value.IndexOf(']');
                return value.Substring(1, endIndex - 1);
            }

            var colonCount = value.Count(x => x == ':');
            if (colonCount == 1)
            {
                var parts = value.Split(':', 2);
                if (IPAddress.TryParse(parts[0], out _))
                    return parts[0];
            }

            return value;
        }

        private static bool IsPublicIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
                return false;

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                    return false;

                if (ip.IsIPv4MappedToIPv6)
                    ip = ip.MapToIPv4();
                else
                    return true;
            }

            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
                return false;

            // 10.0.0.0/8
            if (bytes[0] == 10)
                return false;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return false;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return false;

            // 169.254.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254)
                return false;

            // 100.64.0.0/10 CGNAT
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                return false;

            return true;
        }

        private static bool IsLoopbackOrPrivateIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
                return true;

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv4MappedToIPv6)
                    ip = ip.MapToIPv4();
                else
                    return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast;
            }

            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
                return false;

            if (bytes[0] == 10)
                return true;

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            if (bytes[0] == 169 && bytes[1] == 254)
                return true;

            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                return true;

            return false;
        }

        private static string NormalizeIp(IPAddress? ip)
        {
            if (ip == null)
                return string.Empty;

            if (ip.IsIPv4MappedToIPv6)
                return ip.MapToIPv4().ToString();

            return ip.ToString();
        }
    }
}
