using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Controllers;

namespace NSYazilim.Web.Services
{
    public sealed record LocalizationQueueStats(
        int Pending,
        int Processing,
        int Completed,
        int Failed,
        int Total,
        DateTime? LastActivityAt,
        DateTime? LastCompletedAt,
        DateTime? NextAttemptAt,
        string? LastError)
    {
        public decimal ProgressPercent => Total <= 0
            ? 100m
            : Math.Round(Completed * 100m / Total, 1);
    }

    public sealed record LocalizationSourceUpdateResult(
        bool Changed,
        int InvalidatedTranslations,
        int QueuedJobs,
        bool MachineTranslationAllowed);

    /// <summary>
    /// NSX Localization Engine queue coordinator.
    /// New content is registered once and queued for every active target language.
    /// Adding a new language can backfill every known resource without an item limit.
    /// </summary>
    public sealed class LocalizationTranslationQueueService
    {
        private static readonly IReadOnlyDictionary<string, string> EnglishGuideSources = LocalizationEnglishGuideSeedData.Items
            .GroupBy(x => LocalizationTextKey.Create(x.Source), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => LocalizationTextKey.Normalize(x.First().Source), StringComparer.Ordinal);

        private readonly ApplicationDbContext _db;
        private readonly LocalizationCatalog _catalog;
        private readonly LocalizationTranslationQueueSignal _signal;
        private readonly ILogger<LocalizationTranslationQueueService> _logger;

        public LocalizationTranslationQueueService(
            ApplicationDbContext db,
            LocalizationCatalog catalog,
            LocalizationTranslationQueueSignal signal,
            ILogger<LocalizationTranslationQueueService> logger)
        {
            _db = db;
            _catalog = catalog;
            _signal = signal;
            _logger = logger;
        }

