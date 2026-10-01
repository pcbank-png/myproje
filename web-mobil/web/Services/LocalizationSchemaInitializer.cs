using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public static class LocalizationSchemaInitializer
    {
        public static async Task EnsureAsync(IServiceProvider services, ILogger logger)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS `SiteLanguages` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `Code` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
                    `UrlCode` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
                    `NativeName` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
                    `EnglishName` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
                    `FlagEmoji` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
                    `Direction` varchar(3) CHARACTER SET ascii NOT NULL DEFAULT 'LTR',
                    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
                    `IsDefault` tinyint(1) NOT NULL DEFAULT 0,
                    `SortOrder` int NOT NULL DEFAULT 0,
                    `CreatedAt` datetime(6) NOT NULL,
                    `UpdatedAt` datetime(6) NULL,
                    PRIMARY KEY (`Id`),
                    UNIQUE INDEX `IX_SiteLanguages_Code` (`Code`),
                    UNIQUE INDEX `IX_SiteLanguages_UrlCode` (`UrlCode`),
                    INDEX `IX_SiteLanguages_Active_Sort` (`IsActive`,`SortOrder`)
                ) CHARACTER SET=utf8mb4;");

                await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS `LocalizationResources` (
                    `Id` bigint NOT NULL AUTO_INCREMENT,
                    `SourceKey` varchar(64) CHARACTER SET ascii NOT NULL,
                    `SourceText` longtext CHARACTER SET utf8mb4 NOT NULL,
                    `FirstSeenPath` varchar(500) CHARACTER SET utf8mb4 NULL,
                    `FirstSeenAt` datetime(6) NOT NULL,
                    `LastSeenAt` datetime(6) NOT NULL,
                    `HitCount` bigint NOT NULL DEFAULT 1,
                    `IsIgnored` tinyint(1) NOT NULL DEFAULT 0,
                    PRIMARY KEY (`Id`),
                    UNIQUE INDEX `IX_LocalizationResources_SourceKey` (`SourceKey`),
                    INDEX `IX_LocalizationResources_LastSeenAt` (`LastSeenAt`)
                ) CHARACTER SET=utf8mb4;");

                await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS `LocalizationTranslations` (
                    `Id` bigint NOT NULL AUTO_INCREMENT,
                    `SourceKey` varchar(64) CHARACTER SET ascii NOT NULL,
                    `LanguageCode` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
                    `Value` longtext CHARACTER SET utf8mb4 NOT NULL,
                    `IsMachineTranslated` tinyint(1) NOT NULL DEFAULT 0,
                    `IsReviewed` tinyint(1) NOT NULL DEFAULT 0,
                    `IsLocked` tinyint(1) NOT NULL DEFAULT 0,
                    `UpdatedAt` datetime(6) NOT NULL,
                    PRIMARY KEY (`Id`),
                    UNIQUE INDEX `IX_LocalizationTranslations_Key_Language` (`SourceKey`,`LanguageCode`),
                    INDEX `IX_LocalizationTranslations_LanguageCode` (`LanguageCode`)
                ) CHARACTER SET=utf8mb4;");

                await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS `LocalizationTranslationJobs` (
                    `Id` bigint NOT NULL AUTO_INCREMENT,
                    `SourceKey` varchar(64) CHARACTER SET ascii NOT NULL,
                    `LanguageCode` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
                    `Status` varchar(20) CHARACTER SET ascii NOT NULL DEFAULT 'Pending',
                    `AttemptCount` int NOT NULL DEFAULT 0,
                    `LastError` varchar(1000) CHARACTER SET utf8mb4 NULL,
                    `CreatedAt` datetime(6) NOT NULL,
                    `UpdatedAt` datetime(6) NOT NULL,
                    `StartedAt` datetime(6) NULL,
                    `CompletedAt` datetime(6) NULL,
                    `NextAttemptAt` datetime(6) NULL,
                    PRIMARY KEY (`Id`),
                    UNIQUE INDEX `IX_LocalizationTranslationJobs_Key_Language` (`SourceKey`,`LanguageCode`),
                    INDEX `IX_LocalizationTranslationJobs_Status_Next` (`Status`,`NextAttemptAt`),
                    INDEX `IX_LocalizationTranslationJobs_LanguageCode` (`LanguageCode`)
                ) CHARACTER SET=utf8mb4;");

                await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS `UserLanguagePreferences` (
                    `UserId` int NOT NULL,
                    `LanguageCode` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
                    `UpdatedAt` datetime(6) NOT NULL,
                    PRIMARY KEY (`UserId`),
                    INDEX `IX_UserLanguagePreferences_LanguageCode` (`LanguageCode`)
                ) CHARACTER SET=utf8mb4;");


                await SeedLanguagesAsync(db);
                await SeedCoreTranslationsAsync(db);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "NSX dil motoru veritabanı şeması hazırlanamadı. Site Türkçe güvenli modda çalışmaya devam edecek.");
            }
        }


        private static async Task SeedLanguagesAsync(ApplicationDbContext db)
        {
            var existingCodes = await db.SiteLanguages
                .Select(x => x.Code)
                .ToListAsync();
            var existingSet = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;

            foreach (var language in LocalizationDefaults.Languages.Where(x => !existingSet.Contains(x.Code)))
            {
                db.SiteLanguages.Add(new SiteLanguage
                {
                    Code = language.Code,
                    UrlCode = language.UrlCode,
                    NativeName = language.NativeName,
                    EnglishName = language.EnglishName,
                    FlagEmoji = language.FlagEmoji,
                    Direction = language.Direction,
                    IsActive = language.IsActive,
                    IsDefault = language.IsDefault,
                    SortOrder = language.SortOrder,
                    CreatedAt = now
                });
            }

            await db.SaveChangesAsync();

            // Turkish is the immutable source/canonical language of the public site. Older
            // installations may contain a different IsDefault flag from the pre-SEO language
            // engine. Normalize that state so Admin, first-visit rendering and hreflang agree.
            //
            // SEO recovery policy: the public site intentionally operates only in TR + EN.
            // Keep historical language rows/translations for data safety, but force every
            // retired language inactive so old queue/cache state cannot silently reopen it.
            var languageRows = await db.SiteLanguages.ToListAsync();
            var sourceLanguage = languageRows.FirstOrDefault(x =>
                x.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase));

            foreach (var language in languageRows)
            {
                var shouldBeActive = PublicLanguagePolicy.IsSupported(language);
                if (language.IsActive != shouldBeActive)
                {
                    language.IsActive = shouldBeActive;
                    language.UpdatedAt = DateTime.Now;
                }

                if (!shouldBeActive && language.IsDefault)
                {
                    language.IsDefault = false;
                    language.UpdatedAt = DateTime.Now;
                }
            }

            if (sourceLanguage != null)
            {
                foreach (var staleDefault in languageRows.Where(x => x.IsDefault && x.Id != sourceLanguage.Id))
                    staleDefault.IsDefault = false;

                sourceLanguage.IsDefault = true;
                sourceLanguage.IsActive = true;
                sourceLanguage.UpdatedAt = DateTime.Now;
            }

            await db.SaveChangesAsync();
        }

        private static async Task SeedCoreTranslationsAsync(ApplicationDbContext db)
        {
            var publicAuthSeeds = LocalizationPublicAuthSeedData.Items.ToArray();
            var publicAccountSeeds = LocalizationPublicAccountSeedData.Items.ToArray();
            var conversionSeeds = LocalizationConversionFlowSeedData.Items.ToArray();
            var capturedSeeds = LocalizationCapturedContentSeedData.Items.ToArray();
            var protectedCapturedSeeds = capturedSeeds
                .Where(x => IsRestrictedLocalizationArea(x.Area))
                .ToArray();
            var safePublicCapturedSeeds = capturedSeeds
                .Where(x => !IsRestrictedLocalizationArea(x.Area)
                            && !string.IsNullOrWhiteSpace(x.English)
                            && !LocalizationMachineTranslationService.LooksLikeTurkishText(x.English))
                .ToArray();

            var guideSeeds = LocalizationEnglishGuideSeedData.Items.ToArray();
            var guideKeys = guideSeeds
                .Select(x => LocalizationTextKey.Create(x.Source))
                .ToHashSet(StringComparer.Ordinal);

            // Public auth comes first so duplicate source keys receive the safe, curated
            // /Public/Auth classification instead of the restricted /Account path.
            var reviewedSeeds = publicAuthSeeds
                .Concat(publicAccountSeeds)
                .Concat(conversionSeeds)
                .Concat(LocalizationEnglishOfflineUiSeedData.Items)
                .Concat(LocalizationSeedData.Items)
                .Concat(ProductLocalizationSeedData.Items)
                .Concat(LocalizationEnglishExtendedSeedData.Items)
                .Concat(LocalizationEnglishBankTransferSeedData.Items)
                .Concat(LocalizationEnglishSharedSeedData.Items)
                .Concat(LocalizationEnglishContentSeedData.Items)
                .Concat(LocalizationEnglishDealerSeedData.Items)
                .Concat(LocalizationFinalSeedData.Items)
                .Concat(LocalizationAdminSeedData.Items)
                .Concat(safePublicCapturedSeeds)
                .Concat(protectedCapturedSeeds)
                .ToArray();

            var allSeeds = guideSeeds
                .Concat(reviewedSeeds)
                .Concat(capturedSeeds)
                .GroupBy(x => LocalizationTextKey.Create(x.Source), StringComparer.Ordinal)
                .Select(x => x.First())
                .ToArray();
            if (allSeeds.Length == 0)
                return;

            var keys = allSeeds.Select(x => LocalizationTextKey.Create(x.Source)).Distinct().ToArray();
            var existingResourceList = await db.LocalizationResources
                .Where(x => keys.Contains(x.SourceKey))
                .Select(x => x.SourceKey)
                .ToListAsync();
            var existingResources = existingResourceList.ToHashSet(StringComparer.Ordinal);
            var now = DateTime.Now;

            foreach (var seed in allSeeds)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (existingResources.Add(key))
                {
                    db.LocalizationResources.Add(new LocalizationResource
                    {
                        SourceKey = key,
                        SourceText = LocalizationTextKey.Normalize(seed.Source),
                        FirstSeenPath = seed.Area,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        HitCount = 1
                    });
                }
            }
            await db.SaveChangesAsync();

            // Existing installations may already have curated static UI keys registered
            // under restricted runtime routes. Reclassify ONLY compile-time reviewed public
            // UI keys; never reclassify captured form values, payment details or arbitrary
            // Account/BankTransfer response text. This lets safe labels enter the language
            // queue while sensitive user/payment data remains permanently excluded.
            var curatedPublicSeeds = publicAuthSeeds
                .Concat(publicAccountSeeds)
                .Concat(conversionSeeds)
                .Concat(LocalizationEnglishBankTransferSeedData.Items)
                .Where(x => x.Area.StartsWith("/Public/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var curatedPublicAreas = curatedPublicSeeds
                .GroupBy(x => LocalizationTextKey.Create(x.Source), StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().Area, StringComparer.Ordinal);
            var curatedPublicKeys = curatedPublicAreas.Keys.ToArray();
            var existingCuratedPublicResources = await db.LocalizationResources
                .Where(x => curatedPublicKeys.Contains(x.SourceKey))
                .ToListAsync();

            foreach (var resource in existingCuratedPublicResources)
            {
                if (curatedPublicAreas.TryGetValue(resource.SourceKey, out var publicArea)
                    && !string.Equals(resource.FirstSeenPath, publicArea, StringComparison.OrdinalIgnoreCase))
                {
                    resource.FirstSeenPath = publicArea;
                }
            }

            await db.SaveChangesAsync();

            // A historical machine-translation attempt could leave this public warning as
            // Turkish in a non-Turkish target language. Retire only stale/unreviewed rows;
            // reviewed+locked administrator translations remain authoritative. The normal
            // startup queue below will recreate the missing target for every active language.
            await RepairBankTransferWarningTranslationsAsync(db, now);

            // Upgrade path from the old architecture: Turkish used to be stored as if it
            // were a target translation, even though runtime never read that row. Promote
            // the latest legacy tr-* value to the canonical resource SourceText once, keep
            // SourceKey stable, invalidate stale targets, and retire legacy Turkish rows.
            await PromoteLegacyTurkishTranslationsAsync(db, now);

            // Older generated catalogs registered URLs, e-mail examples and similar
            // technical literals as if they were human language. The machine service
            // correctly refuses to translate those values, but their durable jobs then
            // remain Pending forever and keep the dashboard below 100%. Retire only
            // values that must remain verbatim; real human text is never touched here.
            await RetireInvariantResourcesAsync(db, now);

            // Older provider runs could mark an unchanged Turkish source as a successful
            // Italian/German/etc. target. Clear only those exact, unreviewed copies so the
            // background queue retries them with the stricter validation rules.
            await RepairUnchangedTurkishTargetRowsAsync(db, now);

            // SourceKey is now a permanent identity. Admin Turkish edits intentionally keep
            // that key stable, therefore an old built-in seed must never restore a target
            // translation merely because its historical key still matches. Seed/repair only
            // while the canonical SourceText is still exactly the original seed source.
            var currentSourceRows = await db.LocalizationResources.AsNoTracking()
                .Where(x => keys.Contains(x.SourceKey))
                .Select(x => new { x.SourceKey, x.SourceText })
                .ToListAsync();
            var currentSourceByKey = currentSourceRows
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last().SourceText, StringComparer.Ordinal);
            bool SourceStillMatchesSeed(string key, string? seedSource)
                => currentSourceByKey.TryGetValue(key, out var currentSource)
                   && string.Equals(
                       LocalizationTextKey.Normalize(currentSource),
                       LocalizationTextKey.Normalize(seedSource),
                       StringComparison.Ordinal);

            var languageCodes = new[] { "en-US" };
            var existingTranslations = await db.LocalizationTranslations
                .Where(x => keys.Contains(x.SourceKey) && languageCodes.Contains(x.LanguageCode))
                .ToListAsync();
            var existingSet = existingTranslations
                .Select(x => x.SourceKey + "|" + x.LanguageCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingLookup = existingTranslations
                .GroupBy(x => x.SourceKey + "|" + x.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

            // Guides use the reviewed built-in English catalog only. Fill missing guide
            // translations locally before the startup queue runs, but never overwrite a
            // non-empty existing value here. The admin one-click action performs the
            // controlled legacy/machine cleanup while preserving manual edits.
            foreach (var seed in guideSeeds)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (!SourceStillMatchesSeed(key, seed.Source))
                    continue;

                AddTranslation(db, existingSet, key, "en-US", seed.English, now);
                FillMissingReviewedTranslation(existingLookup, key, "en-US", seed.English, now);
            }

            foreach (var seed in reviewedSeeds)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (guideKeys.Contains(key) || !SourceStillMatchesSeed(key, seed.Source))
                    continue;

                AddTranslation(db, existingSet, key, "en-US", seed.English, now);
                RepairLeakedTurkish(existingLookup, key, "en-US", seed.English, now);
            }

            // Older captured packs shipped an awkward English target for the login-success
            // banner. Upgrade only that known built-in value (or weak/machine/Turkish rows),
            // while preserving any intentional reviewed+locked administrator edit.
            RepairKnownAccountStatusEnglish(existingLookup, now);

            // Provider placeholder cleanup remains language-agnostic. Retired-language repair
            // passes are intentionally not executed in the TR+EN public mode.
            await RepairProviderMarkupTranslationsAsync(db, now);

            // Public captured values from the old generated pack are retired only when the
            // DB still contains exactly that generated value. A later manual admin edit is
            // never destroyed. Sources that already have a curated reviewed seed are excluded.
            var reviewedKeys = guideSeeds
                .Concat(reviewedSeeds)
                .Select(x => LocalizationTextKey.Create(x.Source))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var seed in capturedSeeds.Where(x => !IsRestrictedLocalizationArea(x.Area)
                                                          && !reviewedKeys.Contains(LocalizationTextKey.Create(x.Source))))
                RetireLegacyCapturedTarget(existingLookup, seed, now);

            // The final reviewed pack contains deliberate human corrections and must also
            // repair installations that already received an older generated seed value.
            foreach (var seed in LocalizationFinalSeedData.Items)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (!guideKeys.Contains(key) && SourceStillMatchesSeed(key, seed.Source))
                    RepairReviewedOverride(existingLookup, key, "en-US", seed.English, now);
            }

            foreach (var seed in LocalizationEnglishOfflineUiSeedData.Items)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (!guideKeys.Contains(key) && SourceStillMatchesSeed(key, seed.Source))
                    RepairReviewedOverride(existingLookup, key, "en-US", seed.English, now);
            }

            // Product display names / SEO titles must stay in sync with the reviewed catalog
            // so older self-mapped Turkish EN rows (e.g. Veresiye) get upgraded on startup.
            foreach (var seed in ProductLocalizationSeedData.Items)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (!guideKeys.Contains(key) && SourceStillMatchesSeed(key, seed.Source))
                    RepairReviewedOverride(existingLookup, key, "en-US", seed.English, now);
            }

            // Support-channel pack also needs forced upgrade on existing installs.
            foreach (var seed in LocalizationEnglishExtendedSeedData.Items.Where(x =>
                         string.Equals(x.Area, "/Home/Contact", StringComparison.OrdinalIgnoreCase)))
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (!guideKeys.Contains(key) && SourceStillMatchesSeed(key, seed.Source))
                    RepairReviewedOverride(existingLookup, key, "en-US", seed.English, now);
            }

            // Clear weak public EN rows outside the Guide catalog. Guide rows are always
            // handled locally and must never be emptied into the remote translation queue.
            var weakPublicRows = await db.LocalizationTranslations
                .Where(x => x.LanguageCode == "en-US" && x.Value != "")
                .Join(
                    db.LocalizationResources.Where(x => !x.IsIgnored
                        && (x.FirstSeenPath == null
                            || (!x.FirstSeenPath.StartsWith("/Admin")
                                && !x.FirstSeenPath.StartsWith("/Account")
                                && !x.FirstSeenPath.StartsWith("/Dealer")
                                && !x.FirstSeenPath.StartsWith("/Bayi")
                                && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                && !x.FirstSeenPath.StartsWith("/Havale"))
                            || x.FirstSeenPath.StartsWith("/Public/"))),
                    translation => translation.SourceKey,
                    resource => resource.SourceKey,
                    (translation, resource) => translation)
                .ToListAsync();

            foreach (var row in weakPublicRows)
            {
                // Guide rows are handled exclusively by the local reviewed catalog.
                // Never clear them into the remote machine-translation queue.
                if (!guideKeys.Contains(row.SourceKey))
                    MarkWeakTargetForRetranslation(row, now);
            }

            await db.SaveChangesAsync();
        }

        private static async Task RepairBankTransferWarningTranslationsAsync(ApplicationDbContext db, DateTime now)
        {
            const string source = "Lütfen Havale/EFT yaparken açıklama kısmını boş bırakınız.";
            var sourceKey = LocalizationTextKey.Create(source);
            var normalizedSource = LocalizationTextKey.Normalize(source);

            var rows = await db.LocalizationTranslations
                .Where(x => x.SourceKey == sourceKey
                            && !x.LanguageCode.StartsWith("tr")
                            && x.Value != "")
                .ToListAsync();

            var changed = false;
            foreach (var row in rows)
            {
                if (row.IsReviewed && row.IsLocked)
                    continue;

                var normalizedTarget = LocalizationTextKey.Normalize(row.Value);
                if (!string.Equals(normalizedTarget, normalizedSource, StringComparison.Ordinal)
                    && !LocalizationMachineTranslationService.LooksLikeTurkishText(normalizedTarget))
                {
                    continue;
                }

                row.Value = string.Empty;
                row.IsMachineTranslated = false;
                row.IsReviewed = false;
                row.IsLocked = false;
                row.UpdatedAt = now;
                changed = true;
            }

            if (changed)
                await db.SaveChangesAsync();
        }

        private static async Task PromoteLegacyTurkishTranslationsAsync(ApplicationDbContext db, DateTime now)
        {
            var legacyRows = await db.LocalizationTranslations
                .Where(x => x.LanguageCode.StartsWith("tr"))
                .OrderByDescending(x => x.UpdatedAt)
                .ThenByDescending(x => x.Id)
                .ToListAsync();
            if (legacyRows.Count == 0)
                return;

            var keys = legacyRows
                .Select(x => x.SourceKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var resources = await db.LocalizationResources
                .Where(x => keys.Contains(x.SourceKey))
                .ToDictionaryAsync(x => x.SourceKey);

            var changedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in legacyRows.GroupBy(x => x.SourceKey, StringComparer.Ordinal))
            {
                if (!resources.TryGetValue(group.Key, out var resource))
                    continue;

                var latestValue = group
                    .Select(x => (x.Value ?? string.Empty).Trim())
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                if (string.IsNullOrWhiteSpace(latestValue)
                    || string.Equals(resource.SourceText, latestValue, StringComparison.Ordinal))
                {
                    continue;
                }

                resource.SourceText = latestValue;
                resource.LastSeenAt = now;
                changedKeys.Add(resource.SourceKey);
            }

            // Turkish is no longer a target language row under the new architecture.
            db.LocalizationTranslations.RemoveRange(legacyRows);

            if (changedKeys.Count > 0)
            {
                var changedArray = changedKeys.ToArray();
                var staleTargets = await db.LocalizationTranslations
                    .Where(x => changedArray.Contains(x.SourceKey)
                                && !x.LanguageCode.StartsWith("tr"))
                    .ToListAsync();
                foreach (var target in staleTargets)
                {
                    target.Value = string.Empty;
                    target.IsMachineTranslated = false;
                    target.IsReviewed = false;
                    target.IsLocked = false;
                    target.UpdatedAt = now;
                }

                var staleJobs = await db.LocalizationTranslationJobs
                    .Where(x => changedArray.Contains(x.SourceKey))
                    .ToListAsync();
                if (staleJobs.Count > 0)
                    db.LocalizationTranslationJobs.RemoveRange(staleJobs);
            }

            await db.SaveChangesAsync();
        }

        private static async Task RetireInvariantResourcesAsync(ApplicationDbContext db, DateTime now)
        {
            var resources = await db.LocalizationResources
                .Where(x => !x.IsIgnored)
                .ToListAsync();
            var invariantResources = resources
                .Where(x => LocalizationMachineTranslationService.IsInvariantLocalizationText(x.SourceText))
                .ToList();
            if (invariantResources.Count == 0)
                return;

            foreach (var resource in invariantResources)
                resource.IsIgnored = true;

            var invariantKeys = invariantResources
                .Select(x => x.SourceKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var unfinishedJobs = await db.LocalizationTranslationJobs
                .Where(x => invariantKeys.Contains(x.SourceKey)
                            && (x.Status == "Pending" || x.Status == "Processing"))
                .ToListAsync();

            foreach (var job in unfinishedJobs)
            {
                job.Status = "Failed";
                job.StartedAt = null;
                job.CompletedAt = null;
                job.NextAttemptAt = null;
                job.LastError = "Dil bağımsız bağlantı/e-posta/teknik değer; çeviri kuyruğundan güvenle çıkarıldı.";
                job.UpdatedAt = now;
            }

            await db.SaveChangesAsync();
        }


        private static async Task RepairUnchangedTurkishTargetRowsAsync(ApplicationDbContext db, DateTime now)
        {
            var rows = await db.LocalizationTranslations
                .Where(x => !x.LanguageCode.StartsWith("tr")
                            && x.Value != ""
                            && (!x.IsReviewed || !x.IsLocked))
                .ToListAsync();
            if (rows.Count == 0)
                return;

            var keys = rows.Select(x => x.SourceKey).Distinct(StringComparer.Ordinal).ToArray();
            var sources = await db.LocalizationResources.AsNoTracking()
                .Where(x => keys.Contains(x.SourceKey) && !x.IsIgnored)
                .Select(x => new { x.SourceKey, x.SourceText })
                .ToDictionaryAsync(x => x.SourceKey, x => x.SourceText);

            var changed = false;
            foreach (var row in rows)
            {
                if (row.IsReviewed && row.IsLocked)
                    continue;
                if (!sources.TryGetValue(row.SourceKey, out var sourceText))
                    continue;

                var source = LocalizationTextKey.Normalize(sourceText);
                var target = LocalizationTextKey.Normalize(row.Value);
                var exactTurkishCopy = string.Equals(source, target, StringComparison.Ordinal)
                    && LocalizationMachineTranslationService.LooksLikeTurkishText(source)
                    && !IsLikelyNsxBrandName(source);
                var obviousTurkishLeak = LocalizationMachineTranslationService.LooksLikeUntranslatedTurkish(target)
                    && !IsLikelyNsxBrandName(target);
                if (!exactTurkishCopy && !obviousTurkishLeak)
                    continue;

                row.Value = string.Empty;
                row.IsMachineTranslated = false;
                row.IsReviewed = false;
                row.IsLocked = false;
                row.UpdatedAt = now;
                changed = true;
            }

            if (changed)
                await db.SaveChangesAsync();
        }

        private static bool IsLikelyNsxBrandName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var value = LocalizationTextKey.Normalize(text);
            if (!value.StartsWith("NSX ", StringComparison.OrdinalIgnoreCase)
                || value.Length > 80
                || value.IndexOfAny(new[] { '.', '!', '?', ':', ';', '|' }) >= 0)
            {
                return false;
            }

            return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6;
        }

        private static async Task RepairProviderMarkupTranslationsAsync(ApplicationDbContext db, DateTime now)
        {
            var rows = await db.LocalizationTranslations
                .Where(x => x.Value.Contains("<g")
                            || x.Value.Contains("</g>")
                            || x.Value.Contains("&lt;g")
                            || x.Value.Contains("&lt;/g"))
                .ToListAsync();

            foreach (var row in rows)
            {
                var normalized = LocalizationDisplayNormalizer.Normalize(row.Value, row.LanguageCode);
                if (string.Equals(normalized, row.Value, StringComparison.Ordinal))
                    continue;

                row.Value = normalized;
                row.UpdatedAt = now;
            }

            if (rows.Count > 0)
                await db.SaveChangesAsync();
        }

        private static async Task RepairFrenchUiTranslationsAsync(ApplicationDbContext db, DateTime now)
        {
            var frenchCodes = await db.SiteLanguages
                .AsNoTracking()
                .Where(x => x.Code.StartsWith("fr"))
                .Select(x => x.Code)
                .ToListAsync();
            if (frenchCodes.Count == 0)
                return;

            var seeds = LocalizationFrenchRepairSeedData.Items;
            var seedKeys = seeds
                .Select(x => LocalizationTextKey.Create(x.Source))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var existingResources = await db.LocalizationResources
                .Where(x => seedKeys.Contains(x.SourceKey))
                .ToListAsync();
            var resourceByKey = existingResources
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

            foreach (var seed in seeds)
            {
                var key = LocalizationTextKey.Create(seed.Source);
                if (resourceByKey.ContainsKey(key))
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
                db.LocalizationResources.Add(resource);
                resourceByKey[key] = resource;
            }

            await db.SaveChangesAsync();

            var existingTranslations = await db.LocalizationTranslations
                .Where(x => seedKeys.Contains(x.SourceKey) && frenchCodes.Contains(x.LanguageCode))
                .ToListAsync();
            var translationByKey = existingTranslations
                .GroupBy(x => x.SourceKey + "|" + x.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (var languageCode in frenchCodes)
            {
                foreach (var seed in seeds)
                {
                    var sourceKey = LocalizationTextKey.Create(seed.Source);
                    var lookupKey = sourceKey + "|" + languageCode;
                    var value = LocalizationDisplayNormalizer.Normalize(seed.French, languageCode);

                    if (!translationByKey.TryGetValue(lookupKey, out var row))
                    {
                        row = new LocalizationTranslation
                        {
                            SourceKey = sourceKey,
                            LanguageCode = languageCode,
                            Value = value,
                            IsMachineTranslated = false,
                            IsReviewed = true,
                            IsLocked = true,
                            UpdatedAt = now
                        };
                        db.LocalizationTranslations.Add(row);
                        translationByKey[lookupKey] = row;
                    }
                    else
                    {
                        row.Value = value;
                        row.IsMachineTranslated = false;
                        row.IsReviewed = true;
                        row.IsLocked = true;
                        row.UpdatedAt = now;
                    }
                }
            }

            await db.SaveChangesAsync();
        }

        private static bool IsRestrictedLocalizationArea(string? area)
        {
            if (string.IsNullOrWhiteSpace(area))
                return false;

            return area.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase)
                || area.StartsWith("/Account", StringComparison.OrdinalIgnoreCase)
                || area.StartsWith("/Dealer", StringComparison.OrdinalIgnoreCase)
                || area.StartsWith("/Bayi", StringComparison.OrdinalIgnoreCase)
                || area.StartsWith("/BankTransfer", StringComparison.OrdinalIgnoreCase)
                || area.StartsWith("/Havale", StringComparison.OrdinalIgnoreCase);
        }

        private static void RetireLegacyCapturedTarget(
            IReadOnlyDictionary<string, LocalizationTranslation> existingLookup,
            LocalizationSeedItem seed,
            DateTime now)
        {
            var key = LocalizationTextKey.Create(seed.Source);
            if (!existingLookup.TryGetValue(key + "|en-US", out var row))
                return;

            var oldGeneratedValue = LocalizationDisplayNormalizer.Normalize(seed.English?.Trim(), "en-US");
            if (!string.Equals(row.Value?.Trim(), oldGeneratedValue, StringComparison.Ordinal))
                return;

            row.Value = string.Empty;
            row.IsMachineTranslated = false;
            row.IsReviewed = false;
            row.IsLocked = false;
            row.UpdatedAt = now;
        }

        private static void AddTranslation(ApplicationDbContext db, HashSet<string> existingSet, string sourceKey, string languageCode, string value, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (!existingSet.Add(sourceKey + "|" + languageCode))
                return;

            db.LocalizationTranslations.Add(new LocalizationTranslation
            {
                SourceKey = sourceKey,
                LanguageCode = languageCode,
                Value = LocalizationDisplayNormalizer.Normalize(value.Trim(), languageCode),
                IsMachineTranslated = false,
                IsReviewed = true,
                IsLocked = true,
                UpdatedAt = now
            });
        }

        private static void FillMissingReviewedTranslation(
            IReadOnlyDictionary<string, LocalizationTranslation> existingLookup,
            string sourceKey,
            string languageCode,
            string reviewedValue,
            DateTime now)
        {
            if (string.IsNullOrWhiteSpace(reviewedValue)
                || !existingLookup.TryGetValue(sourceKey + "|" + languageCode, out var row)
                || !string.IsNullOrWhiteSpace(row.Value))
                return;

            row.Value = LocalizationDisplayNormalizer.Normalize(reviewedValue.Trim(), languageCode);
            row.IsMachineTranslated = false;
            row.IsReviewed = true;
            row.IsLocked = true;
            row.UpdatedAt = now;
        }

        private static void RepairKnownAccountStatusEnglish(
            IReadOnlyDictionary<string, LocalizationTranslation> existingLookup,
            DateTime now)
        {
            const string source = "Giriş başarılı. Hesabına hoş geldin.";
            const string reviewed = "Signed in successfully. Welcome to your account.";
            const string legacy = "Introduction is successful. Welcome to the account.";

            var key = LocalizationTextKey.Create(source);
            if (!existingLookup.TryGetValue(key + "|en-US", out var row))
                return;

            var current = LocalizationDisplayNormalizer.Normalize(row.Value?.Trim(), "en-US");
            var target = LocalizationDisplayNormalizer.Normalize(reviewed, "en-US");
            if (string.Equals(current, target, StringComparison.Ordinal))
                return;

            var isKnownLegacy = string.Equals(
                current,
                LocalizationDisplayNormalizer.Normalize(legacy, "en-US"),
                StringComparison.Ordinal);

            if (!isKnownLegacy
                && row.IsReviewed
                && row.IsLocked
                && !row.IsMachineTranslated
                && !ContainsLeakedTurkish(row.Value, "en-US"))
            {
                return;
            }

            row.Value = target;
            row.IsMachineTranslated = false;
            row.IsReviewed = true;
            row.IsLocked = true;
            row.UpdatedAt = now;
        }

        private static void RepairLeakedTurkish(
            IReadOnlyDictionary<string, LocalizationTranslation> existingLookup,
            string sourceKey,
            string languageCode,
            string reviewedValue,
            DateTime now)
        {
            if (string.IsNullOrWhiteSpace(reviewedValue)
                || !existingLookup.TryGetValue(sourceKey + "|" + languageCode, out var row)
                || !ContainsLeakedTurkish(row.Value, languageCode)
                || string.Equals(row.Value.Trim(), reviewedValue.Trim(), StringComparison.Ordinal))
                return;

            row.Value = LocalizationDisplayNormalizer.Normalize(reviewedValue.Trim(), languageCode);
            row.IsMachineTranslated = false;
            row.IsReviewed = true;
            row.IsLocked = true;
            row.UpdatedAt = now;
        }

        private static bool ContainsLeakedTurkish(string? value, string languageCode)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // Turkish names are intentionally preserved in copyright/author labels.
            var inspected = value
                .Replace("Nevzat SÜRÜCÜ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Aynur SÜRÜCÜ", string.Empty, StringComparison.OrdinalIgnoreCase);

            return LocalizationMachineTranslationService.LooksLikeTurkishText(inspected);
        }

        private static void RepairReviewedOverride(
            IReadOnlyDictionary<string, LocalizationTranslation> existingLookup,
            string sourceKey,
            string languageCode,
            string reviewedValue,
            DateTime now)
        {
            if (string.IsNullOrWhiteSpace(reviewedValue)
                || !existingLookup.TryGetValue(sourceKey + "|" + languageCode, out var row))
                return;

            var normalized = LocalizationDisplayNormalizer.Normalize(reviewedValue.Trim(), languageCode);
            if (string.Equals(row.Value.Trim(), normalized, StringComparison.Ordinal))
                return;

            row.Value = normalized;
            row.IsMachineTranslated = false;
            row.IsReviewed = true;
            row.IsLocked = true;
            row.UpdatedAt = now;
        }

        private static void MarkWeakTargetForRetranslation(LocalizationTranslation row, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(row.Value)
                || !row.LanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                || (row.IsReviewed && row.IsLocked))
                return;

            if (!LocalizationMachineTranslationService.LooksLikeTurkishText(row.Value))
                return;

            row.Value = string.Empty;
            row.IsMachineTranslated = false;
            row.IsReviewed = false;
            row.IsLocked = false;
            row.UpdatedAt = now;
        }

    }
}
