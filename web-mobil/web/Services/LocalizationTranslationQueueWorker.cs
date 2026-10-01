using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed class LocalizationTranslationQueueWorker : BackgroundService
    {
        private static readonly IReadOnlyDictionary<string, string> EnglishGuideSources = LocalizationEnglishGuideSeedData.Items
            .GroupBy(x => LocalizationTextKey.Create(x.Source), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => LocalizationTextKey.Normalize(x.First().Source), StringComparer.Ordinal);

        private readonly IServiceProvider _services;
        private readonly LocalizationTranslationQueueSignal _signal;
        private readonly LocalizationMachineTranslationOptions _options;
        private readonly ILogger<LocalizationTranslationQueueWorker> _logger;
        private string? _activeLanguageCode;
        private string? _lastLanguageCode;

        public LocalizationTranslationQueueWorker(
            IServiceProvider services,
            LocalizationTranslationQueueSignal signal,
            IOptions<LocalizationMachineTranslationOptions> options,
            ILogger<LocalizationTranslationQueueWorker> logger)
        {
            _services = services;
            _signal = signal;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Give schema initialization and the web host a short head start.
            // IIS/Plesk recycle sonrası ilk ziyaretçinin CPU/DB/network ile yarışmaması için
            // Çeviri işçisi kısa bir başlangıç payı bırakır; ancak yeni publish sonrası 20 saniye
            // boyunca zorunlu bekleyip yeni metinleri Türkçe bırakmaz. appsettings değeri korunur.
            var startupDelay = TimeSpan.FromSeconds(Math.Clamp(_options.WorkerStartupDelaySeconds, 2, 120));
            try { await Task.Delay(startupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var priorityLanguage = _signal.TakePriorityLanguage();
                    var processed = await ProcessOnceAsync(priorityLanguage, stoppingToken);
                    if (processed)
                    {
                        var continuousDelay = TimeSpan.FromMilliseconds(
                            Math.Clamp(_options.WorkerContinuousDelayMilliseconds, 0, 5000));
                        if (continuousDelay > TimeSpan.Zero)
                            await Task.Delay(continuousDelay, stoppingToken);
                    }
                    else
                    {
                        var idleDelay = TimeSpan.FromSeconds(
                            Math.Clamp(_options.WorkerIdleDelaySeconds, 1, 300));
                        await _signal.WaitAsync(idleDelay, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dil otomatik çeviri kuyruğu bu turda işlenemedi.");
                    try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task<bool> ProcessOnceAsync(string? priorityLanguage, CancellationToken cancellationToken)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var machine = scope.ServiceProvider.GetRequiredService<LocalizationMachineTranslationService>();
            var catalog = scope.ServiceProvider.GetRequiredService<LocalizationCatalog>();

            var now = DateTime.Now;

            // Recover every abandoned Processing row before selecting a batch. The old
            // implementation only recovered stale rows if they happened to fit inside the
            // first global 10 jobs, so a large Pending backlog could leave them stranded.
            var staleProcessing = await db.LocalizationTranslationJobs
                .Where(x => x.Status == "Processing"
                            && (x.StartedAt == null || x.StartedAt < now.AddMinutes(-2)))
                .ToListAsync(cancellationToken);
            if (staleProcessing.Count > 0)
            {
                foreach (var stale in staleProcessing)
                {
                    stale.Status = "Pending";
                    stale.StartedAt = null;
                    stale.NextAttemptAt = null;
                    stale.LastError = "Önceki Plesk çeviri işçisi yarıda kaldı; iş otomatik olarak yeniden sıraya alındı.";
                    stale.UpdatedAt = now;
                }
                await db.SaveChangesAsync(cancellationToken);
            }

            // TR+EN mode is a hard runtime contract, not just a menu preference. Old
            // pending jobs from removed languages used to survive in the durable queue and
            // could keep consuming CPU/network even after those languages disappeared from
            // the public site. Keep only active English target jobs and retire old backlog.
            var activeLanguageRows = await db.SiteLanguages.AsNoTracking()
                .Where(x => x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.NativeName)
                .ToListAsync(cancellationToken);
            var allowedTargetLanguageCodes = PublicLanguagePolicy.FilterPublicLanguages(activeLanguageRows)
                .Select(x => x.Code)
                .Where(PublicLanguagePolicy.IsPublicTargetLanguageCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var retiredQueueJobs = allowedTargetLanguageCodes.Count == 0
                ? await db.LocalizationTranslationJobs
                    .Where(x => x.Status == "Pending" || x.Status == "Failed")
                    .ToListAsync(cancellationToken)
                : await db.LocalizationTranslationJobs
                    .Where(x => (x.Status == "Pending" || x.Status == "Failed")
                                && !allowedTargetLanguageCodes.Contains(x.LanguageCode))
                    .ToListAsync(cancellationToken);
            if (retiredQueueJobs.Count > 0)
            {
                db.LocalizationTranslationJobs.RemoveRange(retiredQueueJobs);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (allowedTargetLanguageCodes.Count == 0)
            {
                _activeLanguageCode = null;
                return false;
            }

            var eligibleLanguageCodes = await db.LocalizationTranslationJobs.AsNoTracking()
                .Where(x => x.Status == "Pending"
                            && allowedTargetLanguageCodes.Contains(x.LanguageCode)
                            && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                .Select(x => x.LanguageCode)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (eligibleLanguageCodes.Count == 0)
            {
                _activeLanguageCode = null;
                return false;
            }

            var languageOrder = allowedTargetLanguageCodes
                .Where(x => eligibleLanguageCodes.Contains(x, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var enabledLanguages = languageOrder
                .Where(machine.IsEnabledFor)
                .ToList();
            if (enabledLanguages.Count == 0)
            {
                _activeLanguageCode = null;
                return false;
            }

            var languageCode = SelectLanguage(enabledLanguages, priorityLanguage);
            if (languageCode == null)
                return false;

            var batchSize = Math.Clamp(_options.QueueBatchSize, 5, 200);
            if (LocalizationMachineTranslationService.IsFreeProviderCoolingDown())
                batchSize = Math.Min(batchSize, 8);
            var jobs = await db.LocalizationTranslationJobs
                .Where(x => x.LanguageCode == languageCode
                            && x.Status == "Pending"
                            && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                .OrderBy(x => x.NextAttemptAt == null ? 0 : 1)
                .ThenBy(x => x.NextAttemptAt)
                .ThenBy(x => x.CreatedAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (jobs.Count == 0)
            {
                _activeLanguageCode = null;
                return false;
            }

            // Reviewed Guide English remains local-only while its Turkish source is unchanged.
            // If the admin edits the canonical Turkish source, SourceKey stays stable but the
            // old reviewed target becomes stale; that edited row is then allowed through the
            // normal retranslation queue instead of being permanently protected by its old key.
            var protectedGuideJobs = new List<LocalizationTranslationJob>();
            if (string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                var guideCandidateKeys = jobs
                    .Where(x => EnglishGuideSources.ContainsKey(x.SourceKey))
                    .Select(x => x.SourceKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (guideCandidateKeys.Length > 0)
                {
                    var currentGuideSources = await db.LocalizationResources.AsNoTracking()
                        .Where(x => guideCandidateKeys.Contains(x.SourceKey))
                        .Select(x => new { x.SourceKey, x.SourceText })
                        .ToListAsync(cancellationToken);
                    var protectedKeys = currentGuideSources
                        .Where(x => EnglishGuideSources.TryGetValue(x.SourceKey, out var original)
                                    && string.Equals(LocalizationTextKey.Normalize(x.SourceText), original, StringComparison.Ordinal))
                        .Select(x => x.SourceKey)
                        .ToHashSet(StringComparer.Ordinal);

                    protectedGuideJobs = jobs.Where(x => protectedKeys.Contains(x.SourceKey)).ToList();
                }
            }

            if (protectedGuideJobs.Count > 0)
            {
                foreach (var job in protectedGuideJobs)
                {
                    job.Status = "Completed";
                    job.CompletedAt = now;
                    job.StartedAt = null;
                    job.NextAttemptAt = null;
                    job.LastError = null;
                    job.UpdatedAt = now;
                }

                await db.SaveChangesAsync(cancellationToken);
                jobs.RemoveAll(x => protectedGuideJobs.Contains(x));
                if (jobs.Count == 0)
                {
                    await UpdateLanguageTurnAsync(db, languageCode, madeProgress: true, cancellationToken);
                    return true;
                }
            }

            foreach (var job in jobs)
            {
                job.Status = "Processing";
                job.AttemptCount = Math.Min(1_000_000, job.AttemptCount + 1);
                job.StartedAt = DateTime.Now;
                job.UpdatedAt = job.StartedAt.Value;
                job.LastError = null;
            }
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var keys = jobs.Select(x => x.SourceKey).ToArray();
                var resources = await db.LocalizationResources.AsNoTracking()
                    .Where(x => keys.Contains(x.SourceKey)
                                && !x.IsIgnored
                                && (x.FirstSeenPath == null || !x.FirstSeenPath.EndsWith("/DescriptionFull"))
                                && (x.FirstSeenPath == null
                                    || (!x.FirstSeenPath.StartsWith("/Admin")
                                        && !x.FirstSeenPath.StartsWith("/Account")
                                        && !x.FirstSeenPath.StartsWith("/Dealer")
                                        && !x.FirstSeenPath.StartsWith("/Bayi")
                                        && !x.FirstSeenPath.StartsWith("/BankTransfer")
                                        && !x.FirstSeenPath.StartsWith("/Havale"))
                                    || x.FirstSeenPath.StartsWith("/Public/")))
                    .ToDictionaryAsync(x => x.SourceKey, x => x.SourceText, cancellationToken);

                var nativeMap = await catalog.GetNativeTranslationMapAsync(languageCode, cancellationToken);
                var isEnglish = languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
                var translated = new HashSet<string>(StringComparer.Ordinal);

                // Close reviewed-seed / pass-through jobs BEFORE calling free providers.
                // Otherwise HTTP 429 burns the whole batch and Dil Motoru never reaches 100%.
                if (isEnglish)
                {
                    await PersistResolvedEnglishAsync(
                        db,
                        jobs,
                        resources,
                        nativeMap,
                        languageCode,
                        translated,
                        cancellationToken);
                }

                var machineResources = resources
                    .Where(x => !translated.Contains(x.Key))
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

                string? providerFailure = null;
                if (machineResources.Count > 0)
                {
                    // Even during cooldown, still try Clients5 only. GoogleFree/MyMemory are
                    // skipped inside FreeCascade while IsFreeProviderCoolingDown() is true.
                    using var translationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var timeoutSeconds = LocalizationMachineTranslationService.IsFreeProviderCoolingDown()
                        ? 45
                        : Math.Clamp(_options.WorkerBatchTimeoutSeconds, 20, 600);
                    translationCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    await machine.TranslateMissingAsync(
                        machineResources,
                        languageCode,
                        nativeMap,
                        "/Localization/Queue",
                        translationCts.Token);

                    var translatedKeys = await db.LocalizationTranslations.AsNoTracking()
                        .Where(x => machineResources.Keys.Contains(x.SourceKey)
                                    && x.LanguageCode == languageCode
                                    && x.Value != "")
                        .Select(x => x.SourceKey)
                        .ToListAsync(cancellationToken);
                    foreach (var key in translatedKeys)
                        translated.Add(key);

                    if (isEnglish)
                    {
                        await PersistResolvedEnglishAsync(
                            db,
                            jobs.Where(x => !translated.Contains(x.SourceKey)).ToList(),
                            resources,
                            nativeMap,
                            languageCode,
                            translated,
                            cancellationToken);
                    }

                    providerFailure = machine.GetLastProviderFailureSummary();
                    if (IsRateLimitFailure(providerFailure))
                        LocalizationMachineTranslationService.ActivateFreeProviderCooldown(TimeSpan.FromMinutes(60));
                }

                var finishedAt = DateTime.Now;
                var rateLimited = IsRateLimitFailure(providerFailure);
                foreach (var job in jobs)
                {
                    if (translated.Contains(job.SourceKey))
                    {
                        job.Status = "Completed";
                        job.CompletedAt = finishedAt;
                        job.NextAttemptAt = null;
                        job.LastError = null;
                    }
                    else if (!resources.ContainsKey(job.SourceKey))
                    {
                        job.Status = "Failed";
                        job.LastError = "Kaynak metin bulunamadı.";
                        job.NextAttemptAt = null;
                    }
                    else
                    {
                        job.Status = "Pending";
                        job.LastError = string.IsNullOrWhiteSpace(providerFailure)
                            ? "Çeviri bu turda üretilemedi; otomatik yeniden denenecek."
                            : providerFailure;
                        job.NextAttemptAt = finishedAt.Add(
                            rateLimited
                                ? GetRateLimitRetryDelay(job.AttemptCount)
                                : GetRetryDelay(job.AttemptCount));
                    }

                    job.UpdatedAt = finishedAt;
                    job.StartedAt = null;
                }

                await db.SaveChangesAsync(cancellationToken);
                catalog.InvalidateTranslations(languageCode);
                await UpdateLanguageTurnAsync(db, languageCode, translated.Count > 0, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // IIS/Plesk is stopping or recycling the application. Best effort puts the
                // claimed rows back into Pending so they are not stranded as Processing.
                var interruptedAt = DateTime.Now;
                MarkJobsPendingForRetry(
                    jobs,
                    interruptedAt,
                    "Plesk/IIS çeviri işçisini yarıda durdurdu; iş yeniden sıraya alındı.",
                    TimeSpan.FromSeconds(10));
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await db.SaveChangesAsync(cleanupCts.Token);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogDebug(cleanupException, "Kapanış sırasında çeviri işleri Pending durumuna geri alınamadı; sonraki başlangıçta stale kurtarma çalışacak.");
                }
                throw;
            }
            catch (OperationCanceledException ex)
            {
                var timedOutAt = DateTime.Now;
                var timeoutSeconds = Math.Clamp(_options.WorkerBatchTimeoutSeconds, 20, 600);
                MarkJobsPendingForRetry(
                    jobs,
                    timedOutAt,
                    $"Çeviri sağlayıcıları {timeoutSeconds} saniye içinde grubu tamamlayamadı; otomatik yeniden denenecek.",
                    GetRateLimitRetryDelay(jobs.Max(x => x.AttemptCount)));
                await db.SaveChangesAsync(cancellationToken);
                await UpdateLanguageTurnAsync(db, languageCode, madeProgress: false, cancellationToken);
                _logger.LogWarning(ex, "{LanguageCode} otomatik çeviri grubu zaman aşımına uğradı.", languageCode);
            }
            catch (Exception ex)
            {
                var failedAt = DateTime.Now;
                var error = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
                MarkJobsPendingForRetry(
                    jobs,
                    failedAt,
                    error,
                    GetRetryDelay(jobs.Max(x => x.AttemptCount)));
                await db.SaveChangesAsync(cancellationToken);
                await UpdateLanguageTurnAsync(db, languageCode, madeProgress: false, cancellationToken);
                _logger.LogWarning(ex, "{LanguageCode} otomatik çeviri kuyruğu işlenirken hata oluştu.", languageCode);
            }

            return true;
        }

        private static void MarkJobsPendingForRetry(
            IEnumerable<LocalizationTranslationJob> jobs,
            DateTime failedAt,
            string error,
            TimeSpan retryDelay)
        {
            var safeError = error.Length <= 1000 ? error : error[..1000];
            foreach (var job in jobs)
            {
                job.Status = "Pending";
                job.LastError = safeError;
                job.NextAttemptAt = failedAt.Add(retryDelay);
                job.UpdatedAt = failedAt;
                job.StartedAt = null;
            }
        }

        private string? SelectLanguage(IReadOnlyList<string> languages, string? priorityLanguage)
        {
            if (languages.Count == 0)
                return null;

            // An explicit "Devam Ettir" request must take the next batch immediately.
            // Previously it waited until the currently active language had no work left,
            // which could starve newly-added Azerbaijani and Turkmen queues for hours.
            if (!string.IsNullOrWhiteSpace(priorityLanguage))
            {
                var requested = languages.FirstOrDefault(x =>
                    string.Equals(x, priorityLanguage.Trim(), StringComparison.OrdinalIgnoreCase));
                if (requested != null)
                {
                    _activeLanguageCode = requested;
                    return requested;
                }
            }

            var active = languages.FirstOrDefault(x =>
                string.Equals(x, _activeLanguageCode, StringComparison.OrdinalIgnoreCase));
            if (active != null)
                return active;

            _activeLanguageCode = null;
            var lastIndex = -1;
            for (var i = 0; i < languages.Count; i++)
            {
                if (string.Equals(languages[i], _lastLanguageCode, StringComparison.OrdinalIgnoreCase))
                {
                    lastIndex = i;
                    break;
                }
            }

            var selected = languages[(lastIndex + 1) % languages.Count];
            _activeLanguageCode = selected;
            return selected;
        }

        private async Task UpdateLanguageTurnAsync(
            ApplicationDbContext db,
            string languageCode,
            bool madeProgress,
            CancellationToken cancellationToken)
        {
            _lastLanguageCode = languageCode;
            if (!madeProgress)
            {
                // The provider produced nothing for this batch. Let another language run
                // instead of allowing one throttled target to block the whole queue.
                _activeLanguageCode = null;
                return;
            }

            var now = DateTime.Now;
            var hasReadyWork = await db.LocalizationTranslationJobs.AsNoTracking()
                .AnyAsync(x => x.LanguageCode == languageCode
                               && x.Status == "Pending"
                               && (x.NextAttemptAt == null || x.NextAttemptAt <= now),
                    cancellationToken);
            _activeLanguageCode = hasReadyWork ? languageCode : null;
        }

        private static async Task PersistResolvedEnglishAsync(
            ApplicationDbContext db,
            IReadOnlyList<LocalizationTranslationJob> jobs,
            IReadOnlyDictionary<string, string> resources,
            IReadOnlyDictionary<string, string> nativeMap,
            string languageCode,
            HashSet<string> translated,
            CancellationToken cancellationToken)
        {
            var missingJobs = jobs.Where(x => !translated.Contains(x.SourceKey)).ToList();
            if (missingJobs.Count == 0)
                return;

            var missingKeys = missingJobs.Select(x => x.SourceKey).ToArray();
            var existingRows = await db.LocalizationTranslations
                .Where(x => missingKeys.Contains(x.SourceKey) && x.LanguageCode == languageCode)
                .ToListAsync(cancellationToken);
            var existingByKey = existingRows
                .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
            var persistAt = DateTime.Now;
            var wrote = false;

            foreach (var job in missingJobs)
            {
                if (!resources.TryGetValue(job.SourceKey, out var sourceText)
                    || string.IsNullOrWhiteSpace(sourceText))
                {
                    continue;
                }

                string? resolved = null;
                if (LocalizationReviewedEnglishCatalog.TryResolve(sourceText, out var seeded)
                    || LocalizationReviewedEnglishCatalog.TryResolveByKey(job.SourceKey, out seeded))
                {
                    resolved = seeded;
                }
                else if (nativeMap.TryGetValue(job.SourceKey, out var mapped)
                         && !string.IsNullOrWhiteSpace(mapped)
                         && !LocalizationMachineTranslationService.LooksLikeTurkishText(mapped))
                {
                    resolved = mapped;
                }
                else if (!LocalizationMachineTranslationService.LooksLikeTurkishText(sourceText))
                {
                    resolved = sourceText;
                }

                if (string.IsNullOrWhiteSpace(resolved))
                    continue;

                resolved = LocalizationDisplayNormalizer.Normalize(resolved, languageCode);
                if (string.IsNullOrWhiteSpace(resolved)
                    || LocalizationMachineTranslationService.LooksLikeTurkishText(resolved))
                {
                    continue;
                }

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
                        UpdatedAt = persistAt
                    };
                    db.LocalizationTranslations.Add(row);
                    existingByKey[job.SourceKey] = row;
                }
                else if (LocalizationMachineTranslationService.LooksLikeTurkishText(row.Value)
                         || string.IsNullOrWhiteSpace(row.Value)
                         || !row.IsReviewed)
                {
                    row.Value = resolved;
                    row.IsMachineTranslated = false;
                    row.IsReviewed = true;
                    row.IsLocked = true;
                    row.UpdatedAt = persistAt;
                }

                translated.Add(job.SourceKey);
                wrote = true;
            }

            if (wrote)
                await db.SaveChangesAsync(cancellationToken);
        }

        private static bool IsRateLimitFailure(string? providerFailure)
            => !string.IsNullOrWhiteSpace(providerFailure)
               && (providerFailure.Contains("429", StringComparison.OrdinalIgnoreCase)
                   || providerFailure.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                   || providerFailure.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

        private static TimeSpan GetRetryDelay(int attemptCount)
        {
            var minutes = attemptCount switch
            {
                <= 1 => 1,
                2 => 2,
                3 => 4,
                4 => 7,
                5 => 10,
                _ => Math.Min(30, 10 + ((attemptCount - 5) * 2))
            };
            return TimeSpan.FromMinutes(minutes);
        }

        private static TimeSpan GetRateLimitRetryDelay(int attemptCount)
        {
            // Free Google/MyMemory quotas recover slowly on shared hosting IPs.
            // Short retries only deepen the 429 hole and keep Dil Motoru below 100%.
            var minutes = attemptCount switch
            {
                <= 1 => 45,
                2 => 60,
                3 => 90,
                _ => 120
            };
            return TimeSpan.FromMinutes(minutes);
        }
    }
}
