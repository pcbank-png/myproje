using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed class SiteLocalizationService
    {
        public const string LanguageCookieName = "NSX.Language";
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly LocalizationCatalog _catalog;
        private readonly ApplicationDbContext _db;
        private readonly LocalizationCaptureService _capture;
        private SiteLanguage? _resolvedLanguage;
        private IReadOnlyList<SiteLanguage>? _languages;

        public SiteLocalizationService(
            IHttpContextAccessor httpContextAccessor,
            LocalizationCatalog catalog,
            ApplicationDbContext db,
            LocalizationCaptureService capture)
        {
            _httpContextAccessor = httpContextAccessor;
            _catalog = catalog;
            _db = db;
            _capture = capture;
        }

        public async Task<IReadOnlyList<SiteLanguage>> GetActiveLanguagesAsync(CancellationToken cancellationToken = default)
        {
            if (_languages != null)
                return _languages;

            var activeLanguages = await _catalog.GetActiveLanguagesAsync(cancellationToken);
            return _languages = PublicLanguagePolicy.FilterPublicLanguages(activeLanguages);
        }

        public async Task<SiteLanguage> GetCurrentLanguageAsync(CancellationToken cancellationToken = default)
        {
            if (_resolvedLanguage != null)
                return _resolvedLanguage;

            var languages = await GetActiveLanguagesAsync(cancellationToken);
            var context = _httpContextAccessor.HttpContext;
            var path = context?.Request.Path.Value ?? string.Empty;

            // Admin panel (Login dahil) yalnızca Türkçe çalışır. Cookie / hesap tercihi /
            // makine çevirisi admin arayüzüne uygulanmaz.
            if (IsAdminPanelPath(path))
                return _resolvedLanguage = ResolveSourceDefaultLanguage(languages);

            // /en, /fr, /de gibi kalıcı SEO dil önekleri routing öncesinde PathBase'e
            // taşınır. URL'de açıkça yazan dil cookie, hesap tercihi ve uiLang'den daha
            // güçlüdür; böylece arama motoru /fr/... adresini daima Fransızca görür.
            if (context?.Items[LocalizedRoutePrefixMiddleware.RouteLanguageItemKey] is string routeLanguageCode
                && !string.IsNullOrWhiteSpace(routeLanguageCode))
            {
                var routeLanguage = languages.FirstOrDefault(x =>
                    string.Equals(x.Code, routeLanguageCode, StringComparison.OrdinalIgnoreCase));
                if (routeLanguage != null)
                {
                    if (!context.Response.HasStarted)
                        WriteLanguageCookie(context.Response, context.Request, routeLanguage.Code);
                    return _resolvedLanguage = routeLanguage;
                }
            }

            // Demo/indirme akışından Account sayfalarına geçerken seçili dili query ile de
            // sabitliyoruz. Böylece proxy/GeoIP/tarayıcı dili yeniden değerlendirildiğinde
            // Login/Register ekranı kullanıcının geldiği sayfanın dilinden kopmaz.
            if (context != null
                && path.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase))
            {
                var authFlowLanguageCode = context.Request.Query["uiLang"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(authFlowLanguageCode))
                {
                    var authFlowLanguage = languages.FirstOrDefault(x =>
                        string.Equals(x.Code, authFlowLanguageCode, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.UrlCode, authFlowLanguageCode, StringComparison.OrdinalIgnoreCase));
                    if (authFlowLanguage != null)
                    {
                        if (!context.Response.HasStarted)
                            WriteLanguageCookie(context.Response, context.Request, authFlowLanguage.Code);
                        return _resolvedLanguage = authFlowLanguage;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                var firstSegment = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstSegment))
                {
                    var routeLanguage = languages.FirstOrDefault(x => string.Equals(x.UrlCode, firstSegment, StringComparison.OrdinalIgnoreCase));
                    if (routeLanguage != null)
                    {
                        if (context != null && !context.Response.HasStarted)
                            WriteLanguageCookie(context.Response, context.Request, routeLanguage.Code);
                        return _resolvedLanguage = routeLanguage;
                    }
                }
            }

            var defaultLanguage = ResolveSourceDefaultLanguage(languages);

            // SEO public URL'lerinde dilin tek kaynağı URL'dir. Öneksiz public URL her zaman
            // canonical Türkçe kaynaktır; cookie veya hesap tercihi bu URL'nin HTML dilini
            // değiştiremez. Böylece /, /store, /blog ve /urun/... aynı URL altında bazen
            // İngilizce bazen Türkçe render edilmez. Yabancı dil tercihi varsa redirect
            // middleware'i kullanıcıyı /en, /fr, /de ... karşılığına taşır.
            if (SeoLanguageUrlService.IsPublicLanguagePath(path))
            {
                if (context != null
                    && !context.Response.HasStarted
                    && !context.Request.Cookies.ContainsKey(LanguageCookieName))
                {
                    WriteLanguageCookie(context.Response, context.Request, defaultLanguage.Code);
                }

                return _resolvedLanguage = defaultLanguage;
            }

            // Account ve diğer SEO-dışı ekranlarda son tarayıcı seçimi / hesap tercihi
            // korunur. Public SEO sayfalarında bu tercih yalnız URL redirect'i için kullanılır.
            var preferredLanguage = await GetPreferredLanguageAsync(cancellationToken);
            if (preferredLanguage != null)
                return _resolvedLanguage = preferredLanguage;

            if (context != null && !context.Response.HasStarted)
                WriteLanguageCookie(context.Response, context.Request, defaultLanguage.Code);

            return _resolvedLanguage = defaultLanguage;
        }

        public async Task<SiteLanguage?> GetPreferredLanguageAsync(CancellationToken cancellationToken = default)
        {
            var languages = await GetActiveLanguagesAsync(cancellationToken);
            var context = _httpContextAccessor.HttpContext;

            // En son bu tarayıcıda yapılan seçim birinci önceliktir. Bu metot public SEO
            // render dilini belirlemez; yalnız redirect ve SEO-dışı ekran tercihini çözer.
            var cookieCode = context?.Request.Cookies[LanguageCookieName];
            if (!string.IsNullOrWhiteSpace(cookieCode))
            {
                var cookieLanguage = languages.FirstOrDefault(x =>
                    string.Equals(x.Code, cookieCode, StringComparison.OrdinalIgnoreCase));
                if (cookieLanguage != null)
                    return cookieLanguage;
            }

            try
            {
                if (context?.User?.Identity?.IsAuthenticated == true
                    && int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                {
                    var preferredCode = await _db.UserLanguagePreferences
                        .AsNoTracking()
                        .Where(x => x.UserId == userId)
                        .Select(x => x.LanguageCode)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(preferredCode))
                    {
                        var preferredLanguage = languages.FirstOrDefault(x =>
                            string.Equals(x.Code, preferredCode, StringComparison.OrdinalIgnoreCase));
                        if (preferredLanguage != null)
                            return preferredLanguage;
                    }
                }
            }
            catch
            {
                // Tercih DB'si okunamasa bile public site canonical Türkçe URL ile çalışır.
            }

            return null;
        }

        private static SiteLanguage ResolveSourceDefaultLanguage(IReadOnlyList<SiteLanguage> languages)
            => languages.FirstOrDefault(x => x.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                ?? languages.FirstOrDefault(x => x.IsDefault)
                ?? languages[0];

        public async Task<string> TranslateAsync(string? sourceText, string? languageCode = null, CancellationToken cancellationToken = default)
        {
            var normalized = LocalizationTextKey.Normalize(sourceText);
            if (string.IsNullOrWhiteSpace(normalized))
                return sourceText ?? string.Empty;

            // Anything explicitly sent to TranslateAsync is canonical source text. Capture it
            // before lookup so newly added public Razor/controller strings automatically enter
            // the durable inventory even on the very first request after a publish.
            var requestPath = _httpContextAccessor.HttpContext?.Request.Path.Value;
            if (IsSafePublicCapturePath(requestPath))
                _capture.Enqueue(normalized, requestPath);

            SiteLanguage? language;
            if (string.IsNullOrWhiteSpace(languageCode))
                language = await GetCurrentLanguageAsync(cancellationToken);
            else
                language = await FindActiveLanguageAsync(languageCode, cancellationToken);

            if (language == null)
                return sourceText ?? string.Empty;

            var key = LocalizationTextKey.Create(normalized);
            if (language.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
            {
                var sourceMap = await _catalog.GetSourceMapAsync(cancellationToken);
                return sourceMap.TryGetValue(key, out var currentSource) && !string.IsNullOrWhiteSpace(currentSource)
                    ? currentSource
                    : sourceText ?? string.Empty;
            }

            // Use the target-only map first. The display map intentionally contains Turkish
            // source fallbacks, which is useful for admin tooling but can leak Turkish into a
            // partially translated public language. Public UI instead falls back to reviewed
            // English while the target-language queue catches up.
            //
            // Product/brand display names always prefer the reviewed compile-time English pack.
            // Otherwise leaked Turkish EN rows (filtered from the native map) or bad machine
            // names such as "Current Tracking" keep Turkish program titles on /en/store.
            if (language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                && ProductLocalizationSeedData.TryResolveEnglish(normalized, out var seededProductEnglish))
            {
                return LocalizationDisplayNormalizer.Normalize(seededProductEnglish, language.Code);
            }

            var map = await _catalog.GetNativeTranslationMapAsync(language.Code, cancellationToken);
            if (map.TryGetValue(key, out var translated)
                && !string.IsNullOrWhiteSpace(translated))
            {
                return LocalizationDisplayNormalizer.Normalize(translated, language.Code);
            }

            if (!language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                if (ProductLocalizationSeedData.TryResolveEnglish(normalized, out var seededEnglishFallback))
                    return LocalizationDisplayNormalizer.Normalize(seededEnglishFallback, "en-US");

                var englishMap = await _catalog.GetNativeTranslationMapAsync("en-US", cancellationToken);
                if (englishMap.TryGetValue(key, out var english)
                    && !string.IsNullOrWhiteSpace(english))
                {
                    return LocalizationDisplayNormalizer.Normalize(english, "en-US");
                }
            }

            return sourceText ?? string.Empty;
        }

        private static bool IsAdminPanelPath(string? path)
            => !string.IsNullOrWhiteSpace(path)
               && path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase);

        private static bool IsSafePublicCapturePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            return !IsAdminPanelPath(path)
                && !path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/Dealer", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/Bayi", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/BankTransfer", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/Havale", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/update", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<SiteLanguage?> FindActiveLanguageAsync(string? code, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            var languages = await GetActiveLanguagesAsync(cancellationToken);
            return languages.FirstOrDefault(x => string.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.UrlCode, code.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public void WriteLanguageCookie(HttpResponse response, HttpRequest request, string languageCode)
        {
            response.Cookies.Append(LanguageCookieName, languageCode, new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(365),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = request.IsHttps,
                Path = "/"
            });
            _resolvedLanguage = null;
        }
    }
}
