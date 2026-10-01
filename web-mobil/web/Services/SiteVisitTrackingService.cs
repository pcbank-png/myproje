using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Services
{
    public sealed class SiteVisitTrackingService
    {
        public const string VisitorCookieName = "NSX.Visitor.Id";
        public const string HumanTrafficType = "Human";
        public const string GoogleRobotTrafficType = "GoogleRobot";
        public const string OtherRobotTrafficType = "OtherRobot";
        private static readonly TimeZoneInfo TurkeyTimeZone = ResolveTurkeyTimeZone();
        private static readonly (string Marker, string Name)[] GoogleRobotAgents =
        {
            ("Google-InspectionTool", "Google Inspection Tool"),
            ("GoogleOther", "GoogleOther"),
            ("AdsBot-Google", "Google AdsBot"),
            ("Mediapartners-Google", "Google Mediapartners"),
            ("Storebot-Google", "Google Storebot"),
            ("Googlebot-Image", "Googlebot Image"),
            ("Googlebot-News", "Googlebot News"),
            ("Googlebot-Video", "Googlebot Video"),
            ("Googlebot", "Googlebot"),
            ("Google-Safety", "Google Safety"),
            ("Google Favicon", "Google Favicon"),
            ("FeedFetcher-Google", "Google FeedFetcher"),
            ("DuplexWeb-Google", "Google DuplexWeb"),
            ("Google Web Preview", "Google Web Preview"),
            ("APIs-Google", "Google APIs")
        };
        private static readonly (string Marker, string Name)[] OtherRobotAgents =
        {
            ("bingbot", "Microsoft Bingbot"), ("BingPreview", "Microsoft Bing Preview"), ("msnbot", "Microsoft MSNBot"),
            ("DuckDuckBot", "DuckDuckGo Bot"), ("DuckAssistBot", "DuckDuckGo Assist"),
            ("YandexBot", "Yandex Bot"), ("YandexImages", "Yandex Images"), ("Baiduspider", "Baidu Spider"),
            ("Applebot", "Applebot"), ("PetalBot", "PetalBot"), ("SemrushBot", "SemrushBot"),
            ("AhrefsBot", "AhrefsBot"), ("MJ12bot", "MJ12bot"), ("DotBot", "DotBot"),
            ("Sogou", "Sogou Spider"), ("SeznamBot", "SeznamBot"), ("Qwantbot", "Qwantbot"), ("Exabot", "Exabot"),
            ("GPTBot", "OpenAI GPTBot"), ("OAI-SearchBot", "OpenAI SearchBot"), ("ChatGPT-User", "ChatGPT User"),
            ("ClaudeBot", "Anthropic ClaudeBot"), ("Claude-SearchBot", "Anthropic SearchBot"), ("Claude-User", "Anthropic User"),
            ("PerplexityBot", "PerplexityBot"), ("Perplexity-User", "Perplexity User"),
            ("CCBot", "Common Crawl"), ("Bytespider", "ByteDance Bytespider"), ("Amazonbot", "Amazonbot"),
            ("facebookexternalhit", "Meta/Facebook Preview"), ("Facebot", "Meta Facebot"),
            ("Twitterbot", "X/Twitter Bot"), ("LinkedInBot", "LinkedIn Bot"), ("Slackbot", "Slackbot"),
            ("Discordbot", "Discord Bot"), ("TelegramBot", "Telegram Bot"), ("WhatsApp", "WhatsApp Preview"),
            ("Pinterestbot", "Pinterest Bot"), ("ia_archiver", "Internet Archive"), ("archive.org_bot", "Internet Archive"),
            ("Chrome-Lighthouse", "Lighthouse/Test"), ("PageSpeed Insights", "PageSpeed/Test"),
            ("HeadlessChrome", "Headless Chrome"), ("PhantomJS", "PhantomJS"), ("Selenium", "Selenium"), ("Playwright", "Playwright"),
            ("curl/", "cURL"), ("Wget/", "Wget"), ("python-requests", "Python Requests"),
            ("python-httpx", "Python HTTPX"), ("aiohttp", "Python aiohttp"), ("Go-http-client", "Go HTTP Client"),
            ("PostmanRuntime", "Postman"), ("okhttp", "OkHttp")
        };
        private static readonly string[] GenericRobotMarkers =
        {
            "bot", "crawler", "spider", "slurp", "scraper", "fetcher", "preview",
            "headless", "monitoring", "validator", "scanner", "httpclient"
        };
        private readonly ApplicationDbContext _db;
        private readonly ClientIpService _clientIpService;
        private readonly IpGeolocationService _ipGeolocationService;
        private readonly IMemoryCache _memoryCache;

        public SiteVisitTrackingService(
            ApplicationDbContext db,
            ClientIpService clientIpService,
            IpGeolocationService ipGeolocationService,
            IMemoryCache memoryCache)
        {
            _db = db;
            _clientIpService = clientIpService;
            _ipGeolocationService = ipGeolocationService;
            _memoryCache = memoryCache;
        }

        public SiteVisitTrackingItem? Capture(HttpContext context)
        {
            var userAgent = context.Request.Headers.UserAgent.ToString();
            var traffic = ClassifyTraffic(userAgent);
            if (!ShouldTrack(context, traffic))
                return null;

            var ipAddress = Truncate(_clientIpService.GetClientIp(context), 80);
            string visitorId;
            string device;
            string browser;
            string os;

            if (traffic.IsRobot)
            {
                // Robotlarda ziyaretçi cookie'si üretmeyiz. IP + User-Agent birleşiminden kararlı
                // fakat geri döndürülemeyen bir kimlik türeterek gerçek kullanıcı cookie havuzunu temiz tutarız.
                visitorId = Hash($"robot|{traffic.TrafficType}|{traffic.RobotName}|{ipAddress}|{userAgent}");
                device = "Robot";
                browser = traffic.RobotName ?? "Robot";
                os = "Otomasyon";
            }
            else
            {
                // Gerçek ziyaretçi cookie'si response başlamadan yazılmalı. Ağır DB/GeoIP işi ise
                // response tamamlandıktan sonra background worker tarafından yapılacak.
                visitorId = Hash(GetOrCreateVisitorId(context));
                (device, browser, os) = ParseUserAgent(userAgent);
            }

            return new SiteVisitTrackingItem(
                visitorId,
                Truncate((context.Request.PathBase + context.Request.Path).Value ?? "/", 500) ?? "/",
                Truncate(context.Request.Headers.Referer.ToString(), 800),
                ipAddress,
                device,
                browser,
                os,
                traffic.TrafficType,
                traffic.RobotName,
                context.User?.Identity?.IsAuthenticated == true && !traffic.IsRobot,
                traffic.IsRobot ? null : TryGetUserId(context),
                DateTime.UtcNow);
        }

        public async Task TrackAsync(SiteVisitTrackingItem item, CancellationToken cancellationToken = default)
        {
            var visit = new SiteVisit
            {
                VisitorId = item.VisitorId,
                Path = item.Path,
                Referrer = item.Referrer,
                IpAddress = item.IpAddress,
                DeviceType = item.DeviceType,
                Browser = item.Browser,
                OperatingSystem = item.OperatingSystem,
                TrafficType = item.TrafficType,
                RobotName = item.RobotName,
                IsAuthenticated = item.IsAuthenticated,
                UserId = item.UserId,
                VisitedAtUtc = item.VisitedAtUtc
            };

            // Ziyaret kaydı önce güvenle yazılır. Robot trafiği ayrıca tutulur fakat konum
            // servislerine gönderilmez; böylece bot yoğunluğu GeoIP kotasını ve gerçek trafik
            // kayıt hızını etkilemez.
            _db.SiteVisits.Add(visit);
            await _db.SaveChangesAsync(cancellationToken);

            if (!IsHumanTraffic(visit))
                return;

            var location = await ResolveVisitLocationAsync(item.IpAddress, visit.Id, cancellationToken);
            visit.GeoLookupAtUtc = DateTime.UtcNow;
            visit.GeoLookupAttemptCount = 1;
            if (location != null)
            {
                visit.Country = location.Country;
                visit.City = location.City;
            }
            await _db.SaveChangesAsync(cancellationToken);
        }

        public async Task<AdminVisitorStatisticsViewModel> BuildStatisticsAsync(int days, CancellationToken cancellationToken = default)
        {
            days = days is 7 or 30 or 90 or 365 ? days : 30;
            var nowTr = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TurkeyTimeZone);
            var todayTr = nowTr.Date;
            var periodStartTr = todayTr.AddDays(-(days - 1));
            var monthStartTr = new DateTime(todayTr.Year, todayTr.Month, 1);
            var queryStartTr = new[] { periodStartTr, monthStartTr, todayTr.AddMonths(-11).AddDays(1 - todayTr.Day) }.Min();
            var queryStartUtc = TimeZoneInfo.ConvertTimeToUtc(queryStartTr, TurkeyTimeZone);

            var visits = await _db.SiteVisits.AsNoTracking()
                .Where(x => x.VisitedAtUtc >= queryStartUtc)
                .OrderByDescending(x => x.VisitedAtUtc)
                .ToListAsync(cancellationToken);

            // Eski kayıtların tamamı robotlar geçmişte hiç kaydedilmediği için gerçek trafiktir.
            // Yeni kayıtlarda TrafficType alanı insan / Google / diğer robot ayrımını kalıcı tutar.
            var humanVisits = visits.Where(IsHumanTraffic).ToList();
            var robotVisits = visits.Where(x => !IsHumanTraffic(x)).ToList();

            // Older rows may not contain a location. Reuse the newest known result for the
            // same IP so historical real page views are included without another external call.
            var knownLocationsByIp = humanVisits
                .Where(x => !string.IsNullOrWhiteSpace(x.IpAddress)
                            && (!string.IsNullOrWhiteSpace(x.Country) || !string.IsNullOrWhiteSpace(x.City)))
                .GroupBy(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderByDescending(v => v.VisitedAtUtc)
                        .Select(v => new VisitLocation(NormalizeCountryName(v.Country), NormalizeLocationName(v.City)))
                        .First(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var visit in humanVisits.Where(x => !string.IsNullOrWhiteSpace(x.IpAddress)))
            {
                if (!knownLocationsByIp.TryGetValue(visit.IpAddress!, out var known))
                    continue;

                if (string.IsNullOrWhiteSpace(visit.Country))
                    visit.Country = known.Country;
                if (string.IsNullOrWhiteSpace(visit.City))
                    visit.City = known.City;
            }

            var localized = humanVisits.Select(x => new
            {
                Visit = x,
                LocalTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.VisitedAtUtc, DateTimeKind.Utc), TurkeyTimeZone)
            }).ToList();
            var localizedRobots = robotVisits.Select(x => new
            {
                Visit = x,
                LocalTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.VisitedAtUtc, DateTimeKind.Utc), TurkeyTimeZone)
            }).ToList();

            var period = localized.Where(x => x.LocalTime >= periodStartTr).ToList();
            var today = localized.Where(x => x.LocalTime.Date == todayTr).ToList();
            var month = localized.Where(x => x.LocalTime >= monthStartTr).ToList();
            var periodRobots = localizedRobots.Where(x => x.LocalTime >= periodStartTr).ToList();
            var todayRobots = localizedRobots.Where(x => x.LocalTime.Date == todayTr).ToList();
            var monthRobots = localizedRobots.Where(x => x.LocalTime >= monthStartTr).ToList();

            var pendingLocationIpCount = await _db.SiteVisits.AsNoTracking()
                .Where(x => x.TrafficType == HumanTrafficType
                            && x.IpAddress != null
                            && x.IpAddress != ""
                            && (x.Country == null || x.Country == "" || x.City == null || x.City == "")
                            && x.GeoLookupAttemptCount < 3)
                .Select(x => x.IpAddress!)
                .Distinct()
                .CountAsync(cancellationToken);
            var unresolvedLocationIpCount = await _db.SiteVisits.AsNoTracking()
                .Where(x => x.TrafficType == HumanTrafficType
                            && x.IpAddress != null
                            && x.IpAddress != ""
                            && (x.Country == null || x.Country == "" || x.City == null || x.City == "")
                            && x.GeoLookupAttemptCount >= 3)
                .Select(x => x.IpAddress!)
                .Distinct()
                .CountAsync(cancellationToken);

            var daily = Enumerable.Range(0, days)
                .Select(offset => periodStartTr.AddDays(offset))
                .Select(date =>
                {
                    var rows = period.Where(x => x.LocalTime.Date == date).ToList();
                    return new VisitorTrendItem
                    {
                        Label = date.ToString("dd MMM"),
                        PageViews = rows.Count,
                        UniqueVisitors = rows.Select(x => x.Visit.VisitorId).Distinct().Count()
                    };
                }).ToList();

            var monthStartForTrend = new DateTime(todayTr.Year, todayTr.Month, 1).AddMonths(-11);
            var monthly = Enumerable.Range(0, 12)
                .Select(offset => monthStartForTrend.AddMonths(offset))
                .Select(date =>
                {
                    var rows = localized.Where(x => x.LocalTime.Year == date.Year && x.LocalTime.Month == date.Month).ToList();
                    return new VisitorTrendItem
                    {
                        Label = date.ToString("MMM yy"),
                        PageViews = rows.Count,
                        UniqueVisitors = rows.Select(x => x.Visit.VisitorId).Distinct().Count()
                    };
                }).ToList();

            var periodGoogleRobots = periodRobots.Count(x => IsGoogleRobotTraffic(x.Visit));
            var periodOtherRobots = periodRobots.Count - periodGoogleRobots;
            var todayGoogleRobots = todayRobots.Count(x => IsGoogleRobotTraffic(x.Visit));
            var todayOtherRobots = todayRobots.Count - todayGoogleRobots;
            var monthGoogleRobots = monthRobots.Count(x => IsGoogleRobotTraffic(x.Visit));
            var monthOtherRobots = monthRobots.Count - monthGoogleRobots;
            var totalPeriodTraffic = period.Count + periodRobots.Count;

            return new AdminVisitorStatisticsViewModel
            {
                SelectedDays = days,
                PeriodStart = periodStartTr,
                PeriodEnd = todayTr,
                TodayPageViews = today.Count,
                TodayUniqueVisitors = today.Select(x => x.Visit.VisitorId).Distinct().Count(),
                MonthPageViews = month.Count,
                MonthUniqueVisitors = month.Select(x => x.Visit.VisitorId).Distinct().Count(),
                PeriodPageViews = period.Count,
                PeriodUniqueVisitors = period.Select(x => x.Visit.VisitorId).Distinct().Count(),
                PagesPerVisitor = period.Count == 0 ? 0 : Math.Round((decimal)period.Count / Math.Max(1, period.Select(x => x.Visit.VisitorId).Distinct().Count()), 2),
                TodayGoogleRobotVisits = todayGoogleRobots,
                TodayOtherRobotVisits = todayOtherRobots,
                TodayRobotVisits = todayRobots.Count,
                MonthGoogleRobotVisits = monthGoogleRobots,
                MonthOtherRobotVisits = monthOtherRobots,
                MonthRobotVisits = monthRobots.Count,
                PeriodGoogleRobotVisits = periodGoogleRobots,
                PeriodOtherRobotVisits = periodOtherRobots,
                PeriodRobotVisits = periodRobots.Count,
                RobotTrafficSharePercent = totalPeriodTraffic == 0 ? 0 : Math.Round(periodRobots.Count * 100m / totalPeriodTraffic, 1),
                RobotAgents = BuildRobotBreakdown(periodRobots.Select(x => x.Visit)),
                RobotTopPages = BuildBreakdown(periodRobots.Select(x => NormalizePath(x.Visit.Path))),
                DailyTrend = daily,
                MonthlyTrend = monthly,
                TopPages = BuildBreakdown(period.Select(x => NormalizePath(x.Visit.Path))),
                Devices = BuildBreakdown(period.Select(x => x.Visit.DeviceType)),
                Browsers = BuildBreakdown(period.Select(x => x.Visit.Browser)),
                Countries = BuildCountryBreakdown(period.Select(x => x.Visit)),
                Cities = BuildCityBreakdown(period.Select(x => x.Visit)),
                LocatedPageViews = period.Count(x => !string.IsNullOrWhiteSpace(x.Visit.Country) || !string.IsNullOrWhiteSpace(x.Visit.City)),
                LocationCoveragePercent = period.Count == 0
                    ? 0
                    : Math.Round(period.Count(x => !string.IsNullOrWhiteSpace(x.Visit.Country) || !string.IsNullOrWhiteSpace(x.Visit.City)) * 100m / period.Count, 1),
                LocationBackfillPendingIpCount = pendingLocationIpCount,
                LocationBackfillUnresolvedIpCount = unresolvedLocationIpCount,
                RecentVisits = humanVisits.Take(50).Select(x => new RecentVisitItem
                {
                    VisitorCode = x.VisitorId.Length >= 8 ? x.VisitorId[..8].ToUpperInvariant() : x.VisitorId,
                    Path = x.Path,
                    Device = $"{x.DeviceType} · {x.OperatingSystem}",
                    Browser = x.Browser,
                    IpAddress = x.IpAddress ?? "-",
                    Location = FormatLocation(x.City, x.Country),
                    VisitedAtUtc = x.VisitedAtUtc
                }).ToList()
            };
        }

        private async Task<VisitLocation?> ResolveVisitLocationAsync(
            string? ipAddress,
            long currentVisitId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return null;

            var cacheKey = $"nsx:site-visit-location:{Hash(ipAddress)}";
            if (_memoryCache.TryGetValue(cacheKey, out VisitLocation? cached))
                return cached;
            if (_memoryCache.TryGetValue($"{cacheKey}:miss", out bool _))
                return null;

            var previous = await _db.SiteVisits.AsNoTracking()
                .Where(x => x.Id != currentVisitId
                            && x.TrafficType == HumanTrafficType
                            && x.IpAddress == ipAddress
                            && ((x.Country != null && x.Country != "") || (x.City != null && x.City != "")))
                .OrderByDescending(x => x.VisitedAtUtc)
                .Select(x => new { x.Country, x.City })
                .FirstOrDefaultAsync(cancellationToken);

            if (previous != null)
            {
                var known = new VisitLocation(
                    NormalizeCountryName(previous.Country),
                    NormalizeLocationName(previous.City));
                _memoryCache.Set(cacheKey, known, TimeSpan.FromDays(7));
                return known;
            }

            var resolved = await _ipGeolocationService.ResolveAsync(ipAddress, cancellationToken);
            var location = resolved == null
                ? null
                : new VisitLocation(
                    NormalizeCountryName(resolved.Country),
                    NormalizeLocationName(resolved.City ?? resolved.Region));

            if (location == null
                || (string.IsNullOrWhiteSpace(location.Country) && string.IsNullOrWhiteSpace(location.City)))
            {
                _memoryCache.Set($"{cacheKey}:miss", true, TimeSpan.FromMinutes(20));
                return null;
            }

            _memoryCache.Set(cacheKey, location, TimeSpan.FromDays(7));
            return location;
        }

        private static IReadOnlyList<RobotTrafficItem> BuildRobotBreakdown(IEnumerable<SiteVisit> source)
        {
            var rows = source.ToList();
            var total = Math.Max(1, rows.Count);

            return rows
                .GroupBy(x => new
                {
                    Category = IsGoogleRobotTraffic(x) ? "Google" : "Diğer",
                    Name = string.IsNullOrWhiteSpace(x.RobotName)
                        ? (IsGoogleRobotTraffic(x) ? "Google Robot" : "Diğer Robot")
                        : x.RobotName!.Trim()
                })
                .Select(group => new RobotTrafficItem
                {
                    Category = group.Key.Category,
                    Name = group.Key.Name,
                    Count = group.Count(),
                    Percentage = Math.Round(group.Count() * 100m / total, 1)
                })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name)
                .Take(12)
                .ToList();
        }

        private static IReadOnlyList<VisitorBreakdownItem> BuildBreakdown(IEnumerable<string?> source)
        {
            var values = source.Select(x => string.IsNullOrWhiteSpace(x) ? "Bilinmiyor" : x.Trim()).ToList();
            var total = Math.Max(1, values.Count);
            return values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(g => new VisitorBreakdownItem { Label = g.Key ?? "Bilinmiyor", Count = g.Count(), Percentage = Math.Round(g.Count() * 100m / total, 1) })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Label).Take(10).ToList();
        }

        private static IReadOnlyList<VisitorLocationItem> BuildCountryBreakdown(IEnumerable<SiteVisit> source)
        {
            var located = source
                .Select(x => new { Visit = x, Country = NormalizeCountryName(x.Country) })
                .Where(x => !string.IsNullOrWhiteSpace(x.Country))
                .ToList();
            var total = Math.Max(1, located.Count);

            return located
                .GroupBy(x => NormalizeLocationKey(x.Country!))
                .Select(group => new VisitorLocationItem
                {
                    Name = group.GroupBy(x => x.Country!, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First().Key,
                    UniqueVisitors = group.Select(x => x.Visit.VisitorId).Distinct(StringComparer.Ordinal).Count(),
                    TotalVisits = group.Count(),
                    Percentage = Math.Round(group.Count() * 100m / total, 1)
                })
                .OrderByDescending(x => x.UniqueVisitors)
                .ThenByDescending(x => x.TotalVisits)
                .ThenBy(x => x.Name)
                .Take(12)
                .ToList();
        }

        private static IReadOnlyList<VisitorLocationItem> BuildCityBreakdown(IEnumerable<SiteVisit> source)
        {
            var located = source
                .Where(x => !string.IsNullOrWhiteSpace(x.City))
                .Select(x => new
                {
                    Visit = x,
                    City = NormalizeLocationName(x.City)!,
                    Country = NormalizeCountryName(x.Country)
                })
                .ToList();
            var total = Math.Max(1, located.Count);

            return located
                .GroupBy(x => $"{NormalizeLocationKey(x.Country ?? string.Empty)}|{NormalizeLocationKey(x.City)}")
                .Select(group => new VisitorLocationItem
                {
                    Name = group.GroupBy(x => x.City, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First().Key,
                    Country = group.Select(x => x.Country).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Bilinmiyor",
                    UniqueVisitors = group.Select(x => x.Visit.VisitorId).Distinct(StringComparer.Ordinal).Count(),
                    TotalVisits = group.Count(),
                    Percentage = Math.Round(group.Count() * 100m / total, 1)
                })
                .OrderByDescending(x => x.UniqueVisitors)
                .ThenByDescending(x => x.TotalVisits)
                .ThenBy(x => x.Name)
                .Take(12)
                .ToList();
        }

        private static string FormatLocation(string? city, string? country)
        {
            var parts = new[] { NormalizeLocationName(city), NormalizeCountryName(country) }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var value = string.Join(" / ", parts);
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string? NormalizeLocationName(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string? NormalizeCountryName(string? country)
        {
            if (string.IsNullOrWhiteSpace(country))
                return null;

            var value = country.Trim();
            return NormalizeLocationKey(value) switch
            {
                "TR" or "TURKEY" or "TURKIYE" => "Türkiye",
                "US" or "USA" or "UNITEDSTATES" or "UNITEDSTATESOFAMERICA" or "AMERIKABIRLESIKDEVLETLERI" => "Amerika Birleşik Devletleri",
                "DE" or "GERMANY" or "DEUTSCHLAND" or "ALMANYA" => "Almanya",
                "GB" or "UK" or "UNITEDKINGDOM" or "BIRLESIKKRALLIK" => "Birleşik Krallık",
                "FR" or "FRANCE" or "FRANSA" => "Fransa",
                "AZ" or "AZERBAIJAN" or "AZERBAYCAN" => "Azerbaycan",
                "TM" or "TURKMENISTAN" => "Türkmenistan",
                _ => value
            };
        }

        private static string NormalizeLocationKey(string value)
        {
            var decomposed = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
            return string.Concat(decomposed
                .Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark)
                .Where(char.IsLetterOrDigit))
                .ToUpperInvariant();
        }

        private static bool IsHumanTraffic(SiteVisit visit)
            => string.IsNullOrWhiteSpace(visit.TrafficType)
               || visit.TrafficType.Equals(HumanTrafficType, StringComparison.OrdinalIgnoreCase);

        private static bool IsGoogleRobotTraffic(SiteVisit visit)
            => visit.TrafficType.Equals(GoogleRobotTrafficType, StringComparison.OrdinalIgnoreCase);

        private static RobotClassification ClassifyTraffic(string? userAgent)
        {
            var ua = userAgent ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ua))
                return new RobotClassification(OtherRobotTrafficType, "Tanımsız Otomasyon", true);

            // Google robotlarını diğer otomasyonlardan ayrı göster. Önce özel ajanları
            // kontrol etmek isimlerin panelde daha anlaşılır görünmesini sağlar.
            foreach (var agent in GoogleRobotAgents)
                if (ua.Contains(agent.Marker, StringComparison.OrdinalIgnoreCase))
                    return new RobotClassification(GoogleRobotTrafficType, agent.Name, true);

            foreach (var agent in OtherRobotAgents)
                if (ua.Contains(agent.Marker, StringComparison.OrdinalIgnoreCase))
                    return new RobotClassification(OtherRobotTrafficType, agent.Name, true);

            if (GenericRobotMarkers.Any(marker => ua.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                return new RobotClassification(OtherRobotTrafficType, "Diğer Robot / Otomasyon", true);

            return new RobotClassification(HumanTrafficType, null, false);
        }

        private static bool ShouldTrack(HttpContext context, RobotClassification traffic)
        {
            if (!HttpMethods.IsGet(context.Request.Method)) return false;
            if (context.Response.StatusCode >= 400) return false;
            var path = context.Request.Path.Value ?? "/";
            if (path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/nas", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/presence", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/liveChatHub", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/salontakip", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/teknikservis", StringComparison.OrdinalIgnoreCase)) return false;

            // Gerçek tarayıcılarda yalnız HTML sayfalarını saymaya devam et. Robotlar bazı
            // taramalarda */* gönderdiği için onları Accept başlığına göre kaybetmeyiz.
            if (traffic.IsRobot) return true;
            var accept = context.Request.Headers.Accept.ToString();
            return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetOrCreateVisitorId(HttpContext context)
        {
            if (context.Request.Cookies.TryGetValue(VisitorCookieName, out var existing) && Guid.TryParse(existing, out _)) return existing;
            var value = Guid.NewGuid().ToString("N");
            context.Response.Cookies.Append(VisitorCookieName, value, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                MaxAge = TimeSpan.FromDays(365)
            });
            return value;
        }

        private static int? TryGetUserId(HttpContext context)
        {
            var value = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(value, out var id) ? id : null;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "/";
            return path.Length > 1 ? path.TrimEnd('/') : path;
        }

        private static (string Device, string Browser, string Os) ParseUserAgent(string ua)
        {
            var device = ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || ua.Contains("Android", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "Mobil" : ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "Tablet" : "Masaüstü";
            var browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge" : ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? "Opera" : ua.Contains("CriOS", StringComparison.OrdinalIgnoreCase) || ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome" : ua.Contains("FxiOS", StringComparison.OrdinalIgnoreCase) || ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox" : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari" : "Diğer";
            var os = ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS" : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android" : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows" : ua.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) ? "macOS" : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux" : "Diğer";
            return (device, browser, os);
        }

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        private static string? Truncate(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(max, value.Trim().Length)];
        private static TimeZoneInfo ResolveTurkeyTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        }

        private sealed record RobotClassification(string TrafficType, string? RobotName, bool IsRobot);
        private sealed record VisitLocation(string? Country, string? City);
    }
}