        public async Task<int> QueueTextsForActiveLanguagesAsync(
            IEnumerable<string?> sourceTexts,
            string? sourcePath,
            CancellationToken cancellationToken = default)
        {
            var normalized = NormalizeTexts(sourceTexts);
            if (normalized.Count == 0)
                return 0;

            var now = DateTime.Now;
            var byKey = normalized.ToDictionary(LocalizationTextKey.Create, x => x, StringComparer.Ordinal);
            var keys = byKey.Keys.ToArray();
            var existingResources = await _db.LocalizationResources
                .Where(x => keys.Contains(x.SourceKey))
                .ToDictionaryAsync(x => x.SourceKey, cancellationToken);

            var safePath = NormalizePath(sourcePath);
            foreach (var pair in byKey)
            {
                if (existingResources.TryGetValue(pair.Key, out var row))
                {
                    row.LastSeenAt = now;
                    row.HitCount = Math.Max(1, row.HitCount + 1);
                    if (safePath.StartsWith("/Public/", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(row.FirstSeenPath, safePath, StringComparison.OrdinalIgnoreCase))
                    {
                        row.FirstSeenPath = safePath;
                    }
                    continue;
                }

                _db.LocalizationResources.Add(new LocalizationResource
                {
                    SourceKey = pair.Key,
                    SourceText = pair.Value,
                    FirstSeenPath = safePath,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    HitCount = 1
                });
            }

            await _db.SaveChangesAsync(cancellationToken);

            var languages = await GetActivePublicTargetLanguageCodesAsync(cancellationToken);

            var queued = 0;
            foreach (var languageCode in languages)
                queued += await QueueKeysForLanguageAsync(keys, languageCode, cancellationToken);

            if (queued > 0)
                _signal.Pulse();

            return queued;
        }


        public async Task<int> QueueExistingKeysForActiveLanguagesAsync(
            IEnumerable<string> sourceKeys,
            CancellationToken cancellationToken = default)
        {
            var keys = sourceKeys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (keys.Length == 0)
                return 0;

            var languages = await GetActivePublicTargetLanguageCodesAsync(cancellationToken);

            var total = 0;
            foreach (var languageCode in languages)
                total += await QueueKeysForLanguageAsync(keys, languageCode, cancellationToken);

            if (total > 0)
                _signal.Pulse();

            return total;
        }

        /// <summary>
        /// Updates the canonical Turkish source text without changing SourceKey. Every old
        /// target-language value is invalid after a source revision, so target translations
        /// are cleared and active safe languages are queued again from the new source text.
        /// Restricted Account/Dealer/BankTransfer/Admin data never enters machine translation.
        /// </summary>
        public async Task<LocalizationSourceUpdateResult> UpdateSourceTextAsync(
            string sourceKey,
            string sourceText,
            CancellationToken cancellationToken = default)
        {
            sourceKey = (sourceKey ?? string.Empty).Trim();
            sourceText = (sourceText ?? string.Empty).Trim();
            if (sourceKey.Length == 0 || sourceText.Length == 0)
                return new LocalizationSourceUpdateResult(false, 0, 0, false);

            var resource = await _db.LocalizationResources
                .FirstOrDefaultAsync(x => x.SourceKey == sourceKey, cancellationToken);
            if (resource == null)
                return new LocalizationSourceUpdateResult(false, 0, 0, false);

            if (string.Equals(resource.SourceText, sourceText, StringComparison.Ordinal))
                return new LocalizationSourceUpdateResult(false, 0, 0, IsMachineTranslationEligible(resource));

            var targetLanguages = await GetActivePublicTargetLanguageCodesAsync(cancellationToken);
            var translations = await _db.LocalizationTranslations
                .Where(x => x.SourceKey == sourceKey)
                .ToListAsync(cancellationToken);
            var invalidated = 0;
            foreach (var translation in translations)
            {
                if (translation.LanguageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                {
                    // Turkish is no longer a target translation row. The resource itself is
                    // the source-of-truth, so retire any legacy tr-TR target row for this key.
                    _db.LocalizationTranslations.Remove(translation);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(translation.Value))
                    invalidated++;

                translation.Value = string.Empty;
                translation.IsMachineTranslated = false;
                translation.IsReviewed = false;
                translation.IsLocked = false;
                translation.UpdatedAt = DateTime.Now;
            }

            // Remove durable jobs for this source. Fresh jobs are created only for active,
            // machine-safe target languages below. This also prevents inactive languages or
            // restricted paths from being processed by the background worker accidentally.
            var jobs = await _db.LocalizationTranslationJobs
                .Where(x => x.SourceKey == sourceKey)
                .ToListAsync(cancellationToken);
            if (jobs.Count > 0)
                _db.LocalizationTranslationJobs.RemoveRange(jobs);

            resource.SourceText = sourceText;
            resource.LastSeenAt = DateTime.Now;
            await _db.SaveChangesAsync(cancellationToken);

            _catalog.InvalidateSources();
            foreach (var languageCode in targetLanguages)
                _catalog.InvalidateTranslations(languageCode);

            var machineTranslationAllowed = IsMachineTranslationEligible(resource)
                && !LocalizationMachineTranslationService.IsInvariantLocalizationText(sourceText);
            var queued = machineTranslationAllowed
                ? await QueueExistingKeysForActiveLanguagesAsync(new[] { sourceKey }, cancellationToken)
                : 0;

            return new LocalizationSourceUpdateResult(true, invalidated, queued, machineTranslationAllowed);
        }

        public async Task<int> IndexKnownDynamicContentAsync(CancellationToken cancellationToken = default)
        {
            var texts = new List<string?>();

            // Full-description rows belong to the manual whole-text editor. Runtime automatic
            // translation works on semantic paragraph/heading/list keys so HTML structure is
            // preserved. Retire historical auto jobs for those manual rows to prevent useless
            // retries from consuming worker turns. Manual translations themselves are untouched.
            try
            {
                var manualFullKeys = await _db.LocalizationResources.AsNoTracking()
                    .Where(x => x.FirstSeenPath != null && x.FirstSeenPath.EndsWith("/DescriptionFull"))
                    .Select(x => x.SourceKey)
                    .ToListAsync(cancellationToken);
                if (manualFullKeys.Count > 0)
                {
                    await _db.LocalizationTranslationJobs
                        .Where(x => manualFullKeys.Contains(x.SourceKey))
                        .ExecuteDeleteAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tam ürün açıklaması manuel kayıtlarının eski otomatik işleri temizlenemedi.");
            }

            try
            {
                var products = await _db.Products.AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .Select(x => new { x.Name, x.Description, x.MetaTitle, x.MetaDescription })
                    .ToListAsync(cancellationToken);
                foreach (var product in products)
                {
                    texts.Add(product.Name);
                    texts.Add(product.MetaTitle);
                    texts.Add(product.MetaDescription);
                    texts.AddRange(ExtractHtmlTextSegments(product.Description));
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Ürün içerikleri dil envanterine alınamadı."); }

            try
            {
                texts.AddRange(await _db.ProductCategories.AsNoTracking()
                    .Select(x => x.Label)
                    .ToListAsync(cancellationToken));
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Kategori içerikleri dil envanterine alınamadı."); }

            try
            {
                var videos = await _db.ProductVideos.AsNoTracking()
                    .Select(x => new { x.Title, x.VideoType })
                    .ToListAsync(cancellationToken);
                foreach (var video in videos)
                {
                    texts.Add(video.Title);
                    texts.Add(video.VideoType);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Video içerikleri dil envanterine alınamadı."); }

            try
            {
                var ads = await _db.Advertisements.AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .Select(x => new { x.Title, x.Description, x.AltText })
                    .ToListAsync(cancellationToken);
                foreach (var ad in ads)
                {
                    texts.Add(ad.Title);
                    texts.Add(ad.Description);
                    texts.Add(ad.AltText);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Reklam içerikleri dil envanterine alınamadı."); }

            try
            {
                var campaigns = await _db.Set<Campaign>().AsNoTracking()
                    .Select(x => new { x.Name, x.Description })
                    .ToListAsync(cancellationToken);
                foreach (var campaign in campaigns)
                {
                    texts.Add(campaign.Name);
                    texts.Add(campaign.Description);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Kampanya içerikleri dil envanterine alınamadı."); }

            try
            {
                texts.AddRange(await _db.CampaignCoupons.AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .Select(x => x.Title)
                    .ToListAsync(cancellationToken));
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Kupon içerikleri dil envanterine alınamadı."); }

            try
            {
                var sliders = await _db.Set<HomeSlider>().AsNoTracking()
                    .Select(x => new { x.Title, x.Subtitle, x.ButtonText })
                    .ToListAsync(cancellationToken);
                foreach (var slider in sliders)
                {
                    texts.Add(slider.Title);
                    texts.Add(slider.Subtitle);
                    texts.Add(slider.ButtonText);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Slider içerikleri dil envanterine alınamadı; tablo bu kurulumda olmayabilir."); }

            // Rehber/blog içerikleri DB'de değil BlogController.Pages içindeki statik SEO
            // kataloğunda tutuluyor. Önceki motor bunları yalnız sayfa ziyaret edilirse
            // görüyordu; artık uygulama açılışında tamamı envantere alınır.
            foreach (var page in BlogController.Pages)
            {
                texts.Add(page.Title);
                texts.Add(page.Description);
                texts.Add(page.H1);
                texts.Add(page.Body);
                texts.Add(page.CtaText);
                texts.AddRange(page.Keywords ?? Array.Empty<string>());
                if (page.Sections != null)
                {
                    foreach (var section in page.Sections)
                    {
                        texts.Add(section.Heading);
                        texts.Add(section.Content);
                    }
                }
                if (page.Faqs != null)
                {
                    foreach (var faq in page.Faqs)
                    {
                        texts.Add(faq.Question);
                        texts.Add(faq.Answer);
                    }
                }
            }

            return await QueueTextsForActiveLanguagesAsync(texts, "/Public/DynamicContent", cancellationToken);
        }

        public async Task<int> QueueProductAsync(Product product, CancellationToken cancellationToken = default)
        {
            var texts = new List<string?>
            {
                product.Name,
                product.MetaTitle,
                product.MetaDescription
            };
            texts.AddRange(ExtractHtmlTextSegments(product.Description));
            return await QueueTextsForActiveLanguagesAsync(texts, $"/Public/Product/{product.Id}", cancellationToken);
        }

        public Task<int> QueueCategoryAsync(ProductCategory category, CancellationToken cancellationToken = default)
            => QueueTextsForActiveLanguagesAsync(new[] { category.Label }, $"/Public/ProductCategory/{category.Id}", cancellationToken);

        public Task<int> QueueVideoAsync(ProductVideo video, CancellationToken cancellationToken = default)
            => QueueTextsForActiveLanguagesAsync(new[] { video.Title, video.VideoType }, $"/Public/Video/{video.Id}", cancellationToken);

        public Task<int> QueueAdvertisementAsync(Advertisement advertisement, CancellationToken cancellationToken = default)
            => QueueTextsForActiveLanguagesAsync(new[] { advertisement.Title, advertisement.Description, advertisement.AltText }, $"/Public/Advertisement/{advertisement.Id}", cancellationToken);

        public Task<int> QueueCampaignAsync(Campaign campaign, CancellationToken cancellationToken = default)
            => QueueTextsForActiveLanguagesAsync(new[] { campaign.Name, campaign.Description }, $"/Public/Campaign/{campaign.Id}", cancellationToken);

        public Task<int> QueueCampaignCouponAsync(CampaignCoupon coupon, CancellationToken cancellationToken = default)
            => QueueTextsForActiveLanguagesAsync(new[] { coupon.Title }, $"/Public/CampaignCoupon/{coupon.Id}", cancellationToken);

        public Task<int> QueueHomeSliderAsync(HomeSlider slider, CancellationToken cancellationToken = default)
            => QueueTextsForActiveLanguagesAsync(new[] { slider.Title, slider.Subtitle, slider.ButtonText }, $"/Public/HomeSlider/{slider.Id}", cancellationToken);

        public async Task<int> QueueAllMissingForLanguageAsync(
            string languageCode,
            CancellationToken cancellationToken = default)
        {
            languageCode = (languageCode ?? string.Empty).Trim();
            if (!PublicLanguagePolicy.IsPublicTargetLanguageCode(languageCode))
                return 0;

            var activeTargets = await GetActivePublicTargetLanguageCodesAsync(cancellationToken);
            if (!activeTargets.Contains(languageCode, StringComparer.OrdinalIgnoreCase))
                return 0;

            var keys = await _db.LocalizationResources.AsNoTracking()
                .Where(x => !x.IsIgnored
                            && (x.FirstSeenPath == null || !x.FirstSeenPath.EndsWith("/DescriptionFull"))
                            && (x.FirstSeenPath == null
                                || (!x.FirstSeenPath.StartsWith("/Admin")
                                    && !x.FirstSeenPath.StartsWith("/Account")
                                    && !x.FirstSeenPath.StartsWith("/Dealer")
                                    && !x.FirstSeenPath.StartsWith("/Bayi")
                                    && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                    && !x.FirstSeenPath.StartsWith("/Havale"))
                                || x.FirstSeenPath.StartsWith("/Public/")))
                .OrderByDescending(x => x.HitCount)
                .ThenBy(x => x.Id)
                .Select(x => x.SourceKey)
                .ToListAsync(cancellationToken);

            return await QueueKeysForLanguageAsync(keys, languageCode, cancellationToken, requeueFailed: true);
        }

        public async Task<int> QueueAllMissingForActiveLanguagesAsync(CancellationToken cancellationToken = default)
        {
            var languages = await GetActivePublicTargetLanguageCodesAsync(cancellationToken);

            var total = 0;
            foreach (var languageCode in languages)
                total += await QueueAllMissingForLanguageAsync(languageCode, cancellationToken);

            if (total > 0)
                _signal.Pulse();

            return total;
        }

        public async Task<LocalizationQueueStats> GetStatsAsync(
            string languageCode,
            CancellationToken cancellationToken = default)
        {
            var eligibleResources = _db.LocalizationResources.AsNoTracking()
                .Where(x => !x.IsIgnored
                            && (x.FirstSeenPath == null || !x.FirstSeenPath.EndsWith("/DescriptionFull"))
                            && (x.FirstSeenPath == null
                                || (!x.FirstSeenPath.StartsWith("/Admin")
                                    && !x.FirstSeenPath.StartsWith("/Account")
                                    && !x.FirstSeenPath.StartsWith("/Dealer")
                                    && !x.FirstSeenPath.StartsWith("/Bayi")
                                    && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                    && !x.FirstSeenPath.StartsWith("/Havale"))
                                || x.FirstSeenPath.StartsWith("/Public/")));
            var resourceKeysQuery = eligibleResources.Select(x => x.SourceKey);

            if (!string.IsNullOrWhiteSpace(languageCode)
                && languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
            {
                var sourceCount = await resourceKeysQuery.CountAsync(cancellationToken);
                return new LocalizationQueueStats(
                    Pending: 0,
                    Processing: 0,
                    Completed: sourceCount,
                    Failed: 0,
                    Total: sourceCount,
                    LastActivityAt: null,
                    LastCompletedAt: null,
                    NextAttemptAt: null,
                    LastError: null);
            }

            var totalResources = await resourceKeysQuery.CountAsync(cancellationToken);
            var completedKeys = (await _db.LocalizationTranslations.AsNoTracking()
                    .Where(x => x.LanguageCode == languageCode && x.Value != "" && resourceKeysQuery.Contains(x.SourceKey))
                    .Select(x => x.SourceKey)
                    .Distinct()
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

            if (languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                var sourceRows = await eligibleResources
                    .Select(x => new { x.SourceKey, x.SourceText })
                    .ToListAsync(cancellationToken);

                foreach (var resource in sourceRows)
                {
                    if (!completedKeys.Contains(resource.SourceKey)
                        && !LocalizationMachineTranslationService.LooksLikeTurkishText(resource.SourceText))
                    {
                        completedKeys.Add(resource.SourceKey);
                    }
                }
            }

            var jobs = await _db.LocalizationTranslationJobs.AsNoTracking()
                .Where(x => x.LanguageCode == languageCode && resourceKeysQuery.Contains(x.SourceKey))
                .Select(x => new
                {
                    x.SourceKey,
                    x.Status,
                    x.UpdatedAt,
                    x.CompletedAt,
                    x.NextAttemptAt,
                    x.LastError
                })
                .ToListAsync(cancellationToken);

            int Count(string status) => jobs.Count(x =>
                !completedKeys.Contains(x.SourceKey)
                && string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase));

            var activeJobs = jobs.Where(x => !completedKeys.Contains(x.SourceKey)).ToList();
            var lastErrorRow = activeJobs
                .Where(x => !string.IsNullOrWhiteSpace(x.LastError))
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();

            return new LocalizationQueueStats(
                Count("Pending"),
                Count("Processing"),
                completedKeys.Count,
                Count("Failed"),
                totalResources,
                jobs.Count == 0 ? null : jobs.Max(x => (DateTime?)x.UpdatedAt),
                jobs.Where(x => x.CompletedAt.HasValue).Max(x => x.CompletedAt),
                activeJobs.Where(x => x.NextAttemptAt.HasValue).Min(x => x.NextAttemptAt),
                lastErrorRow?.LastError);
        }

        public async Task<IReadOnlyDictionary<string, LocalizationQueueStats>> GetStatsForLanguagesAsync(
            IEnumerable<string> languageCodes,
            CancellationToken cancellationToken = default)
        {
            var codes = languageCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (codes.Length == 0)
                return new Dictionary<string, LocalizationQueueStats>(StringComparer.OrdinalIgnoreCase);

            // Dil Motoru ana ekranı eskiden HER DİL için GetStatsAsync çağırıyor ve aynı
            // LocalizationResources/Translations/Jobs tablolarını tekrar tekrar okuyordu.
            // Kaynaklar bir kez, hedef çeviriler bir kez ve tüm dil işleri bir kez okunur.
            var eligibleResources = await _db.LocalizationResources.AsNoTracking()
                .Where(x => !x.IsIgnored
                            && (x.FirstSeenPath == null || !x.FirstSeenPath.EndsWith("/DescriptionFull"))
                            && (x.FirstSeenPath == null
                                || (!x.FirstSeenPath.StartsWith("/Admin")
                                    && !x.FirstSeenPath.StartsWith("/Account")
                                    && !x.FirstSeenPath.StartsWith("/Dealer")
                                    && !x.FirstSeenPath.StartsWith("/Bayi")
                                    && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                    && !x.FirstSeenPath.StartsWith("/Havale"))
                                || x.FirstSeenPath.StartsWith("/Public/")))
                .Select(x => new { x.SourceKey, x.SourceText })
                .ToListAsync(cancellationToken);

            var totalResources = eligibleResources.Count;
            var targetCodes = codes
                .Where(x => !x.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var completedByLanguage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in targetCodes)
                completedByLanguage[code] = new HashSet<string>(StringComparer.Ordinal);

            if (targetCodes.Length > 0)
            {
                var translationRows = await (
                    from translation in _db.LocalizationTranslations.AsNoTracking()
                    join resource in _db.LocalizationResources.AsNoTracking()
                        on translation.SourceKey equals resource.SourceKey
                    where targetCodes.Contains(translation.LanguageCode)
                          && translation.Value != ""
                          && !resource.IsIgnored
                          && (resource.FirstSeenPath == null || !resource.FirstSeenPath.EndsWith("/DescriptionFull"))
                          && (resource.FirstSeenPath == null
                              || (!resource.FirstSeenPath.StartsWith("/Admin")
                                  && !resource.FirstSeenPath.StartsWith("/Account")
                                  && !resource.FirstSeenPath.StartsWith("/Dealer")
                                  && !resource.FirstSeenPath.StartsWith("/Bayi")
                                  && !resource.FirstSeenPath.StartsWith("/BankTransfer")
                                  && !resource.FirstSeenPath.StartsWith("/Havale"))
                              || resource.FirstSeenPath.StartsWith("/Public/"))
                    select new { translation.LanguageCode, translation.SourceKey })
                    .ToListAsync(cancellationToken);

                foreach (var row in translationRows)
                {
                    if (completedByLanguage.TryGetValue(row.LanguageCode, out var set))
                        set.Add(row.SourceKey);
                }

                // English source text that is already non-Turkish is intentionally pass-through
                // and counts as complete, matching the single-language GetStatsAsync behavior.
                foreach (var code in targetCodes.Where(x => x.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
                {
                    var set = completedByLanguage[code];
                    foreach (var resource in eligibleResources)
                    {
                        if (!set.Contains(resource.SourceKey)
                            && !LocalizationMachineTranslationService.LooksLikeTurkishText(resource.SourceText))
                        {
                            set.Add(resource.SourceKey);
                        }
                    }
                }
            }

            var allJobRows = new List<LocalizationStatsJobRow>();
            if (targetCodes.Length > 0)
            {
                var rawJobs = await (
                    from job in _db.LocalizationTranslationJobs.AsNoTracking()
                    join resource in _db.LocalizationResources.AsNoTracking()
                        on job.SourceKey equals resource.SourceKey
                    where targetCodes.Contains(job.LanguageCode)
                          && !resource.IsIgnored
                          && (resource.FirstSeenPath == null || !resource.FirstSeenPath.EndsWith("/DescriptionFull"))
                          && (resource.FirstSeenPath == null
                              || (!resource.FirstSeenPath.StartsWith("/Admin")
                                  && !resource.FirstSeenPath.StartsWith("/Account")
                                  && !resource.FirstSeenPath.StartsWith("/Dealer")
                                  && !resource.FirstSeenPath.StartsWith("/Bayi")
                                  && !resource.FirstSeenPath.StartsWith("/BankTransfer")
                                  && !resource.FirstSeenPath.StartsWith("/Havale"))
                              || resource.FirstSeenPath.StartsWith("/Public/"))
                    select new
                    {
                        job.LanguageCode,
                        job.SourceKey,
                        job.Status,
                        job.UpdatedAt,
                        job.CompletedAt,
                        job.NextAttemptAt,
                        job.LastError
                    })
                    .ToListAsync(cancellationToken);

                allJobRows = rawJobs.Select(x => new LocalizationStatsJobRow(
                    x.LanguageCode,
                    x.SourceKey,
                    x.Status,
                    x.UpdatedAt,
                    x.CompletedAt,
                    x.NextAttemptAt,
                    x.LastError)).ToList();
            }

            var allJobsByLanguage = allJobRows
                .GroupBy(x => x.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, LocalizationQueueStats>(StringComparer.OrdinalIgnoreCase);

            foreach (var code in codes)
            {
                if (code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                {
                    result[code] = new LocalizationQueueStats(
                        0, 0, totalResources, 0, totalResources, null, null, null, null);
                    continue;
                }

                var completed = completedByLanguage.TryGetValue(code, out var completedKeys)
                    ? completedKeys
                    : new HashSet<string>(StringComparer.Ordinal);
                var languageJobs = allJobsByLanguage.TryGetValue(code, out var rows)
                    ? rows
                    : new List<LocalizationStatsJobRow>();
                var activeJobs = languageJobs
                    .Where(x => !string.Equals(x.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                                && !completed.Contains(x.SourceKey))
                    .ToList();

                var lastErrorRow = activeJobs
                    .Where(x => !string.IsNullOrWhiteSpace(x.LastError))
                    .OrderByDescending(x => x.UpdatedAt)
                    .FirstOrDefault();
                var nextAttemptAt = activeJobs
                    .Where(x => x.NextAttemptAt.HasValue)
                    .Select(x => x.NextAttemptAt)
                    .DefaultIfEmpty(null)
                    .Min();
                var lastActivityAt = languageJobs.Count == 0
                    ? (DateTime?)null
                    : languageJobs.Max(x => (DateTime?)x.UpdatedAt);
                var lastCompletedAt = languageJobs
                    .Where(x => x.CompletedAt.HasValue)
                    .Select(x => x.CompletedAt)
                    .DefaultIfEmpty(null)
                    .Max();

                result[code] = new LocalizationQueueStats(
                    activeJobs.Count(x => string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
                    activeJobs.Count(x => string.Equals(x.Status, "Processing", StringComparison.OrdinalIgnoreCase)),
                    completed.Count,
                    activeJobs.Count(x => string.Equals(x.Status, "Failed", StringComparison.OrdinalIgnoreCase)),
                    totalResources,
                    lastActivityAt,
                    lastCompletedAt,
                    nextAttemptAt,
                    lastErrorRow?.LastError);
            }

            return result;
        }

        private sealed record LocalizationStatsJobRow(
            string LanguageCode,
            string SourceKey,
            string Status,
            DateTime UpdatedAt,
            DateTime? CompletedAt,
            DateTime? NextAttemptAt,
            string? LastError);

        public async Task<int> ResumeLanguageAsync(string languageCode, CancellationToken cancellationToken = default)
        {
            languageCode = (languageCode ?? string.Empty).Trim();
            if (languageCode.Length == 0 || languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                return 0;

            // First reconcile missing resources/translations and revive old Failed rows that
            // still have a valid source. Then force every unfinished job to be due now.
            await QueueAllMissingForLanguageAsync(languageCode, cancellationToken);

            var eligibleKeys = await _db.LocalizationResources.AsNoTracking()
                .Where(x => !x.IsIgnored
                            && (x.FirstSeenPath == null || !x.FirstSeenPath.EndsWith("/DescriptionFull"))
                            && (x.FirstSeenPath == null
                                || (!x.FirstSeenPath.StartsWith("/Admin")
                                    && !x.FirstSeenPath.StartsWith("/Account")
                                    && !x.FirstSeenPath.StartsWith("/Dealer")
                                    && !x.FirstSeenPath.StartsWith("/Bayi")
                                    && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                    && !x.FirstSeenPath.StartsWith("/Havale"))
                                || x.FirstSeenPath.StartsWith("/Public/")))
                .Select(x => x.SourceKey)
                .ToListAsync(cancellationToken);

            var translationRows = await _db.LocalizationTranslations.AsNoTracking()
                .Where(x => x.LanguageCode == languageCode
                            && x.Value != ""
                            && eligibleKeys.Contains(x.SourceKey))
                .Select(x => new { x.SourceKey, x.Value, x.IsReviewed, x.IsLocked })
                .ToListAsync(cancellationToken);
            var translated = translationRows
                .Where(x => (x.IsReviewed && x.IsLocked)
                            || !LocalizationMachineTranslationService.LooksLikeTurkishText(x.Value))
                .Select(x => x.SourceKey)
                .ToHashSet(StringComparer.Ordinal);

            var jobs = await _db.LocalizationTranslationJobs
                .Where(x => x.LanguageCode == languageCode
                            && eligibleKeys.Contains(x.SourceKey)
                            && x.Status != "Completed")
                .ToListAsync(cancellationToken);

            // Also reopen Completed jobs that still only have leaked Turkish EN values so
            // Devam Ettir can replace them from the reviewed catalog without waiting on 429.
            var leakedCompletedKeys = translationRows
                .Where(x => !translated.Contains(x.SourceKey))
                .Select(x => x.SourceKey)
                .ToHashSet(StringComparer.Ordinal);
            if (leakedCompletedKeys.Count > 0)
            {
                var leakedJobs = await _db.LocalizationTranslationJobs
                    .Where(x => x.LanguageCode == languageCode
                                && leakedCompletedKeys.Contains(x.SourceKey)
                                && x.Status == "Completed")
                    .ToListAsync(cancellationToken);
                jobs.AddRange(leakedJobs);
            }

            var now = DateTime.Now;
            var resumed = 0;
            var cooling = LocalizationMachineTranslationService.IsFreeProviderCoolingDown();
            var coolDelay = LocalizationMachineTranslationService.GetFreeProviderCooldownRemaining()
                ?? TimeSpan.FromMinutes(60);
            var resourcesByKey = await _db.LocalizationResources.AsNoTracking()
                .Where(x => eligibleKeys.Contains(x.SourceKey))
                .Select(x => new { x.SourceKey, x.SourceText })
                .ToDictionaryAsync(x => x.SourceKey, x => x.SourceText, StringComparer.Ordinal, cancellationToken);

            var unfinishedKeys = jobs
                .Where(x => !translated.Contains(x.SourceKey))
                .Select(x => x.SourceKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var existingTranslationRows = unfinishedKeys.Length == 0
                ? new List<LocalizationTranslation>()
                : await _db.LocalizationTranslations
                    .Where(x => x.LanguageCode == languageCode && unfinishedKeys.Contains(x.SourceKey))
                    .ToListAsync(cancellationToken);
            var existingByKey = existingTranslationRows
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

            foreach (var job in jobs)
            {
                if (translated.Contains(job.SourceKey))
                {
                    job.Status = "Completed";
                    job.CompletedAt ??= now;
                    job.StartedAt = null;
                    job.NextAttemptAt = null;
                    job.LastError = null;
                    job.UpdatedAt = now;
                    continue;
                }

                // Close jobs that already have reviewed seed English / non-Turkish sources so
                // Dil Motoru does not stay below 100% waiting on free-provider retries.
                if (resourcesByKey.TryGetValue(job.SourceKey, out var sourceText))
                {
                    string? resolved = null;
                    if (LocalizationReviewedEnglishCatalog.TryResolve(sourceText, out var seeded)
                        || LocalizationReviewedEnglishCatalog.TryResolveByKey(job.SourceKey, out seeded))
                    {
                        resolved = seeded;
                    }
                    else if (!LocalizationMachineTranslationService.LooksLikeTurkishText(sourceText))
                    {
                        resolved = sourceText;
                    }

                    if (!string.IsNullOrWhiteSpace(resolved)
                        && !LocalizationMachineTranslationService.LooksLikeTurkishText(resolved))
                    {
                        resolved = LocalizationDisplayNormalizer.Normalize(resolved, languageCode);
                        if (!existingByKey.TryGetValue(job.SourceKey, out var row))
                        {
                            row = new LocalizationTranslation
                            {
                                SourceKey = job.SourceKey,
                                LanguageCode = languageCode,
                                Value = resolved,
                                IsMachineTranslated = false,
                                IsReviewed = true,
                                IsLocked = true,
                                UpdatedAt = now
                            };
                            _db.LocalizationTranslations.Add(row);
                            existingByKey[job.SourceKey] = row;
                        }
                        else
                        {
                            row.Value = resolved;
                            row.IsMachineTranslated = false;
                            row.IsReviewed = true;
                            row.IsLocked = true;
                            row.UpdatedAt = now;
                        }

                        job.Status = "Completed";
                        job.CompletedAt ??= now;
                        job.StartedAt = null;
                        job.NextAttemptAt = null;
                        job.LastError = null;
                        job.UpdatedAt = now;
                        translated.Add(job.SourceKey);
                        continue;
                    }
                }

                // Do not immediately re-fire rate-limited leftovers; that keeps Dil Motoru
                // stuck on "Çevriliyor: N" while Google/MyMemory return HTTP 429.
                job.Status = "Pending";
                job.StartedAt = null;
                job.UpdatedAt = now;
                if (cooling)
                {
                    job.NextAttemptAt = now.Add(coolDelay);
                    job.LastError = $"Ücretsiz çeviri kotası dolu (HTTP 429). Sonraki deneme ~{(int)Math.Ceiling(coolDelay.TotalMinutes)} dk sonra.";
                }
                else
                {
                    job.AttemptCount = 0;
                    job.NextAttemptAt = null;
                    job.LastError = null;
                    resumed++;
                }
            }

            if (jobs.Count > 0)
                await _db.SaveChangesAsync(cancellationToken);

            _catalog.InvalidateTranslations(languageCode);
            return resumed;
        }

        /// <summary>
        /// Closes every unfinished EN job that already has reviewed catalog English (or a
        /// non-Turkish source). Used when free providers are HTTP 429 throttled so Dil Motoru
        /// can still reach 100% for seed-covered strings without waiting on Google/MyMemory.
        /// </summary>
        public async Task<(int Closed, int Remaining, IReadOnlyList<string> RemainingSamples)> ForceCloseFromReviewedCatalogAsync(
            string languageCode,
            CancellationToken cancellationToken = default)
        {
            languageCode = (languageCode ?? string.Empty).Trim();
            if (!languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return (0, 0, Array.Empty<string>());

            await QueueAllMissingForLanguageAsync(languageCode, cancellationToken);

            var eligibleResources = await _db.LocalizationResources.AsNoTracking()
                .Where(x => !x.IsIgnored
                            && (x.FirstSeenPath == null || !x.FirstSeenPath.EndsWith("/DescriptionFull"))
                            && (x.FirstSeenPath == null
                                || (!x.FirstSeenPath.StartsWith("/Admin")
                                    && !x.FirstSeenPath.StartsWith("/Account")
                                    && !x.FirstSeenPath.StartsWith("/Dealer")
                                    && !x.FirstSeenPath.StartsWith("/Bayi")
                                    && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                    && !x.FirstSeenPath.StartsWith("/Havale"))
                                || x.FirstSeenPath.StartsWith("/Public/")))
                .Select(x => new { x.SourceKey, x.SourceText })
                .ToListAsync(cancellationToken);

            var resourcesByKey = eligibleResources
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last().SourceText, StringComparer.Ordinal);
            var eligibleKeys = resourcesByKey.Keys.ToArray();

            var jobs = await _db.LocalizationTranslationJobs
                .Where(x => x.LanguageCode == languageCode
                            && eligibleKeys.Contains(x.SourceKey)
                            && x.Status != "Completed")
                .ToListAsync(cancellationToken);

            var existingRows = await _db.LocalizationTranslations
                .Where(x => x.LanguageCode == languageCode && eligibleKeys.Contains(x.SourceKey))
                .ToListAsync(cancellationToken);
            var existingByKey = existingRows
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

            var now = DateTime.Now;
            var closed = 0;
            var remainingSamples = new List<string>();

            foreach (var job in jobs)
            {
                if (!resourcesByKey.TryGetValue(job.SourceKey, out var sourceText)
                    || string.IsNullOrWhiteSpace(sourceText))
                {
                    job.Status = "Failed";
                    job.LastError = "Kaynak metin bulunamadı.";
                    job.StartedAt = null;
                    job.NextAttemptAt = null;
                    job.UpdatedAt = now;
                    continue;
                }

                string? resolved = null;
                if (LocalizationReviewedEnglishCatalog.TryResolve(sourceText, out var seeded)
                    || LocalizationReviewedEnglishCatalog.TryResolveByKey(job.SourceKey, out seeded))
                {
                    resolved = seeded;
                }
                else if (!LocalizationMachineTranslationService.LooksLikeTurkishText(sourceText))
                {
                    resolved = sourceText;
                }
                else if (existingByKey.TryGetValue(job.SourceKey, out var existing)
                         && !string.IsNullOrWhiteSpace(existing.Value)
                         && ((existing.IsReviewed && existing.IsLocked)
                             || !LocalizationMachineTranslationService.LooksLikeTurkishText(existing.Value)))
                {
                    resolved = existing.Value;
                }

                if (string.IsNullOrWhiteSpace(resolved)
                    || LocalizationMachineTranslationService.LooksLikeTurkishText(resolved))
                {
                    job.Status = "Pending";
                    job.StartedAt = null;
                    job.UpdatedAt = now;
                    if (remainingSamples.Count < 12)
                        remainingSamples.Add(sourceText.Length <= 160 ? sourceText : sourceText[..157] + "...");
                    continue;
                }

                resolved = LocalizationDisplayNormalizer.Normalize(resolved, languageCode);
                if (!existingByKey.TryGetValue(job.SourceKey, out var row))
                {
                    row = new LocalizationTranslation
                    {
                        SourceKey = job.SourceKey,
                        LanguageCode = languageCode,
                        Value = resolved,
                        IsMachineTranslated = false,
                        IsReviewed = true,
                        IsLocked = true,
                        UpdatedAt = now
                    };
                    _db.LocalizationTranslations.Add(row);
                    existingByKey[job.SourceKey] = row;
                }
                else
                {
                    row.Value = resolved;
                    row.IsMachineTranslated = false;
                    row.IsReviewed = true;
                    row.IsLocked = true;
                    row.UpdatedAt = now;
                }

                job.Status = "Completed";
                job.CompletedAt ??= now;
                job.StartedAt = null;
                job.NextAttemptAt = null;
                job.LastError = null;
                job.UpdatedAt = now;
                closed++;
            }

            if (jobs.Count > 0)
                await _db.SaveChangesAsync(cancellationToken);

            _catalog.InvalidateTranslations(languageCode);
            var remaining = jobs.Count(x => !string.Equals(x.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                                            && !string.Equals(x.Status, "Failed", StringComparison.OrdinalIgnoreCase));
            return (closed, remaining, remainingSamples);
        }

        public async Task<int> RetryFailedAsync(string languageCode, CancellationToken cancellationToken = default)
        {
            if (!PublicLanguagePolicy.IsPublicTargetLanguageCode(languageCode))
                return 0;

            var activeTargets = await GetActivePublicTargetLanguageCodesAsync(cancellationToken);
            if (!activeTargets.Contains(languageCode, StringComparer.OrdinalIgnoreCase))
                return 0;

            var now = DateTime.Now;
            var eligibleKeys = _db.LocalizationResources.AsNoTracking()
                .Where(x => !x.IsIgnored
                            && (x.FirstSeenPath == null || !x.FirstSeenPath.EndsWith("/DescriptionFull"))
                            && (x.FirstSeenPath == null
                                || (!x.FirstSeenPath.StartsWith("/Admin")
                                    && !x.FirstSeenPath.StartsWith("/Account")
                                    && !x.FirstSeenPath.StartsWith("/Dealer")
                                    && !x.FirstSeenPath.StartsWith("/Bayi")
                                    && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                    && !x.FirstSeenPath.StartsWith("/Havale"))
                                || x.FirstSeenPath.StartsWith("/Public/")))
                .Select(x => x.SourceKey);

            var jobs = await _db.LocalizationTranslationJobs
                .Where(x => x.LanguageCode == languageCode
                            && x.Status == "Failed"
                            && eligibleKeys.Contains(x.SourceKey))
                .ToListAsync(cancellationToken);

            foreach (var job in jobs)
            {
                job.Status = "Pending";
                job.AttemptCount = 0;
                job.LastError = null;
                job.NextAttemptAt = null;
                job.UpdatedAt = now;
            }

            if (jobs.Count > 0)
                await _db.SaveChangesAsync(cancellationToken);

            return jobs.Count;
        }

        private async Task<int> QueueKeysForLanguageAsync(
            IReadOnlyCollection<string> sourceKeys,
            string languageCode,
            CancellationToken cancellationToken,
            bool requeueFailed = false)
        {
            if (sourceKeys.Count == 0 || !PublicLanguagePolicy.IsPublicTargetLanguageCode(languageCode))
                return 0;

            var keyArray = sourceKeys.Distinct(StringComparer.Ordinal).ToArray();
            if (string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                var guideCandidateKeys = keyArray.Where(x => EnglishGuideSources.ContainsKey(x)).ToArray();
                if (guideCandidateKeys.Length > 0)
                {
                    var currentGuideSources = await _db.LocalizationResources.AsNoTracking()
                        .Where(x => guideCandidateKeys.Contains(x.SourceKey))
                        .Select(x => new { x.SourceKey, x.SourceText })
                        .ToListAsync(cancellationToken);
                    var protectedGuideKeys = currentGuideSources
                        .Where(x => EnglishGuideSources.TryGetValue(x.SourceKey, out var original)
                                    && string.Equals(LocalizationTextKey.Normalize(x.SourceText), original, StringComparison.Ordinal))
                        .Select(x => x.SourceKey)
                        .ToHashSet(StringComparer.Ordinal);
                    keyArray = keyArray.Where(x => !protectedGuideKeys.Contains(x)).ToArray();
                }
            }

            // Unchanged reviewed English Guide content is supplied exclusively by the local
            // catalog. Edited Turkish source rows are intentionally allowed through.
            if (keyArray.Length == 0)
                return 0;

            var translationRows = await _db.LocalizationTranslations
                .Where(x => keyArray.Contains(x.SourceKey) && x.LanguageCode == languageCode)
                .ToListAsync(cancellationToken);
            var translationByKey = translationRows
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
            var translated = translationRows
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => x.SourceKey)
                .ToHashSet(StringComparer.Ordinal);

            if (languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                var sourceRows = await _db.LocalizationResources.AsNoTracking()
                    .Where(x => keyArray.Contains(x.SourceKey))
                    .Select(x => new { x.SourceKey, x.SourceText })
                    .ToListAsync(cancellationToken);

                var nowForPassThrough = DateTime.Now;
                foreach (var sourceRow in sourceRows)
                {
                    if (translated.Contains(sourceRow.SourceKey)
                        || LocalizationMachineTranslationService.LooksLikeTurkishText(sourceRow.SourceText))
                    {
                        continue;
                    }

                    if (translationByKey.TryGetValue(sourceRow.SourceKey, out var existingTranslation))
                    {
                        existingTranslation.Value = sourceRow.SourceText;
                        existingTranslation.IsMachineTranslated = false;
                        existingTranslation.UpdatedAt = nowForPassThrough;
                    }
                    else
                    {
                        var passThrough = new LocalizationTranslation
                        {
                            SourceKey = sourceRow.SourceKey,
                            LanguageCode = languageCode,
                            Value = sourceRow.SourceText,
                            IsMachineTranslated = false,
                            IsReviewed = false,
                            IsLocked = false,
                            UpdatedAt = nowForPassThrough
                        };
                        translationByKey[sourceRow.SourceKey] = passThrough;
                        _db.LocalizationTranslations.Add(passThrough);
                    }

                    translated.Add(sourceRow.SourceKey);
                }
            }

            var existingJobs = await _db.LocalizationTranslationJobs
                .Where(x => keyArray.Contains(x.SourceKey) && x.LanguageCode == languageCode)
                .ToDictionaryAsync(x => x.SourceKey, cancellationToken);

            var now = DateTime.Now;
            var queued = 0;
            foreach (var sourceKey in keyArray)
            {
                if (translated.Contains(sourceKey))
                {
                    if (existingJobs.TryGetValue(sourceKey, out var completedJob)
                        && !string.Equals(completedJob.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        completedJob.Status = "Completed";
                        completedJob.CompletedAt = now;
                        completedJob.UpdatedAt = now;
                        completedJob.LastError = null;
                    }
                    continue;
                }

                if (existingJobs.TryGetValue(sourceKey, out var existing))
                {
                    var completedWithoutTranslation = string.Equals(existing.Status, "Completed", StringComparison.OrdinalIgnoreCase);
                    var retryableFailure = requeueFailed && string.Equals(existing.Status, "Failed", StringComparison.OrdinalIgnoreCase);
                    if (completedWithoutTranslation || retryableFailure)
                    {
                        existing.Status = "Pending";
                        existing.AttemptCount = 0;
                        existing.StartedAt = null;
                        existing.CompletedAt = null;
                        existing.LastError = null;
                        existing.NextAttemptAt = null;
                        existing.UpdatedAt = now;
                        queued++;
                    }
                    continue;
                }

                _db.LocalizationTranslationJobs.Add(new LocalizationTranslationJob
                {
                    SourceKey = sourceKey,
                    LanguageCode = languageCode,
                    Status = "Pending",
                    AttemptCount = 0,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                queued++;
            }

            if (queued > 0 || _db.ChangeTracker.HasChanges())
            {
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex)
                {
                    // Another request/worker may have queued the same source-language pair.
                    // Unique index is the final guard; the next worker pass will pick the surviving row.
                    _logger.LogDebug(ex, "Dil kuyruğunda eşzamanlı kayıt çakışması güvenli şekilde yutuldu.");
                    _db.ChangeTracker.Clear();
                }
            }

            _catalog.InvalidateTranslations(languageCode);
            return queued;
        }

        private static bool IsMachineTranslationEligible(LocalizationResource resource)
        {
            if (resource.IsIgnored)
                return false;

            var path = resource.FirstSeenPath;
            if (path?.EndsWith("/DescriptionFull", StringComparison.OrdinalIgnoreCase) == true)
                return false;

            return path == null
                || ((!path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase)
                     && !path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase)
                     && !path.StartsWith("/Dealer", StringComparison.OrdinalIgnoreCase)
                     && !path.StartsWith("/Bayi", StringComparison.OrdinalIgnoreCase)
                     && !path.StartsWith("/BankTransfer", StringComparison.OrdinalIgnoreCase)
                     && !path.StartsWith("/Havale", StringComparison.OrdinalIgnoreCase))
                    || path.StartsWith("/Public/", StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string?> ExtractHtmlTextSegments(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                yield break;

            var value = html.Trim();
            if (!value.Contains('<'))
            {
                yield return value;
                yield break;
            }

            // Runtime middleware p/h/li gibi blokları, içlerinde <strong>/<em>/<br> olsa
            // bile TEK metin anahtarı olarak çözmeye çalışır. Kuyruk da aynı semantik
            // blokları üretmeli; eski yalnız-text-node regex'i formatlı blokları parçalayarak
            // bazı yeni ürün açıklamalarının yarısının Türkçe kalmasına neden oluyordu.
            var normalizedHtml = ProductContentSanitizer.NormalizeForEditor(value);
            var blockMatches = System.Text.RegularExpressions.Regex.Matches(
                normalizedHtml,
                @"(?is)<(?<tag>p|h1|h2|h3|h4|li|blockquote|caption|th|td)\b[^>]*>(?<content>.*?)</\k<tag>\s*>",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            if (blockMatches.Count > 0)
            {
                foreach (System.Text.RegularExpressions.Match match in blockMatches)
                {
                    var text = ProductContentSanitizer.ToPlainText(match.Groups["content"].Value);
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return text;
                }

                yield break;
            }

            // Semantik blok bulunmayan eski/serbest HTML için güvenli fallback.
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         normalizedHtml,
                         @">(?<text>[^<]+)<",
                         System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                var text = System.Net.WebUtility.HtmlDecode(match.Groups["text"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
            }
        }

        private async Task<IReadOnlyList<string>> GetActivePublicTargetLanguageCodesAsync(
            CancellationToken cancellationToken)
        {
            var activeLanguages = await _db.SiteLanguages.AsNoTracking()
                .Where(x => x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.NativeName)
                .ToListAsync(cancellationToken);

            return PublicLanguagePolicy.FilterPublicLanguages(activeLanguages)
                .Select(x => x.Code)
                .Where(PublicLanguagePolicy.IsPublicTargetLanguageCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> NormalizeTexts(IEnumerable<string?> sourceTexts)
        {
            return sourceTexts
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(LocalizationTextKey.Normalize)
                .Where(x => x.Length >= 2 && x.Length <= 9000 && x.Any(char.IsLetter))
                .Where(x => !LocalizationMachineTranslationService.IsInvariantLocalizationText(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static string NormalizePath(string? path)
        {
            var value = string.IsNullOrWhiteSpace(path) ? "/Admin/Content" : path.Trim();
            return value.Length <= 500 ? value : value[..500];
        }
    }
}
