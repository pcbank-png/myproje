using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed record GuideTranslationApplyResult(int Added, int Updated, int Preserved, int ResourcesAdded)
    {
        public int Applied => Added + Updated;
    }

    /// <summary>
    /// Applies the built-in guide translation memory to the normal localization tables.
    /// Existing manual English values are preserved; only missing/weak/legacy generated
    /// guide rows are replaced. This operation never calls a remote translation provider.
    /// </summary>
    public sealed class LocalizationGuideTranslationService
    {
        private readonly ApplicationDbContext _db;
        private readonly LocalizationCatalog _catalog;

        public LocalizationGuideTranslationService(ApplicationDbContext db, LocalizationCatalog catalog)
        {
            _db = db;
            _catalog = catalog;
        }

        public async Task<GuideTranslationApplyResult> ApplyEnglishAsync(CancellationToken cancellationToken = default)
        {
            const string languageCode = "en-US";
            var seeds = LocalizationEnglishGuideSeedData.Items;
            var keys = seeds.Select(x => LocalizationTextKey.Create(x.Source)).Distinct(StringComparer.Ordinal).ToArray();
            if (keys.Length == 0)
                return new GuideTranslationApplyResult(0, 0, 0, 0);

            var now = DateTime.Now;
            var resourceRows = await _db.LocalizationResources
                .Where(x => keys.Contains(x.SourceKey))
                .ToListAsync(cancellationToken);
            var resources = resourceRows.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);

            var resourcesAdded = 0;
            foreach (var seed in seeds)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (resources.ContainsKey(key))
                    continue;

                var resource = new LocalizationResource
                {
                    SourceKey = key,
                    SourceText = LocalizationTextKey.Normalize(seed.Source),
                    FirstSeenPath = seed.Area,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    HitCount = 1
                };
                resources[key] = resource;
                _db.LocalizationResources.Add(resource);
                resourcesAdded++;
            }

            // A Guide key is only protected by the built-in local catalog while its
            // canonical Turkish source is still the original seed text. SourceKey remains
            // stable after a Turkish admin edit, so key equality alone is no longer enough.
            var applicableSeeds = seeds
                .Where(seed =>
                {
                    var key = LocalizationTextKey.Create(seed.Source);
                    return resources.TryGetValue(key, out var resource)
                           && string.Equals(
                               LocalizationTextKey.Normalize(resource.SourceText),
                               LocalizationTextKey.Normalize(seed.Source),
                               StringComparison.Ordinal);
                })
                .ToArray();
            var applicableKeys = applicableSeeds
                .Select(x => LocalizationTextKey.Create(x.Source))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var existingTranslationRows = await _db.LocalizationTranslations
                .Where(x => x.LanguageCode == languageCode && applicableKeys.Contains(x.SourceKey))
                .ToListAsync(cancellationToken);
            var existingRows = existingTranslationRows.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);

            // Older releases seeded English from several different built-in packs.
            // A reviewed+locked row is therefore not automatically a manual admin edit.
            // Build the set of known historical built-in values so the one-click Guide
            // action can safely replace those exact values with the curated Guide catalog.
            var legacyBuiltIn = BuildLegacyBuiltInEnglish(languageCode);

            var added = 0;
            var updated = 0;
            var preserved = 0;

            foreach (var seed in applicableSeeds)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                var target = LocalizationDisplayNormalizer.Normalize(seed.English, languageCode);
                if (string.IsNullOrWhiteSpace(target))
                    continue;

                if (!existingRows.TryGetValue(key, out var row))
                {
                    row = new LocalizationTranslation
                    {
                        SourceKey = key,
                        LanguageCode = languageCode,
                        Value = target,
                        IsMachineTranslated = false,
                        IsReviewed = true,
                        IsLocked = true,
                        UpdatedAt = now
                    };
                    existingRows[key] = row;
                    _db.LocalizationTranslations.Add(row);
                    added++;
                    continue;
                }

                if (ShouldReplace(row, key, legacyBuiltIn))
                {
                    row.Value = target;
                    row.IsMachineTranslated = false;
                    row.IsReviewed = true;
                    row.IsLocked = true;
                    row.UpdatedAt = now;
                    updated++;
                }
                else
                {
                    preserved++;
                }
            }

            // Any old pending/failed machine-translation job for a guide key is now obsolete.
            // Mark it completed so the background worker never needs to send this guide text
            // to a remote provider after the local reviewed translation has been applied.
            var jobs = await _db.LocalizationTranslationJobs
                .Where(x => x.LanguageCode == languageCode && applicableKeys.Contains(x.SourceKey))
                .ToListAsync(cancellationToken);
            foreach (var job in jobs)
            {
                job.Status = "Completed";
                job.CompletedAt = now;
                job.StartedAt = null;
                job.NextAttemptAt = null;
                job.LastError = null;
                job.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (resourcesAdded > 0)
                _catalog.InvalidateSources();
            _catalog.InvalidateTranslations(languageCode);
            return new GuideTranslationApplyResult(added, updated, preserved, resourcesAdded);
        }

        private static bool ShouldReplace(
            LocalizationTranslation row,
            string sourceKey,
            IReadOnlyDictionary<string, HashSet<string>> legacyBuiltIn)
        {
            if (string.IsNullOrWhiteSpace(row.Value))
                return true;

            var normalizedCurrent = LocalizationDisplayNormalizer.Normalize(row.Value, "en-US");

            // Exact values shipped by an older built-in seed are safe to upgrade. This is
            // what separates old reviewed/locked package data from a genuinely custom
            // translation written later by an administrator.
            if (legacyBuiltIn.TryGetValue(sourceKey, out var knownValues)
                && knownValues.Contains(normalizedCurrent))
                return true;

            if (row.IsMachineTranslated)
                return true;

            // A reviewed+locked value that differs from every known built-in value is
            // treated as an intentional admin edit and remains authoritative.
            if (row.IsReviewed && row.IsLocked)
                return false;

            if (LocalizationMachineTranslationService.LooksLikeTurkishText(row.Value))
                return true;

            return !row.IsReviewed || !row.IsLocked;
        }

        private static IReadOnlyDictionary<string, HashSet<string>> BuildLegacyBuiltInEnglish(string languageCode)
        {
            var seeds = LocalizationCapturedContentSeedData.Items
                .Concat(LocalizationEnglishOfflineUiSeedData.Items)
                .Concat(LocalizationSeedData.Items)
                .Concat(ProductLocalizationSeedData.Items)
                .Concat(LocalizationEnglishExtendedSeedData.Items)
                .Concat(LocalizationEnglishAccountSeedData.Items)
                .Concat(LocalizationEnglishBankTransferSeedData.Items)
                .Concat(LocalizationEnglishSharedSeedData.Items)
                .Concat(LocalizationEnglishContentSeedData.Items)
                .Concat(LocalizationEnglishDealerSeedData.Items)
                .Concat(LocalizationFinalSeedData.Items)
                .Concat(LocalizationAdminSeedData.Items)
                .Concat(LocalizationGermanArabicExtendedSeedData.Items);

            var output = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var seed in seeds)
            {
                if (string.IsNullOrWhiteSpace(seed.Source) || string.IsNullOrWhiteSpace(seed.English))
                    continue;

                var key = LocalizationTextKey.Create(seed.Source);
                var value = LocalizationDisplayNormalizer.Normalize(seed.English, languageCode);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!output.TryGetValue(key, out var values))
                {
                    values = new HashSet<string>(StringComparer.Ordinal);
                    output[key] = values;
                }

                values.Add(value);
            }

            return output;
        }
    }
}
