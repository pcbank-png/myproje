using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Public SEO language contract. The site intentionally exposes only Turkish and English.
    /// Retired locale prefixes stay known so old indexed URLs can be consolidated instead of
    /// turning into long-lived 404 pages.
    /// </summary>
    public static class PublicLanguagePolicy
    {
        private static readonly HashSet<string> SupportedUrlCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "tr", "en"
        };

        private static readonly HashSet<string> RetiredUrlCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "de", "fr", "it", "es", "pt", "pt-br", "pl", "nl", "ro", "ru", "ar",
            "ja", "ko", "az", "kk", "uz", "ky", "tk"
        };

        public static IReadOnlyList<SiteLanguage> FilterPublicLanguages(IEnumerable<SiteLanguage> languages)
        {
            var filtered = (languages ?? Array.Empty<SiteLanguage>())
                .Where(IsSupported)
                .GroupBy(x => Normalize(x.UrlCode), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(y => y.IsDefault).ThenBy(y => y.SortOrder).First())
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.NativeName)
                .ToList();

            if (filtered.Count > 0)
                return filtered;

            // Database/bootstrap failures must never reopen retired public languages.
            return LocalizationDefaults.Languages
                .Where(IsSupported)
                .OrderBy(x => x.SortOrder)
                .ToList();
        }

        public static bool IsSupported(SiteLanguage? language)
        {
            if (language == null)
                return false;

            // URL identity is the public SEO identity. If it exists, require it to be
            // explicitly TR or EN so a stale/misconfigured row can never reopen /de, /fr, etc.
            var urlCode = Normalize(language.UrlCode);
            if (!string.IsNullOrWhiteSpace(urlCode))
                return SupportedUrlCodes.Contains(urlCode);

            var code = Normalize(language.Code).Split('-', 2)[0];
            return SupportedUrlCodes.Contains(code);
        }

        public static bool IsRetiredUrlCode(string? urlCode)
            => RetiredUrlCodes.Contains(Normalize(urlCode));

        // Turkish is the source language. English is the only machine-translation/public
        // target while the site operates in the intentional TR+EN mode.
        public static bool IsPublicTargetLanguageCode(string? code)
        {
            var normalized = Normalize(code);
            return normalized == "en" || normalized.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string? value)
            => (value ?? string.Empty).Trim().Trim('/').Replace('_', '-').ToLowerInvariant();
    }
}
