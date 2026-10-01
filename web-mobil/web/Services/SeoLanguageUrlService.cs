using System.Text.RegularExpressions;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed class SeoLanguageUrlService
    {
        private static readonly Regex AnchorHrefRegex = new(
            "(?<prefix><a\\b[^>]*?\\bhref\\s*=\\s*)(?<quote>[\\\"'])(?<url>/[^\\\"']*)(\\k<quote>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly SiteLocalizationService _localization;

        public SeoLanguageUrlService(SiteLocalizationService localization)
        {
            _localization = localization;
        }

        public async Task<SeoLanguageLinks> BuildAsync(
            string? canonicalPathOrUrl,
            SiteLanguage? currentLanguage = null,
            CancellationToken cancellationToken = default)
        {
            var languages = await _localization.GetActiveLanguagesAsync(cancellationToken);
            var defaultLanguage = ResolveDefaultLanguage(languages);
            currentLanguage ??= await _localization.GetCurrentLanguageAsync(cancellationToken);

            var basePath = NormalizeBasePath(canonicalPathOrUrl, languages);
            if (!IsPublicLanguagePath(basePath))
            {
                var privateUrl = SeoTextHelper.AbsoluteUrl(basePath);
                return new SeoLanguageLinks(
                    privateUrl,
                    privateUrl,
                    basePath,
                    currentLanguage,
                    defaultLanguage,
                    Array.Empty<SeoAlternateLink>());
            }

            var alternates = languages
                .Select(language => new SeoAlternateLink(
                    NormalizeHrefLang(language),
                    SeoTextHelper.AbsoluteUrl(BuildLocalizedPath(basePath, language, defaultLanguage))))
                .ToList();

            var currentUrl = SeoTextHelper.AbsoluteUrl(BuildLocalizedPath(basePath, currentLanguage, defaultLanguage));
            var defaultUrl = SeoTextHelper.AbsoluteUrl(BuildLocalizedPath(basePath, defaultLanguage, defaultLanguage));

            return new SeoLanguageLinks(currentUrl, defaultUrl, basePath, currentLanguage, defaultLanguage, alternates);
        }

        public async Task<string> BuildLocalizedReturnUrlAsync(
            string? returnUrl,
            SiteLanguage targetLanguage,
            CancellationToken cancellationToken = default)
        {
            var languages = await _localization.GetActiveLanguagesAsync(cancellationToken);
            var defaultLanguage = ResolveDefaultLanguage(languages);

            var value = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
                value = absolute.PathAndQuery + absolute.Fragment;

            var fragment = string.Empty;
            var fragmentIndex = value.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                fragment = value[fragmentIndex..];
                value = value[..fragmentIndex];
            }

            var query = string.Empty;
            var queryIndex = value.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = value[queryIndex..];
                value = value[..queryIndex];
            }

            var basePath = NormalizeBasePath(value, languages);
            var localizedPath = IsPublicLanguagePath(basePath)
                ? BuildLocalizedPath(basePath, targetLanguage, defaultLanguage)
                : basePath;
            return localizedPath + query + fragment;
        }

        public async Task<string> RewritePublicAnchorLinksAsync(
            string html,
            SiteLanguage currentLanguage,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html;

            var languages = await _localization.GetActiveLanguagesAsync(cancellationToken);
            var defaultLanguage = ResolveDefaultLanguage(languages);
            if (SameLanguage(currentLanguage, defaultLanguage))
                return html;

            return AnchorHrefRegex.Replace(html, match =>
            {
                var url = match.Groups["url"].Value;
                if (!ShouldLocalizeAnchor(url, languages))
                    return match.Value;

                var localized = LocalizeRelativeUrl(url, currentLanguage, defaultLanguage, languages);
                return match.Groups["prefix"].Value
                    + match.Groups["quote"].Value
                    + localized
                    + match.Groups["quote"].Value;
            });
        }

        public static string BuildLocalizedPath(string basePath, SiteLanguage language, SiteLanguage defaultLanguage)
        {
            var normalized = SeoTextHelper.CanonicalPath(basePath);
            if (SameLanguage(language, defaultLanguage))
                return normalized;

            var prefix = NormalizeUrlCode(language.UrlCode);
            return normalized == "/" ? $"/{prefix}" : $"/{prefix}{normalized}";
        }

        public static SiteLanguage ResolveDefaultLanguage(IReadOnlyList<SiteLanguage> languages)
            => languages.FirstOrDefault(x => x.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                ?? languages.FirstOrDefault(x => x.IsDefault)
                ?? languages.First();

        public static string NormalizeBasePath(string? pathOrUrl, IReadOnlyList<SiteLanguage> languages)
        {
            var value = string.IsNullOrWhiteSpace(pathOrUrl) ? "/" : pathOrUrl.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
                value = absolute.AbsolutePath;

            var queryIndex = value.IndexOf('?');
            if (queryIndex >= 0)
                value = value[..queryIndex];
            var fragmentIndex = value.IndexOf('#');
            if (fragmentIndex >= 0)
                value = value[..fragmentIndex];

            if (!value.StartsWith('/'))
                value = "/" + value;

            var trimmed = value.Trim('/');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                var slash = trimmed.IndexOf('/');
                var first = slash >= 0 ? trimmed[..slash] : trimmed;
                var routeLanguage = languages.FirstOrDefault(x =>
                    string.Equals(NormalizeUrlCode(x.UrlCode), first, StringComparison.OrdinalIgnoreCase));
                if (routeLanguage != null)
                {
                    value = slash >= 0 ? "/" + trimmed[(slash + 1)..] : "/";
                }
            }

            return SeoTextHelper.CanonicalPath(value);
        }

        private static string LocalizeRelativeUrl(
            string url,
            SiteLanguage targetLanguage,
            SiteLanguage defaultLanguage,
            IReadOnlyList<SiteLanguage> languages)
        {
            var fragment = string.Empty;
            var fragmentIndex = url.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                fragment = url[fragmentIndex..];
                url = url[..fragmentIndex];
            }

            var query = string.Empty;
            var queryIndex = url.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = url[queryIndex..];
                url = url[..queryIndex];
            }

            var basePath = NormalizeBasePath(url, languages);
            return BuildLocalizedPath(basePath, targetLanguage, defaultLanguage) + query + fragment;
        }

        private static bool ShouldLocalizeAnchor(string url, IReadOnlyList<SiteLanguage> languages)
        {
            if (string.IsNullOrWhiteSpace(url)
                || url.StartsWith("//", StringComparison.Ordinal)
                || url.StartsWith("/#", StringComparison.Ordinal))
                return false;

            var clean = url.Split('?', '#')[0];
            if (clean == "/")
                return true;

            var first = clean.Trim('/').Split('/', 2)[0];
            if (languages.Any(x => string.Equals(NormalizeUrlCode(x.UrlCode), first, StringComparison.OrdinalIgnoreCase)))
                return false;

            return IsPublicLanguagePath(clean);
        }

        public static bool IsRouteLanguagePath(string? path)
        {
            // Kalıcı dil öneki yalnız public içerik rotalarında yaşar. Account, ödeme,
            // indirme, API ve statik dosyalar tek kök URL kullanır; böylece PathBase özel
            // ekranların asset/form adreslerine sızmaz ve gereksiz kopya URL oluşmaz.
            return IsPublicLanguagePath(path);
        }

        public static bool IsPublicLanguagePath(string? path)
        {
            var clean = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            var queryIndex = clean.IndexOf('?');
            if (queryIndex >= 0) clean = clean[..queryIndex];
            var fragmentIndex = clean.IndexOf('#');
            if (fragmentIndex >= 0) clean = clean[..fragmentIndex];
            if (!clean.StartsWith('/')) clean = "/" + clean;
            if (clean == "/") return true;

            var lower = clean.ToLowerInvariant();
            string[] excludedPrefixes =
            {
                "/admin", "/account", "/language", "/api", "/nas", "/css", "/js", "/lib", "/assets",
                "/nsx-assets", "/uploads", "/generated", "/favicon", "/robots.txt", "/sitemap.xml",
                "/livechathub", "/teknikservis", "/salontakip", "/veresiye", "/license", "/update",
                "/presence", "/media", "/dealer", "/bayi", "/banktransfer", "/havale-bildirimi",
                "/customer", "/hata", "/sayfa-bulunamadi", "/store/download",
                "/store/free-download-start", "/store/freeactivation", "/store/freeprogram"
            };

            return !excludedPrefixes.Any(lower.StartsWith);
        }

        private static string NormalizeHrefLang(SiteLanguage language)
        {
            var code = (language.Code ?? string.Empty).Trim().Replace('_', '-');
            return string.IsNullOrWhiteSpace(code) ? NormalizeUrlCode(language.UrlCode) : code;
        }

        private static string NormalizeUrlCode(string? code)
        {
            var value = (code ?? string.Empty).Trim().Trim('/').ToLowerInvariant();
            return string.IsNullOrWhiteSpace(value) ? "tr" : value;
        }

        private static bool SameLanguage(SiteLanguage left, SiteLanguage right)
            => left.Id != 0 && right.Id != 0
                ? left.Id == right.Id
                : string.Equals(left.Code, right.Code, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record SeoAlternateLink(string HrefLang, string Url);

    public sealed record SeoLanguageLinks(
        string CurrentUrl,
        string DefaultUrl,
        string BasePath,
        SiteLanguage CurrentLanguage,
        SiteLanguage DefaultLanguage,
        IReadOnlyList<SeoAlternateLink> Alternates);
}
