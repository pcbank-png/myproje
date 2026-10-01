using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NSYazilim.Web.Services
{
    public class IpGeolocationService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly ClientIpService _clientIpService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<IpGeolocationService> _logger;

        public IpGeolocationService(HttpClient httpClient, ClientIpService clientIpService, IConfiguration configuration, ILogger<IpGeolocationService> logger)
        {
            _httpClient = httpClient;
            _clientIpService = clientIpService;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<IpLocationResult?> ResolveAsync(string? ipAddress, CancellationToken cancellationToken = default)
        {
            var originalIp = (ipAddress ?? string.Empty).Trim();
            var ip = originalIp;
            var isLocalTestLookup = false;

            // Local testte IP genelde ::1 veya 127.0.0.1 gelir. Bu IP'lerin şehir bilgisi yoktur.
            // Development appsettings içinde IpGeolocation:LocalTestPublicIp verilirse, test için o public IP sorgulanır.
            if (!_clientIpService.IsPublicIp(ip))
            {
                var localTestPublicIp = (_configuration["IpGeolocation:LocalTestPublicIp"] ?? string.Empty).Trim();
                if (_clientIpService.IsPublicIp(localTestPublicIp))
                {
                    ip = localTestPublicIp;
                    isLocalTestLookup = true;
                }
                else
                {
                    return BuildLocalNetworkResult(originalIp);
                }
            }

            // GeoIP servisleri şehir bazında bazen hatalı blok verisi döndürebilir.
            // Admin tarafından doğrulanan IP'ler önce burada düzeltilir; dış servis sonucu bunu ezemez.
            var manualCorrection = FindManualCorrection(ip);
            if (manualCorrection != null)
            {
                if (isLocalTestLookup)
                    manualCorrection.Source = $"Local test {originalIp} => {ip} / {manualCorrection.Source}";

                return manualCorrection;
            }

            try
            {
                var providerTasks = BuildProviderTasks(ip, cancellationToken).ToList();
                if (providerTasks.Count == 0)
                    return isLocalTestLookup ? BuildLocalNetworkResult(originalIp) : null;

                var results = await Task.WhenAll(providerTasks);
                var usableResults = results
                    .Where(x => x != null && (!string.IsNullOrWhiteSpace(x.City) || !string.IsNullOrWhiteSpace(x.Region) || !string.IsNullOrWhiteSpace(x.Country)))
                    .Select(x => x!)
                    .ToList();

                if (usableResults.Count == 0)
                    return isLocalTestLookup ? BuildLocalNetworkResult(originalIp) : null;

                var bestResult = PickBestResult(usableResults);
                if (bestResult != null && isLocalTestLookup)
                {
                    bestResult.Source = $"Local test {originalIp} => {ip} / {bestResult.Source}";
                    bestResult.Confidence = Math.Min(99, Math.Max(bestResult.Confidence, 90));
                }

                return bestResult;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IP lokasyon bilgisi alınamadı. IP: {IpAddress}", ipAddress);
                return isLocalTestLookup ? BuildLocalNetworkResult(originalIp) : null;
            }
        }

        private IEnumerable<Task<IpLocationResult?>> BuildProviderTasks(string ipAddress, CancellationToken cancellationToken)
        {
            var ip2LocationKey = (_configuration["IpGeolocation:Ip2LocationApiKey"] ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(ip2LocationKey))
                yield return QueryIp2LocationAsync(ipAddress, ip2LocationKey, cancellationToken);

            var ipInfoToken = (_configuration["IpGeolocation:IpInfoToken"] ?? string.Empty).Trim();
            yield return QueryIpInfoAsync(ipAddress, ipInfoToken, cancellationToken);
            yield return QueryIpApiAsync(ipAddress, cancellationToken);
            yield return QueryIpApiCoAsync(ipAddress, cancellationToken);
            yield return QueryIpWhoIsAsync(ipAddress, cancellationToken);
        }

        private async Task<IpLocationResult?> QueryIp2LocationAsync(string ipAddress, string apiKey, CancellationToken cancellationToken)
        {
            try
            {
                var url = $"https://api.ip2location.io/?key={Uri.EscapeDataString(apiKey)}&ip={Uri.EscapeDataString(ipAddress)}&format=json&lang=tr";
                var payload = await GetJsonAsync<Ip2LocationResponse>(url, cancellationToken);
                if (payload == null)
                    return null;

                return BuildResult(payload.CityName, payload.RegionName, payload.CountryName, "IP2Location", 95);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IP2Location lokasyon servisi cevap vermedi. IP: {IpAddress}", ipAddress);
                return null;
            }
        }

        private async Task<IpLocationResult?> QueryIpInfoAsync(string ipAddress, string token, CancellationToken cancellationToken)
        {
            try
            {
                var url = string.IsNullOrWhiteSpace(token)
                    ? $"https://ipinfo.io/{Uri.EscapeDataString(ipAddress)}/json"
                    : $"https://ipinfo.io/{Uri.EscapeDataString(ipAddress)}/json?token={Uri.EscapeDataString(token)}";

                var payload = await GetJsonAsync<IpInfoResponse>(url, cancellationToken);
                if (payload == null || payload.Bogon == true)
                    return null;

                return BuildResult(payload.City, payload.Region, NormalizeCountry(payload.Country), "IPinfo", 90);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IPinfo lokasyon servisi cevap vermedi. IP: {IpAddress}", ipAddress);
                return null;
            }
        }

        private async Task<IpLocationResult?> QueryIpApiAsync(string ipAddress, CancellationToken cancellationToken)
        {
            try
            {
                var url = $"http://ip-api.com/json/{Uri.EscapeDataString(ipAddress)}?fields=status,message,country,regionName,city,query&lang=tr";
                var payload = await GetJsonAsync<IpApiResponse>(url, cancellationToken);
                if (payload == null || !string.Equals(payload.Status, "success", StringComparison.OrdinalIgnoreCase))
                    return null;

                return BuildResult(payload.City, payload.RegionName, payload.Country, "ip-api", 88);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ip-api lokasyon servisi cevap vermedi. IP: {IpAddress}", ipAddress);
                return null;
            }
        }

        private async Task<IpLocationResult?> QueryIpApiCoAsync(string ipAddress, CancellationToken cancellationToken)
        {
            try
            {
                var url = $"https://ipapi.co/{Uri.EscapeDataString(ipAddress)}/json/";
                var payload = await GetJsonAsync<IpApiCoResponse>(url, cancellationToken);
                if (payload == null || payload.Error == true)
                    return null;

                return BuildResult(payload.City, payload.Region, payload.CountryName, "ipapi.co", 86);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ipapi.co lokasyon servisi cevap vermedi. IP: {IpAddress}", ipAddress);
                return null;
            }
        }

        private async Task<IpLocationResult?> QueryIpWhoIsAsync(string ipAddress, CancellationToken cancellationToken)
        {
            try
            {
                var url = $"https://ipwho.is/{Uri.EscapeDataString(ipAddress)}?lang=tr";
                var payload = await GetJsonAsync<IpWhoIsResponse>(url, cancellationToken);
                if (payload?.Success != true)
                    return null;

                // ipwho.is bazı Türkiye IP bloklarında şehir bazında zayıf kaldığı için ağırlığı düşük tutuldu.
                return BuildResult(payload.City, payload.Region, payload.Country, "ipwho.is", 60);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ipwho.is lokasyon servisi cevap vermedi. IP: {IpAddress}", ipAddress);
                return null;
            }
        }

        private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return default;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }

        private IpLocationResult? FindManualCorrection(string ipAddress)
        {
            if (!_clientIpService.IsPublicIp(ipAddress))
                return null;

            var section = _configuration.GetSection("IpGeolocation:ManualCorrections");
            if (!section.Exists())
                return null;

            foreach (var item in section.GetChildren())
            {
                var exactIp = (item["Ip"] ?? item["ExactIp"] ?? string.Empty).Trim();
                var cidr = (item["Cidr"] ?? item["Range"] ?? string.Empty).Trim();

                var isMatch = false;
                if (!string.IsNullOrWhiteSpace(exactIp))
                    isMatch = string.Equals(NormalizeIpText(exactIp), NormalizeIpText(ipAddress), StringComparison.OrdinalIgnoreCase);

                if (!isMatch && !string.IsNullOrWhiteSpace(cidr))
                    isMatch = IsIpInCidr(ipAddress, cidr);

                if (!isMatch)
                    continue;

                var city = TrimTo(item["City"], 120);
                var region = TrimTo(item["Region"], 120);
                var country = TrimTo(item["Country"], 120) ?? "Türkiye";

                if (string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(region))
                    continue;

                return new IpLocationResult
                {
                    City = city,
                    Region = region,
                    Country = country,
                    Source = string.IsNullOrWhiteSpace(cidr)
                        ? $"Manuel doğrulama ({exactIp})"
                        : $"Manuel doğrulama ({cidr})",
                    Confidence = 100
                };
            }

            return null;
        }

        private static bool IsIpInCidr(string ipAddress, string cidr)
        {
            try
            {
                var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !IPAddress.TryParse(ipAddress, out var ip))
                    return false;

                if (!int.TryParse(parts[1], out var prefixLength))
                    return false;

                if (network.IsIPv4MappedToIPv6)
                    network = network.MapToIPv4();
                if (ip.IsIPv4MappedToIPv6)
                    ip = ip.MapToIPv4();

                var networkBytes = network.GetAddressBytes();
                var ipBytes = ip.GetAddressBytes();
                if (networkBytes.Length != ipBytes.Length)
                    return false;

                var maxPrefix = networkBytes.Length * 8;
                if (prefixLength < 0 || prefixLength > maxPrefix)
                    return false;

                var fullBytes = prefixLength / 8;
                var remainingBits = prefixLength % 8;

                for (var i = 0; i < fullBytes; i++)
                {
                    if (networkBytes[i] != ipBytes[i])
                        return false;
                }

                if (remainingBits == 0)
                    return true;

                var mask = (byte)(0xFF << (8 - remainingBits));
                return (networkBytes[fullBytes] & mask) == (ipBytes[fullBytes] & mask);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeIpText(string ipAddress)
        {
            if (!IPAddress.TryParse((ipAddress ?? string.Empty).Trim(), out var ip))
                return (ipAddress ?? string.Empty).Trim();

            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            return ip.ToString();
        }

        private static IpLocationResult? BuildLocalNetworkResult(string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return null;

            if (!IPAddress.TryParse(ipAddress.Trim(), out var ip))
                return null;

            if (!IPAddress.IsLoopback(ip) && !IsPrivateOrCgnatIp(ip))
                return null;

            return new IpLocationResult
            {
                City = "Yerel Test",
                Region = "Localhost",
                Country = "Geliştirme Ortamı",
                Source = "Localhost / private IP",
                Confidence = 100
            };
        }

        private static bool IsPrivateOrCgnatIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
                return true;

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
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

        private static IpLocationResult? BuildResult(string? city, string? region, string? country, string source, int confidence)
        {
            city = TrimTo(city, 120);
            region = TrimTo(region, 120);
            country = TrimTo(country, 120);

            if (string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(region) && string.IsNullOrWhiteSpace(country))
                return null;

            return new IpLocationResult
            {
                City = city,
                Region = region,
                Country = country,
                Source = source,
                Confidence = confidence
            };
        }

        private static IpLocationResult? PickBestResult(List<IpLocationResult> results)
        {
            var cityGroups = results
                .Where(x => !string.IsNullOrWhiteSpace(x.City))
                .GroupBy(x => NormalizeForCompare(x.City!))
                .Select(g => new
                {
                    Key = g.Key,
                    Score = g.Sum(x => Math.Max(1, x.Confidence)),
                    Count = g.Count(),
                    Best = g.OrderByDescending(x => x.Confidence).First()
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Count)
                .FirstOrDefault();

            if (cityGroups != null)
            {
                var sameCityResults = results
                    .Where(x => NormalizeForCompare(x.City ?? string.Empty) == cityGroups.Key)
                    .OrderByDescending(x => x.Confidence)
                    .ToList();

                var best = cityGroups.Best;
                return new IpLocationResult
                {
                    City = best.City,
                    Region = sameCityResults.Select(x => x.Region).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    Country = sameCityResults.Select(x => x.Country).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    Source = string.Join(" + ", sameCityResults.Select(x => x.Source).Distinct()),
                    Confidence = Math.Min(99, cityGroups.Score / Math.Max(1, sameCityResults.Count))
                };
            }

            return results.OrderByDescending(x => x.Confidence).FirstOrDefault();
        }

        private static string? TrimTo(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private static string NormalizeForCompare(string value)
        {
            value = (value ?? string.Empty).Trim().ToUpper(new CultureInfo("tr-TR"));
            return value
                .Replace("İ", "I", StringComparison.Ordinal)
                .Replace("Ğ", "G", StringComparison.Ordinal)
                .Replace("Ü", "U", StringComparison.Ordinal)
                .Replace("Ş", "S", StringComparison.Ordinal)
                .Replace("Ö", "O", StringComparison.Ordinal)
                .Replace("Ç", "C", StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal);
        }

        private static string? NormalizeCountry(string? country)
        {
            if (string.IsNullOrWhiteSpace(country))
                return null;

            country = country.Trim();
            return country.ToUpperInvariant() switch
            {
                "TR" => "Türkiye",
                "US" => "United States",
                "DE" => "Germany",
                "FR" => "France",
                "GB" => "United Kingdom",
                _ => country
            };
        }

        private sealed class IpWhoIsResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("city")]
            public string? City { get; set; }

            [JsonPropertyName("region")]
            public string? Region { get; set; }

            [JsonPropertyName("country")]
            public string? Country { get; set; }
        }

        private sealed class IpApiResponse
        {
            [JsonPropertyName("status")]
            public string? Status { get; set; }

            [JsonPropertyName("city")]
            public string? City { get; set; }

            [JsonPropertyName("regionName")]
            public string? RegionName { get; set; }

            [JsonPropertyName("country")]
            public string? Country { get; set; }
        }

        private sealed class IpApiCoResponse
        {
            [JsonPropertyName("error")]
            public bool? Error { get; set; }

            [JsonPropertyName("city")]
            public string? City { get; set; }

            [JsonPropertyName("region")]
            public string? Region { get; set; }

            [JsonPropertyName("country_name")]
            public string? CountryName { get; set; }
        }

        private sealed class IpInfoResponse
        {
            [JsonPropertyName("city")]
            public string? City { get; set; }

            [JsonPropertyName("region")]
            public string? Region { get; set; }

            [JsonPropertyName("country")]
            public string? Country { get; set; }

            [JsonPropertyName("bogon")]
            public bool? Bogon { get; set; }
        }

        private sealed class Ip2LocationResponse
        {
            [JsonPropertyName("city_name")]
            public string? CityName { get; set; }

            [JsonPropertyName("region_name")]
            public string? RegionName { get; set; }

            [JsonPropertyName("country_name")]
            public string? CountryName { get; set; }
        }
    }

    public class IpLocationResult
    {
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        public string? Source { get; set; }
        public int Confidence { get; set; }
    }
}
