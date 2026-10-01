using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Services
{
    public sealed class OnlineVisitorTracker
    {
        private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(30);

        private static readonly ConcurrentDictionary<string, OnlineVisitorState> FallbackVisitors = new(StringComparer.Ordinal);
        private readonly ApplicationDbContext _db;
        private readonly ClientIpService _clientIpService;
        private readonly IpGeolocationService _ipGeolocationService;
        private readonly ConcurrentDictionary<string, string> _cityCache = new(StringComparer.OrdinalIgnoreCase);

        public OnlineVisitorTracker(ApplicationDbContext db, ClientIpService clientIpService, IpGeolocationService ipGeolocationService)
        {
            _db = db;
            _clientIpService = clientIpService;
            _ipGeolocationService = ipGeolocationService;
        }

        public async Task TouchAsync(HttpContext httpContext, OnlineVisitorPingRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(request);

            var nowUtc = DateTime.UtcNow;
            var visitorId = NormalizeVisitorId(request.VisitorId, httpContext.Session.Id);
            var ipAddress = Limit(_clientIpService.GetClientIp(httpContext), 80);
            var city = await ResolveCityAsync(ipAddress, cancellationToken);
            var userAgent = Limit(httpContext.Request.Headers["User-Agent"].FirstOrDefault(), 500);
            var pagePath = NormalizePagePath(request.PagePath);
            var pageTitle = Limit(request.PageTitle, 220);
            var referrer = NormalizeReferrer(request.Referrer);
            var browser = DetectBrowser(userAgent);
            var operatingSystem = DetectOperatingSystem(userAgent);
            var deviceType = DetectDeviceType(userAgent);
            var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true;
            var userName = isAuthenticated
                ? Limit(httpContext.User.FindFirstValue(ClaimTypes.Name) ?? httpContext.User.Identity?.Name, 150)
                : string.Empty;
            var userEmail = isAuthenticated
                ? Limit(httpContext.User.FindFirstValue(ClaimTypes.Email), 180)
                : string.Empty;
            var userRole = isAuthenticated
                ? Limit(httpContext.User.FindFirstValue(ClaimTypes.Role), 60)
                : string.Empty;

            FallbackVisitors.AddOrUpdate(
                visitorId,
                _ => new OnlineVisitorState
                {
                    VisitorId = visitorId,
                    FirstSeenAtUtc = nowUtc,
                    LastSeenAtUtc = nowUtc,
                    IpAddress = ipAddress,
                    City = city,
                    PagePath = pagePath,
                    PageTitle = pageTitle,
                    Referrer = referrer,
                    Browser = browser,
                    OperatingSystem = operatingSystem,
                    DeviceType = deviceType,
                    UserAgent = userAgent,
                    IsAuthenticated = isAuthenticated,
                    UserName = userName,
                    UserEmail = userEmail,
                    UserRole = userRole
                },
                (_, existing) => existing with
                {
                    LastSeenAtUtc = nowUtc,
                    IpAddress = ipAddress,
                    City = string.IsNullOrWhiteSpace(city) ? existing.City : city,
                    PagePath = pagePath,
                    PageTitle = pageTitle,
                    Referrer = string.IsNullOrWhiteSpace(referrer) ? existing.Referrer : referrer,
                    Browser = browser,
                    OperatingSystem = operatingSystem,
                    DeviceType = deviceType,
                    UserAgent = userAgent,
                    IsAuthenticated = isAuthenticated,
                    UserName = userName,
                    UserEmail = userEmail,
                    UserRole = userRole
                });

            try
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO `OnlineVisitorPresences`
                    (`VisitorId`, `FirstSeenAtUtc`, `LastSeenAtUtc`, `IpAddress`, `City`, `PagePath`, `PageTitle`, `Referrer`, `Browser`, `OperatingSystem`, `DeviceType`, `UserAgent`, `IsAuthenticated`, `UserName`, `UserEmail`, `UserRole`)
                    VALUES ({visitorId}, {nowUtc}, {nowUtc}, {ipAddress}, {city}, {pagePath}, {pageTitle}, {referrer}, {browser}, {operatingSystem}, {deviceType}, {userAgent}, {isAuthenticated}, {userName}, {userEmail}, {userRole})
                    ON DUPLICATE KEY UPDATE
                    `LastSeenAtUtc` = VALUES(`LastSeenAtUtc`), `IpAddress` = VALUES(`IpAddress`),
                    `City` = IF(VALUES(`City`) = '', `City`, VALUES(`City`)), `PagePath` = VALUES(`PagePath`),
                    `PageTitle` = VALUES(`PageTitle`), `Referrer` = IF(VALUES(`Referrer`) = '', `Referrer`, VALUES(`Referrer`)),
                    `Browser` = VALUES(`Browser`), `OperatingSystem` = VALUES(`OperatingSystem`), `DeviceType` = VALUES(`DeviceType`),
                    `UserAgent` = VALUES(`UserAgent`), `IsAuthenticated` = VALUES(`IsAuthenticated`),
                    `UserName` = VALUES(`UserName`), `UserEmail` = VALUES(`UserEmail`), `UserRole` = VALUES(`UserRole`);", cancellationToken);
            }
            catch
            {
                // Veritabanı geçici olarak erişilemezse canlı ekran bellek yedeğiyle çalışmayı sürdürür.
            }

            Cleanup(nowUtc);
        }

        public async Task<OnlineVisitorSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var nowUtc = DateTime.UtcNow;
            Cleanup(nowUtc);

            var onlineLimit = nowUtc - OnlineWindow;
            try
            {
                var persisted = await _db.OnlineVisitorPresences
                    .AsNoTracking()
                    .Where(x => x.LastSeenAtUtc >= onlineLimit)
                    .OrderByDescending(x => x.LastSeenAtUtc)
                    .ToListAsync(cancellationToken);

                var visitors = persisted.Select(x => new OnlineVisitorItem
                {
                    VisitorId = x.VisitorId,
                    VisitorCode = ShortVisitorCode(x.VisitorId),
                    FirstSeenAtUtc = x.FirstSeenAtUtc,
                    LastSeenAtUtc = x.LastSeenAtUtc,
                    IpAddress = x.IpAddress ?? string.Empty,
                    City = x.City ?? string.Empty,
                    PagePath = x.PagePath,
                    PageTitle = x.PageTitle ?? string.Empty,
                    Referrer = x.Referrer ?? string.Empty,
                    Browser = x.Browser,
                    OperatingSystem = x.OperatingSystem,
                    DeviceType = x.DeviceType,
                    IsAuthenticated = x.IsAuthenticated,
                    UserName = x.UserName ?? string.Empty,
                    UserEmail = x.UserEmail ?? string.Empty,
                    UserRole = x.UserRole ?? string.Empty
                }).ToList();

                return BuildSnapshot(visitors, nowUtc);
            }
            catch
            {
                var visitors = FallbackVisitors.Values
                .Where(x => x.LastSeenAtUtc >= onlineLimit)
                .OrderByDescending(x => x.LastSeenAtUtc)
                .Select(x => new OnlineVisitorItem
                {
                    VisitorId = x.VisitorId,
                    VisitorCode = ShortVisitorCode(x.VisitorId),
                    FirstSeenAtUtc = x.FirstSeenAtUtc,
                    LastSeenAtUtc = x.LastSeenAtUtc,
                    IpAddress = x.IpAddress,
                    City = x.City,
                    PagePath = x.PagePath,
                    PageTitle = x.PageTitle,
                    Referrer = x.Referrer,
                    Browser = x.Browser,
                    OperatingSystem = x.OperatingSystem,
                    DeviceType = x.DeviceType,
                    IsAuthenticated = x.IsAuthenticated,
                    UserName = x.UserName,
                    UserEmail = x.UserEmail,
                    UserRole = x.UserRole
                })
                .ToList();

                return BuildSnapshot(visitors, nowUtc);
            }
        }

        private static OnlineVisitorSnapshot BuildSnapshot(IReadOnlyList<OnlineVisitorItem> visitors, DateTime nowUtc)
        {
            return new OnlineVisitorSnapshot
            {
                GeneratedAtUtc = nowUtc,
                OnlineWindowSeconds = (int)OnlineWindow.TotalSeconds,
                OnlineCount = visitors.Count,
                GuestCount = visitors.Count(x => !x.IsAuthenticated),
                AuthenticatedCount = visitors.Count(x => x.IsAuthenticated),
                ActivePageCount = visitors
                    .Select(x => x.PagePath)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                Visitors = visitors
            };
        }


        private async Task<string> ResolveCityAsync(string ipAddress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                return string.Empty;
            }

            if (_cityCache.TryGetValue(ipAddress, out var cachedCity))
            {
                return cachedCity;
            }

            try
            {
                var location = await _ipGeolocationService.ResolveAsync(ipAddress, cancellationToken);
                var city = Limit(location?.City, 120);
                _cityCache.TryAdd(ipAddress, city);
                return city;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _cityCache.TryAdd(ipAddress, string.Empty);
                return string.Empty;
            }
        }

        private void Cleanup(DateTime nowUtc)
        {
            var retentionLimit = nowUtc - RetentionWindow;
            foreach (var visitor in FallbackVisitors)
            {
                if (visitor.Value.LastSeenAtUtc < retentionLimit)
                {
                    FallbackVisitors.TryRemove(visitor.Key, out _);
                }
            }
        }

        private static string NormalizeVisitorId(string? visitorId, string sessionId)
        {
            var value = string.IsNullOrWhiteSpace(visitorId) ? sessionId : visitorId.Trim();
            if (value.Length > 80)
            {
                value = value[..80];
            }

            var safeValue = new string(value
                .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
                .ToArray());

            return string.IsNullOrWhiteSpace(safeValue)
                ? $"session-{sessionId}"
                : safeValue;
        }

        private static string NormalizePagePath(string? pagePath)
        {
            if (string.IsNullOrWhiteSpace(pagePath))
            {
                return "/";
            }

            var value = pagePath.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
            {
                value = absoluteUri.PathAndQuery;
            }

            if (!value.StartsWith("/", StringComparison.Ordinal))
            {
                value = "/" + value;
            }

            return Limit(value, 500);
        }

        private static string NormalizeReferrer(string? referrer)
        {
            if (string.IsNullOrWhiteSpace(referrer))
            {
                return string.Empty;
            }

            var value = referrer.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return Limit($"{uri.Host}{uri.PathAndQuery}", 500);
            }

            return Limit(value, 500);
        }

        private static string DetectBrowser(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return "Bilinmiyor";
            if (userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
            if (userAgent.Contains("OPR/", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
            if (userAgent.Contains("SamsungBrowser", StringComparison.OrdinalIgnoreCase)) return "Samsung Internet";
            if (userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("FxiOS/", StringComparison.OrdinalIgnoreCase)) return "Firefox";
            if (userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("CriOS/", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
            if (userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase)) return "Safari";
            return "Diğer";
        }

        private static string DetectOperatingSystem(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return "Bilinmiyor";
            if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
            if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
            if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iOS / iPadOS";
            if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Mac OS", StringComparison.OrdinalIgnoreCase)) return "macOS";
            if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
            return "Diğer";
        }

        private static string DetectDeviceType(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return "Bilinmiyor";
            if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Tablet", StringComparison.OrdinalIgnoreCase)) return "Tablet";
            if (userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "Mobil";
            return "Masaüstü";
        }

        private static string ShortVisitorCode(string visitorId)
        {
            if (string.IsNullOrWhiteSpace(visitorId)) return "-";
            var compact = visitorId.Replace("-", string.Empty, StringComparison.Ordinal);
            return compact.Length <= 8 ? compact.ToUpperInvariant() : compact[..8].ToUpperInvariant();
        }

        private static string Limit(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private sealed record OnlineVisitorState
        {
            public string VisitorId { get; init; } = string.Empty;
            public DateTime FirstSeenAtUtc { get; init; }
            public DateTime LastSeenAtUtc { get; init; }
            public string IpAddress { get; init; } = string.Empty;
            public string City { get; init; } = string.Empty;
            public string PagePath { get; init; } = "/";
            public string PageTitle { get; init; } = string.Empty;
            public string Referrer { get; init; } = string.Empty;
            public string Browser { get; init; } = string.Empty;
            public string OperatingSystem { get; init; } = string.Empty;
            public string DeviceType { get; init; } = string.Empty;
            public string UserAgent { get; init; } = string.Empty;
            public bool IsAuthenticated { get; init; }
            public string UserName { get; init; } = string.Empty;
            public string UserEmail { get; init; } = string.Empty;
            public string UserRole { get; init; } = string.Empty;
        }
    }

    public sealed class OnlineVisitorPingRequest
    {
        public string? VisitorId { get; set; }
        public string? PagePath { get; set; }
        public string? PageTitle { get; set; }
        public string? Referrer { get; set; }
    }

    public sealed class OnlineVisitorSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public int OnlineWindowSeconds { get; set; }
        public int OnlineCount { get; set; }
        public int GuestCount { get; set; }
        public int AuthenticatedCount { get; set; }
        public int ActivePageCount { get; set; }
        public IReadOnlyList<OnlineVisitorItem> Visitors { get; set; } = Array.Empty<OnlineVisitorItem>();
    }

    public sealed class OnlineVisitorItem
    {
        public string VisitorId { get; set; } = string.Empty;
        public string VisitorCode { get; set; } = string.Empty;
        public DateTime FirstSeenAtUtc { get; set; }
        public DateTime LastSeenAtUtc { get; set; }
        public string IpAddress { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string PagePath { get; set; } = string.Empty;
        public string PageTitle { get; set; } = string.Empty;
        public string Referrer { get; set; } = string.Empty;
        public string Browser { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
        public bool IsAuthenticated { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public string UserRole { get; set; } = string.Empty;
    }
}
