using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed class LocalizationCatalog
    {
        private const string LanguagesCacheKey = "nsx:i18n:languages:v1";
        private const string SourcesCacheKey = "nsx:i18n:sources:v1";
        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly ILogger<LocalizationCatalog> _logger;

        public LocalizationCatalog(ApplicationDbContext db, IMemoryCache cache, ILogger<LocalizationCatalog> logger)
        {
            _db = db;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IReadOnlyList<SiteLanguage>> GetActiveLanguagesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var cached = await _cache.GetOrCreateAsync(LanguagesCacheKey, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                    return await _db.SiteLanguages
                        .AsNoTracking()
                        .Where(x => x.IsActive)
                        .OrderBy(x => x.SortOrder)
                        .ThenBy(x => x.NativeName)
                        .ToListAsync(cancellationToken);
                });

                if (cached is { Count: > 0 })
                    return cached;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aktif dil kataloğu veritabanından okunamadı; güvenli varsayılanlar kullanılacak.");
            }

            return LocalizationDefaults.Languages;
        }

        public async Task<SiteLanguage> GetDefaultLanguageAsync(CancellationToken cancellationToken = default)
        {
            var languages = await GetActiveLanguagesAsync(cancellationToken);
            // NSX public site's canonical/source language is Turkish. Keep the root URL and
            // first-visit behavior Turkish even if an old database row was accidentally
            // marked as default before the multilingual SEO rollout.
            return languages.FirstOrDefault(x => x.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                ?? languages.FirstOrDefault(x => x.IsDefault)
                ?? languages[0];
        }

        /// <summary>
        /// Turkish is the canonical source catalog. SourceKey is intentionally stable even
        /// after the admin edits the Turkish text. The map therefore exposes both the stable
        /// key and the hash of the current SourceText so old Razor text and later source-code
        /// updates can resolve to the same canonical source row.
        /// </summary>
        public async Task<Dictionary<string, string>> GetSourceMapAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var map = await _cache.GetOrCreateAsync(SourcesCacheKey, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                    var rows = await _db.LocalizationResources
                        .AsNoTracking()
                        .Where(x => !x.IsIgnored && x.SourceText != "")
                        .OrderBy(x => x.Id)
                        .Select(x => new { x.SourceKey, x.SourceText })
                        .ToListAsync(cancellationToken);

                    var result = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var row in rows)
                    {
                        result[row.SourceKey] = row.SourceText;

                        var currentTextKey = LocalizationTextKey.Create(row.SourceText);
                        if (!string.IsNullOrWhiteSpace(currentTextKey))
                            result[currentTextKey] = row.SourceText;
                    }

                    return result;
                });

                return map ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Türkçe ana kaynak kataloğu okunamadı.");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        public async Task<Dictionary<string, string>> GetTranslationMapAsync(string languageCode, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(languageCode) || languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var normalizedCode = languageCode.Trim();
            try
            {
                // Display maps also carry the canonical Turkish source as a fallback. This is
                // important immediately after a source edit: while a target language is being
                // retranslated (or is intentionally excluded from machine translation), the
                // visitor must see the NEW canonical source rather than stale Razor/DB text.
                // Keep this as a copy so GetNativeTranslationMapAsync remains target-only and
                // the machine translator can still correctly detect missing translations.
                var nativeMap = await LoadTranslationMapAsync(normalizedCode, cancellationToken);
                var displayMap = new Dictionary<string, string>(nativeMap, StringComparer.Ordinal);
                var sourceMap = await GetSourceMapAsync(cancellationToken);
                foreach (var pair in sourceMap)
                    displayMap.TryAdd(pair.Key, pair.Value);

                return displayMap;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{LanguageCode} çeviri kataloğu okunamadı.", normalizedCode);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        public async Task<Dictionary<string, string>> GetNativeTranslationMapAsync(string languageCode, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(languageCode) || languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                return await LoadTranslationMapAsync(languageCode.Trim(), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{LanguageCode} yerel çeviri kataloğu okunamadı.", languageCode);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private async Task<Dictionary<string, string>> LoadTranslationMapAsync(string languageCode, CancellationToken cancellationToken)
        {
            var cacheKey = $"nsx:i18n:translations:{languageCode.ToLowerInvariant()}:v1";
            var map = await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                var rows = await _db.LocalizationTranslations
                    .AsNoTracking()
                    .Where(x => x.LanguageCode == languageCode && x.Value != "")
                    .Select(x => new { x.SourceKey, x.Value, x.IsReviewed, x.IsLocked })
                    .ToListAsync(cancellationToken);

                var usableRows = languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                    ? rows.Where(x => (x.IsReviewed && x.IsLocked)
                        || !LocalizationMachineTranslationService.LooksLikeTurkishText(x.Value)).ToList()
                    : rows;

                var sourceKeys = usableRows
                    .Select(x => x.SourceKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var currentSourceByKey = sourceKeys.Length == 0
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : await _db.LocalizationResources
                        .AsNoTracking()
                        .Where(x => sourceKeys.Contains(x.SourceKey))
                        .Select(x => new { x.SourceKey, x.SourceText })
                        .ToDictionaryAsync(x => x.SourceKey, x => x.SourceText, StringComparer.Ordinal, cancellationToken);

                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in usableRows)
                {
                    result[row.SourceKey] = row.Value;
                    if (!currentSourceByKey.TryGetValue(row.SourceKey, out var currentSource))
                        continue;

                    var currentTextKey = LocalizationTextKey.Create(currentSource);
                    if (!string.IsNullOrWhiteSpace(currentTextKey))
                        result[currentTextKey] = row.Value;
                }

                // Reviewed product/brand English seeds always win in the native EN catalog.
                // This closes the gap where a leaked Turkish EN row is filtered out and the
                // public store falls back to the Turkish Product.Name.
                if (languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var seed in ProductLocalizationSeedData.Items)
                    {
                        if (string.IsNullOrWhiteSpace(seed.English))
                            continue;

                        var seedKey = LocalizationTextKey.Create(seed.Source);
                        if (!string.IsNullOrWhiteSpace(seedKey))
                            result[seedKey] = LocalizationTextKey.Normalize(seed.English);
                    }
                }

                return result;
            });

            return map ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public void InvalidateLanguages() => _cache.Remove(LanguagesCacheKey);
        public void InvalidateSources() => _cache.Remove(SourcesCacheKey);

        public void InvalidateTranslations(string? languageCode = null)
        {
            if (!string.IsNullOrWhiteSpace(languageCode))
                _cache.Remove($"nsx:i18n:translations:{languageCode.Trim().ToLowerInvariant()}:v1");
            else
            {
                foreach (var language in LocalizationDefaults.Languages)
                    _cache.Remove($"nsx:i18n:translations:{language.Code.ToLowerInvariant()}:v1");
            }
        }
    }

    public static class LocalizationDefaults
    {
        public static readonly IReadOnlyList<SiteLanguage> Languages = new[]
        {
            new SiteLanguage { Id = -1, Code = "tr-TR", UrlCode = "tr", NativeName = "Türkçe", EnglishName = "Turkish", FlagEmoji = "🇹🇷", Direction = "LTR", IsActive = true, IsDefault = true, SortOrder = 10 },
            new SiteLanguage { Id = -2, Code = "en-US", UrlCode = "en", NativeName = "English", EnglishName = "English", FlagEmoji = "🇬🇧", Direction = "LTR", IsActive = true, SortOrder = 20 }
        };
    }
}
