using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin/Languages")]
    public sealed class AdminLanguagesController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly LocalizationCatalog _catalog;
        private readonly LocalizationMachineTranslationService _machineTranslation;
        private readonly LocalizationTranslationQueueService _translationQueue;
        private readonly LocalizationTranslationQueueSignal _translationQueueSignal;
        private readonly LocalizationGuideTranslationService _guideTranslations;

        public AdminLanguagesController(
            ApplicationDbContext db,
            LocalizationCatalog catalog,
            LocalizationMachineTranslationService machineTranslation,
            LocalizationTranslationQueueService translationQueue,
            LocalizationTranslationQueueSignal translationQueueSignal,
            LocalizationGuideTranslationService guideTranslations)
        {
            _db = db;
            _catalog = catalog;
            _machineTranslation = machineTranslation;
            _translationQueue = translationQueue;
            _translationQueueSignal = translationQueueSignal;
            _guideTranslations = guideTranslations;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var languages = await _db.SiteLanguages.AsNoTracking()
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.NativeName)
                .ToListAsync();
            var statsByLanguage = await _translationQueue.GetStatsForLanguagesAsync(
                languages.Select(x => x.Code),
                HttpContext.RequestAborted);
            var totalResources = statsByLanguage.Values.FirstOrDefault()?.Total ?? 0;
            var rows = new List<AdminLanguageRowViewModel>(languages.Count);
            foreach (var language in languages)
            {
                var queueStats = statsByLanguage.TryGetValue(language.Code, out var stats)
                    ? stats
                    : new LocalizationQueueStats(0, 0, 0, 0, totalResources, null, null, null, null);
                rows.Add(new AdminLanguageRowViewModel
                {
                    Language = language,
                    TranslationCount = queueStats.Completed,
                    CoveragePercent = Math.Min(100m, queueStats.ProgressPercent),
                    QueuePending = queueStats.Pending,
                    QueueProcessing = queueStats.Processing,
                    QueueCompleted = queueStats.Completed,
                    QueueFailed = queueStats.Failed,
                    QueueProgressPercent = queueStats.ProgressPercent,
                    QueueLastActivityAt = queueStats.LastActivityAt,
                    QueueLastCompletedAt = queueStats.LastCompletedAt,
                    QueueNextAttemptAt = queueStats.NextAttemptAt,
                    QueueLastError = queueStats.LastError
                });
            }

            return View(new AdminLanguagesViewModel
            {
                Languages = rows,
                TotalResources = totalResources,
                MachineTranslationAvailable = _machineTranslation.IsEnabledFor("en-US")
            });
        }

        [HttpPost("Add")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Add(string? code, string? urlCode, string? nativeName, string? englishName, string? flagEmoji, string? direction, int sortOrder = 100, bool activateNow = false, bool autoTranslateNow = false)
        {
            code = NormalizeLanguageCode(code);
            urlCode = NormalizeUrlCode(urlCode);
            nativeName = (nativeName ?? string.Empty).Trim();
            englishName = (englishName ?? string.Empty).Trim();
            flagEmoji = NormalizeFlagEmoji(flagEmoji);
            direction = string.Equals(direction, "RTL", StringComparison.OrdinalIgnoreCase) ? "RTL" : "LTR";

            if (!Regex.IsMatch(code, "^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,2}$", RegexOptions.CultureInvariant)
                || !Regex.IsMatch(urlCode, "^[a-z]{2,3}(?:-[a-z0-9]{2,8})?$", RegexOptions.CultureInvariant)
                || string.IsNullOrWhiteSpace(nativeName)
                || string.IsNullOrWhiteSpace(englishName))
            {
                TempData["Error"] = "Dil kodu, URL kodu ve dil adlarını kontrol edin.";
                return RedirectToAction(nameof(Index));
            }

            if (!PublicLanguagePolicy.IsSupported(new SiteLanguage { Code = code, UrlCode = urlCode }))
            {
                TempData["Error"] = "NSX public sitesi TR + EN modunda kilitlidir. Yeni public dil eklenemez.";
                return RedirectToAction(nameof(Index));
            }

            if (await _db.SiteLanguages.AnyAsync(x => x.Code == code || x.UrlCode == urlCode))
            {
                TempData["Error"] = "Bu dil kodu veya URL kodu zaten kayıtlı.";
                return RedirectToAction(nameof(Index));
            }

            _db.SiteLanguages.Add(new SiteLanguage
            {
                Code = code,
                UrlCode = urlCode,
                NativeName = nativeName,
                EnglishName = englishName,
                FlagEmoji = flagEmoji,
                Direction = direction,
                IsActive = activateNow,
                IsDefault = false,
                SortOrder = Math.Clamp(sortOrder, 0, 9999),
                CreatedAt = DateTime.Now
            });
            await _db.SaveChangesAsync();
            _catalog.InvalidateLanguages();
            _catalog.InvalidateTranslations(code);

            // First index every known dynamic DB content (products, long description
            // text nodes, categories, videos, ads, campaigns and sliders), then backfill
            // this new language across the complete catalog. There is no item limit.
            await _translationQueue.IndexKnownDynamicContentAsync(HttpContext.RequestAborted);
            var queuedCount = await _translationQueue.QueueAllMissingForLanguageAsync(code, HttpContext.RequestAborted);
            if (_machineTranslation.IsEnabledFor(code))
                _translationQueueSignal.Pulse(code);

            var activationText = activateNow
                ? "aktif edildi ve site dil seçicisine bağlandı"
                : "pasif olarak eklendi";
            var providerText = _machineTranslation.IsEnabledFor(code)
                ? "Otomatik çeviri işçisi kuyruğu arka planda tamamlayacak."
                : "Çeviri sağlayıcısı etkinleştiğinde bekleyen kuyruk otomatik devam edecek.";
            var queueStats = await _translationQueue.GetStatsAsync(code, HttpContext.RequestAborted);
            TempData["Success"] = $"{nativeName} {activationText}. {queuedCount} yeni çeviri işi oluşturuldu; toplam {queueStats.Pending} metin bekliyor, {queueStats.Completed}/{queueStats.Total} metin hazır. {providerText}";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Edit/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string? nativeName, string? englishName, string? flagEmoji, string? direction, int sortOrder = 100)
        {
            var language = await _db.SiteLanguages.FirstOrDefaultAsync(x => x.Id == id);
            if (language == null)
                return NotFound();

            nativeName = (nativeName ?? string.Empty).Trim();
            englishName = (englishName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(nativeName) || string.IsNullOrWhiteSpace(englishName))
            {
                TempData["Error"] = "Yerel ve İngilizce dil adları boş bırakılamaz.";
                return RedirectToAction(nameof(Index));
            }

            language.NativeName = nativeName;
            language.EnglishName = englishName;
            language.FlagEmoji = NormalizeFlagEmoji(flagEmoji);
            language.Direction = string.Equals(direction, "RTL", StringComparison.OrdinalIgnoreCase) ? "RTL" : "LTR";
            language.SortOrder = Math.Clamp(sortOrder, 0, 9999);
            language.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
            _catalog.InvalidateLanguages();

            TempData["Success"] = $"{language.NativeName} dil ayarları güncellendi.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Delete/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var language = await _db.SiteLanguages.FirstOrDefaultAsync(x => x.Id == id);
            if (language == null)
                return NotFound();

            if (language.IsDefault || IsSourceLanguage(language.Code))
            {
                TempData["Error"] = language.IsDefault
                    ? "Varsayılan dil silinemez. Önce başka bir dili varsayılan yapın."
                    : "Türkçe kaynak dil silinemez.";
                return RedirectToAction(nameof(Index));
            }

            var languageCode = language.Code;
            var languageName = language.NativeName;
            await using var transaction = await _db.Database.BeginTransactionAsync(HttpContext.RequestAborted);

            await _db.LocalizationTranslationJobs
                .Where(x => x.LanguageCode == languageCode)
                .ExecuteDeleteAsync(HttpContext.RequestAborted);
            await _db.LocalizationTranslations
                .Where(x => x.LanguageCode == languageCode)
                .ExecuteDeleteAsync(HttpContext.RequestAborted);
            await _db.UserLanguagePreferences
                .Where(x => x.LanguageCode == languageCode)
                .ExecuteDeleteAsync(HttpContext.RequestAborted);

            _db.SiteLanguages.Remove(language);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            await transaction.CommitAsync(HttpContext.RequestAborted);

            _catalog.InvalidateLanguages();
            _catalog.InvalidateTranslations(languageCode);
            TempData["Success"] = $"{languageName} ve bu dile ait çeviri kayıtları silindi.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Toggle/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id)
        {
            var language = await _db.SiteLanguages.FirstOrDefaultAsync(x => x.Id == id);
            if (language == null)
                return NotFound();

            if (!PublicLanguagePolicy.IsSupported(language))
            {
                TempData["Error"] = "Bu dil TR + EN public dil politikasının dışındadır ve yeniden aktifleştirilemez.";
                return RedirectToAction(nameof(Index));
            }

            if (language.IsDefault && language.IsActive)
            {
                TempData["Error"] = "Varsayılan dil pasif yapılamaz. Önce başka bir dili varsayılan yapın.";
                return RedirectToAction(nameof(Index));
            }

            language.IsActive = !language.IsActive;
            language.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
            _catalog.InvalidateLanguages();

            var queued = 0;
            if (language.IsActive)
            {
                await _translationQueue.IndexKnownDynamicContentAsync(HttpContext.RequestAborted);
                queued = await _translationQueue.QueueAllMissingForLanguageAsync(language.Code, HttpContext.RequestAborted);
            }
            if (language.IsActive && _machineTranslation.IsEnabledFor(language.Code))
                _translationQueueSignal.Pulse(language.Code);
            TempData["Success"] = language.IsActive
                ? $"{language.NativeName} aktif yapıldı; {queued} eksik metin otomatik çeviri kuyruğuna alındı."
                : $"{language.NativeName} pasif yapıldı.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Default/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetDefault(int id)
        {
            var target = await _db.SiteLanguages.FirstOrDefaultAsync(x => x.Id == id);
            if (target == null)
                return NotFound();

            if (!IsSourceLanguage(target.Code))
            {
                TempData["Error"] = "NSX sitesinde kaynak ve varsayılan dil Türkçe olarak sabittir. Diğer diller URL önekiyle çalışır.";
                return RedirectToAction(nameof(Index));
            }

            var defaults = await _db.SiteLanguages.Where(x => x.IsDefault).ToListAsync();
            foreach (var language in defaults)
            {
                language.IsDefault = false;
                language.UpdatedAt = DateTime.Now;
            }

            target.IsDefault = true;
            target.IsActive = true;
            target.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
            _catalog.InvalidateLanguages();
            var queued = await _translationQueue.QueueAllMissingForLanguageAsync(target.Code, HttpContext.RequestAborted);
            if (_machineTranslation.IsEnabledFor(target.Code))
                _translationQueueSignal.Pulse(target.Code);

            TempData["Success"] = $"Varsayılan dil {target.NativeName} olarak ayarlandı. {queued} eksik metin çeviri kuyruğuna alındı.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Resume/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Resume(int id)
        {
            var language = await _db.SiteLanguages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (language == null)
                return NotFound();

            if (!PublicLanguagePolicy.IsSupported(language))
            {
                TempData["Error"] = "Bu dil TR + EN public dil politikasının dışındadır; çeviri kuyruğu yeniden başlatılamaz.";
                return RedirectToAction(nameof(Index));
            }

            if (language.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Info"] = "Türkçe kaynak dil olduğu için otomatik çeviri kuyruğu kullanılmıyor.";
                return RedirectToAction(nameof(Index));
            }

            await _translationQueue.IndexKnownDynamicContentAsync(HttpContext.RequestAborted);
            var resumed = await _translationQueue.ResumeLanguageAsync(language.Code, HttpContext.RequestAborted);
            if (resumed > 0)
                _translationQueueSignal.Pulse(language.Code);
            var stats = await _translationQueue.GetStatsAsync(language.Code, HttpContext.RequestAborted);
            var cooling = LocalizationMachineTranslationService.IsFreeProviderCoolingDown();
            var coolMinutes = (int)Math.Ceiling(
                (LocalizationMachineTranslationService.GetFreeProviderCooldownRemaining() ?? TimeSpan.Zero).TotalMinutes);

            if (cooling && stats.Pending + stats.Processing > 0)
            {
                TempData["Info"] =
                    $"{language.NativeName}: ücretsiz çeviri kotası dolu (HTTP 429). Seed ile kapanabilenler işlendi; kalan {stats.Pending + stats.Processing} iş ~{Math.Max(coolMinutes, 15)} dk sonra otomatik denenecek. Şu an {stats.Completed}/{stats.Total} hazır.";
            }
            else
            {
                TempData["Success"] = resumed > 0
                    ? $"{language.NativeName} çeviri motoru devam ettirildi. {resumed} iş hemen yeniden denemeye açıldı; {stats.Completed}/{stats.Total} hazır, {stats.Pending} bekliyor."
                    : $"{language.NativeName} kuyruğu kontrol edildi. {stats.Completed}/{stats.Total} çeviri hazır" +
                      (stats.Pending + stats.Processing > 0
                          ? $"; kalan {stats.Pending + stats.Processing} iş sırada."
                          : ".");
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("ForceCloseFromCatalog/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForceCloseFromCatalog(int id)
        {
            var language = await _db.SiteLanguages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (language == null)
                return NotFound();

            if (!language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Info"] = "Katalog ile kapatma yalnızca English hedef dili için kullanılır.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _translationQueue.ForceCloseFromReviewedCatalogAsync(
                language.Code,
                HttpContext.RequestAborted);
            var stats = await _translationQueue.GetStatsAsync(language.Code, HttpContext.RequestAborted);

            if (result.Remaining == 0)
            {
                TempData["Success"] =
                    $"{language.NativeName}: katalog ile {result.Closed} iş kapatıldı. {stats.Completed}/{stats.Total} hazır (%{stats.ProgressPercent}).";
            }
            else
            {
                var samples = result.RemainingSamples.Count == 0
                    ? string.Empty
                    : " Örnek kalanlar: " + string.Join(" | ", result.RemainingSamples.Take(5));
                TempData["Info"] =
                    $"{language.NativeName}: katalog ile {result.Closed} iş kapatıldı; {result.Remaining} iş hâlâ ücretsiz API veya manuel çeviri bekliyor ({stats.Completed}/{stats.Total}).{samples}";
                if (result.Remaining > 0 && !LocalizationMachineTranslationService.IsFreeProviderCoolingDown())
                    _translationQueueSignal.Pulse(language.Code);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Heartbeat")]
        [ValidateAntiForgeryToken]
        public IActionResult Heartbeat()
        {
            // While the admin monitors the queue, keep the IIS/Plesk application warm and
            // wake the durable worker immediately. No translation is performed in the HTTP
            // request itself, so the endpoint stays fast and safe to call periodically.
            _translationQueueSignal.Pulse();
            return NoContent();
        }

        [HttpGet("{code}/Translations")]
        public async Task<IActionResult> Translations(string code, string? q = null, int page = 1, int? productId = null)
        {
            var language = await _db.SiteLanguages.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
            if (language == null)
                return NotFound();

            var isSourceLanguage = IsSourceLanguage(language.Code);
            const int pageSize = 60;
            page = Math.Max(1, page);
            int? matchedProductId = null;
            string? matchedProductName = null;
            string? matchedProductDescription = null;
            string? matchedProductFullKey = null;

            var resourcesQuery = _db.LocalizationResources.AsNoTracking().Where(x => !x.IsIgnored);

            // A direct productId is used after saving so the page never has to rediscover
            // the product from a long search query. This keeps the editor bound to one
            // stable full-description key even when the description contains many HTML blocks.
            if (productId.HasValue)
            {
                var directProduct = await _db.Products.AsNoTracking()
                    .Where(x => x.Id == productId.Value && !x.IsDeleted && x.Description != null && x.Description != "")
                    .Select(x => new { x.Id, x.Name, x.Description })
                    .FirstOrDefaultAsync();

                if (directProduct != null)
                {
                    var fullPlain = ProductContentSanitizer.ToPlainText(directProduct.Description);
                    if (!string.IsNullOrWhiteSpace(fullPlain))
                    {
                        matchedProductId = directProduct.Id;
                        matchedProductName = directProduct.Name;
                        matchedProductFullKey = await EnsureFullProductDescriptionResourceAsync(
                            directProduct.Id,
                            fullPlain,
                            HttpContext.RequestAborted);
                        matchedProductDescription = await _db.LocalizationResources.AsNoTracking()
                            .Where(x => x.SourceKey == matchedProductFullKey)
                            .Select(x => x.SourceText)
                            .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? fullPlain;
                    }
                }
            }

            if (!matchedProductId.HasValue && !string.IsNullOrWhiteSpace(q))
            {
                q = LocalizationTextKey.Normalize(q);
                var queryText = NormalizeAdminSearchQuery(q);
                if (string.IsNullOrWhiteSpace(queryText))
                    queryText = q;

                var products = await _db.Products.AsNoTracking()
                    .Where(x => !x.IsDeleted && x.Description != null && x.Description != "")
                    .Select(x => new { x.Id, x.Name, x.Description })
                    .ToListAsync();

                foreach (var product in products)
                {
                    var match = FindMatchingDescriptionBlock(product.Description, queryText);
                    if (match == null)
                        continue;

                    // Search can match any paragraph/list item, but editing always targets
                    // the ENTIRE current product description. One product = one stable key.
                    var fullPlain = ProductContentSanitizer.ToPlainText(product.Description);
                    if (string.IsNullOrWhiteSpace(fullPlain))
                        continue;

                    matchedProductId = product.Id;
                    matchedProductName = product.Name;
                    matchedProductFullKey = await EnsureFullProductDescriptionResourceAsync(
                        product.Id,
                        fullPlain,
                        HttpContext.RequestAborted);
                    matchedProductDescription = await _db.LocalizationResources.AsNoTracking()
                        .Where(x => x.SourceKey == matchedProductFullKey)
                        .Select(x => x.SourceText)
                        .FirstOrDefaultAsync(HttpContext.RequestAborted) ?? fullPlain;
                    break;
                }

                if (!matchedProductId.HasValue)
                {
                    if (isSourceLanguage)
                    {
                        resourcesQuery = resourcesQuery.Where(x =>
                            x.SourceText.Contains(queryText)
                            || (x.FirstSeenPath != null && x.FirstSeenPath.Contains(queryText)));
                    }
                    else
                    {
                        var matchedTranslationKeys = _db.LocalizationTranslations.AsNoTracking()
                            .Where(x => x.LanguageCode == language.Code && x.Value.Contains(queryText))
                            .Select(x => x.SourceKey);

                        resourcesQuery = resourcesQuery.Where(x =>
                            x.SourceText.Contains(queryText)
                            || (x.FirstSeenPath != null && x.FirstSeenPath.Contains(queryText))
                            || matchedTranslationKeys.Contains(x.SourceKey));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(matchedProductFullKey))
            {
                var stableFullKey = matchedProductFullKey;
                resourcesQuery = resourcesQuery.Where(x => x.SourceKey == stableFullKey);
                page = 1;
            }

            var totalCount = await resourcesQuery.CountAsync();
            var resources = !string.IsNullOrWhiteSpace(matchedProductFullKey)
                ? await resourcesQuery.ToListAsync()
                : await resourcesQuery
                    .OrderByDescending(x => x.LastSeenAt)
                    .ThenBy(x => x.Id)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

            var resourceKeys = resources.Select(x => x.SourceKey).ToArray();
            var translationRows = isSourceLanguage
                ? new List<LocalizationTranslation>()
                : await _db.LocalizationTranslations.AsNoTracking()
                    .Where(x => x.LanguageCode == language.Code && resourceKeys.Contains(x.SourceKey))
                    .ToListAsync();
            var translations = translationRows.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);

            var items = resources.Select(resource =>
            {
                translations.TryGetValue(resource.SourceKey, out var translation);
                return new AdminTranslationRowViewModel
                {
                    SourceKey = resource.SourceKey,
                    SourceText = resource.SourceText,
                    FirstSeenPath = resource.FirstSeenPath,
                    HitCount = resource.HitCount,
                    Value = isSourceLanguage ? resource.SourceText : translation?.Value ?? string.Empty,
                    IsReviewed = isSourceLanguage || translation?.IsReviewed == true,
                    IsLocked = isSourceLanguage || translation?.IsLocked == true
                };
            }).ToList();

            var queueStats = await _translationQueue.GetStatsAsync(language.Code, HttpContext.RequestAborted);
            var missingCount = Math.Max(0, queueStats.Total - queueStats.Completed);
            return View(new AdminTranslationsViewModel
            {
                Language = language,
                Items = items,
                Query = q,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                MissingCount = missingCount,
                CanAutoTranslate = !isSourceLanguage && _machineTranslation.IsEnabledFor(language.Code),
                QueuePending = queueStats.Pending,
                QueueProcessing = queueStats.Processing,
                QueueCompleted = queueStats.Completed,
                QueueFailed = queueStats.Failed,
                QueueProgressPercent = queueStats.ProgressPercent,
                MatchedProductId = matchedProductId,
                MatchedProductName = matchedProductName,
                MatchedProductDescription = matchedProductDescription
            });
        }

        [HttpPost("{code}/Translations/Auto")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AutoTranslate(string code, string? q = null, int page = 1)
        {
            var language = await _db.SiteLanguages.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
            if (language == null)
                return NotFound();

            // Admin "Tüm Eksikleri" gerçek envanteri önce yeniden tarar. Böylece ürün/
            // kategori/reklam gibi DB içerikleri güncellendiyse eski kaynak listesine bağlı kalmaz.
            await _translationQueue.IndexKnownDynamicContentAsync(HttpContext.RequestAborted);
            var queued = await _translationQueue.QueueAllMissingForLanguageAsync(language.Code, HttpContext.RequestAborted);
            if (_machineTranslation.IsEnabledFor(language.Code))
                _translationQueueSignal.Pulse(language.Code);
            var providerText = _machineTranslation.IsEnabledFor(language.Code)
                ? "Arka plan çeviri işçisi kuyruğu kontrollü partiler halinde işleyecek."
                : "Otomatik çeviri sağlayıcısı etkin değil; işler kuyrukta güvenle bekleyecek.";
            var queueStats = await _translationQueue.GetStatsAsync(language.Code, HttpContext.RequestAborted);
            TempData["Success"] = $"{queued} yeni çeviri işi oluşturuldu. Toplam {queueStats.Pending} metin bekliyor, {queueStats.Completed}/{queueStats.Total} metin hazır. {providerText}";
            return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
        }

        [HttpPost("{code}/Translations/ApplyGuides")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyGuideTranslations(string code, string? q = null, int page = 1)
        {
            var language = await _db.SiteLanguages.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code == code, HttpContext.RequestAborted);
            if (language == null)
                return NotFound();

            if (!string.Equals(language.Code, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Yerel Rehber çeviri paketi şu anda yalnızca İngilizce için hazırlanmıştır.";
                return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
            }

            var result = await _guideTranslations.ApplyEnglishAsync(HttpContext.RequestAborted);
            TempData["Success"] = $"Rehber İngilizce paketi mevcut çeviri motoruna yerel olarak uygulandı. {result.Added} yeni çeviri eklendi, {result.Updated} eksik/eski otomatik çeviri düzeltildi, {result.Preserved} mevcut onaylı/manuel çeviri korundu. İnternet veya harici çeviri API'si kullanılmadı.";
            return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
        }

        [HttpPost("{code}/Translations/RetryFailed")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RetryFailed(string code, string? q = null, int page = 1)
        {
            var language = await _db.SiteLanguages.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
            if (language == null)
                return NotFound();

            var count = await _translationQueue.RetryFailedAsync(language.Code, HttpContext.RequestAborted);
            if (_machineTranslation.IsEnabledFor(language.Code))
                _translationQueueSignal.Pulse(language.Code);
            TempData["Success"] = $"{count} hatalı çeviri yeniden kuyruğa alındı.";
            return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
        }

        [HttpPost("{code}/Translations/SaveProductDescription")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveProductDescription(
            string code,
            int productId,
            string? value,
            string? q = null)
        {
            var language = await _db.SiteLanguages.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code == code, HttpContext.RequestAborted);
            if (language == null)
                return NotFound();

            var product = await _db.Products.AsNoTracking()
                .Where(x => x.Id == productId && !x.IsDeleted && x.Description != null && x.Description != "")
                .Select(x => new { x.Id, x.Name, x.Description })
                .FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (product == null)
                return NotFound();

            var fullSource = ProductContentSanitizer.ToPlainText(product.Description);
            if (string.IsNullOrWhiteSpace(fullSource))
            {
                TempData["Error"] = "Ürünün tam açıklama kaynağı oluşturulamadı.";
                return RedirectToAction(nameof(Translations), new { code = language.Code, q = product.Name, productId = product.Id, page = 1 });
            }

            var sourceKey = await EnsureFullProductDescriptionResourceAsync(
                product.Id,
                fullSource,
                HttpContext.RequestAborted);

            if (IsSourceLanguage(language.Code))
            {
                var sourceValue = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sourceValue))
                {
                    TempData["Error"] = "Ana Türkçe kaynak metin boş bırakılamaz.";
                    return RedirectToAction(nameof(Translations), new { code = language.Code, q = product.Name, productId = product.Id, page = 1 });
                }

                var sourceUpdate = await _translationQueue.UpdateSourceTextAsync(
                    sourceKey,
                    sourceValue,
                    HttpContext.RequestAborted);

                var savedSource = await _db.LocalizationResources.AsNoTracking()
                    .Where(x => x.SourceKey == sourceKey)
                    .Select(x => x.SourceText)
                    .FirstOrDefaultAsync(HttpContext.RequestAborted);
                if (!string.Equals(savedSource ?? string.Empty, sourceValue, StringComparison.Ordinal))
                {
                    TempData["Error"] = "Ana Türkçe ürün açıklaması veritabanına doğrulanmış şekilde kaydedilemedi.";
                }
                else if (!sourceUpdate.Changed)
                {
                    TempData["Success"] = $"{product.Name} ana Türkçe açıklamasında değişiklik yok; mevcut çeviriler korundu.";
                }
                else if (sourceUpdate.MachineTranslationAllowed)
                {
                    TempData["Success"] = $"{product.Name} ana Türkçe açıklaması güncellendi. {sourceUpdate.InvalidatedTranslations} eski hedef çeviri geçersizleştirildi, {sourceUpdate.QueuedJobs} iş güncel kaynakla yeniden kuyruğa alındı.";
                }
                else
                {
                    TempData["Success"] = $"{product.Name} ana Türkçe açıklaması güncellendi. {sourceUpdate.InvalidatedTranslations} eski hedef çeviri geçersizleştirildi; bu alan güvenlik kuralı nedeniyle makine çeviri kuyruğuna gönderilmedi.";
                }

                return RedirectToAction(nameof(Translations), new
                {
                    code = language.Code,
                    q = product.Name,
                    productId = product.Id,
                    page = 1
                });
            }

            var normalizedValue = LocalizationDisplayNormalizer.Normalize((value ?? string.Empty).Trim(), language.Code);
            var now = DateTime.Now;
            var translation = await _db.LocalizationTranslations
                .FirstOrDefaultAsync(x => x.SourceKey == sourceKey && x.LanguageCode == language.Code, HttpContext.RequestAborted);
            if (translation == null)
            {
                translation = new LocalizationTranslation
                {
                    SourceKey = sourceKey,
                    LanguageCode = language.Code
                };
                _db.LocalizationTranslations.Add(translation);
            }

            translation.Value = normalizedValue;
            translation.IsMachineTranslated = false;
            translation.IsReviewed = !string.IsNullOrWhiteSpace(normalizedValue);
            translation.IsLocked = !string.IsNullOrWhiteSpace(normalizedValue);
            translation.UpdatedAt = now;

            var job = await _db.LocalizationTranslationJobs
                .FirstOrDefaultAsync(x => x.SourceKey == sourceKey && x.LanguageCode == language.Code, HttpContext.RequestAborted);
            if (job == null)
            {
                job = new LocalizationTranslationJob
                {
                    SourceKey = sourceKey,
                    LanguageCode = language.Code,
                    CreatedAt = now
                };
                _db.LocalizationTranslationJobs.Add(job);
            }

            job.Status = string.IsNullOrWhiteSpace(normalizedValue) ? "Pending" : "Completed";
            job.CompletedAt = string.IsNullOrWhiteSpace(normalizedValue) ? null : now;
            job.LastError = null;
            job.NextAttemptAt = null;
            job.UpdatedAt = now;

            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            _catalog.InvalidateTranslations(language.Code);

            // Verify the exact LONGTEXT value before reporting success. This makes a silent
            // failed/truncated save impossible: the admin sees an error instead of a false success.
            var savedValue = await _db.LocalizationTranslations.AsNoTracking()
                .Where(x => x.SourceKey == sourceKey && x.LanguageCode == language.Code)
                .Select(x => x.Value)
                .FirstOrDefaultAsync(HttpContext.RequestAborted);

            if (!string.Equals(savedValue ?? string.Empty, normalizedValue, StringComparison.Ordinal))
            {
                TempData["Error"] = "Tam ürün açıklaması veritabanına doğrulanmış şekilde kaydedilemedi. Kayıt geri okunamadı.";
            }
            else
            {
                TempData["Success"] = $"{product.Name} tam {language.NativeName} açıklaması kaydedildi ve doğrulandı.";
            }

            return RedirectToAction(nameof(Translations), new
            {
                code = language.Code,
                q = product.Name,
                productId = product.Id,
                page = 1
            });
        }

        private async Task<string> EnsureFullProductDescriptionResourceAsync(
            int productId,
            string fullSource,
            CancellationToken cancellationToken)
        {
            var normalizedSource = LocalizationTextKey.Normalize(fullSource);
            var sourceKey = LocalizationTextKey.Create(normalizedSource);
            if (sourceKey.Length == 0)
                return string.Empty;

            var now = DateTime.Now;
            var expectedPath = $"/Public/Product/{productId}/DescriptionFull";
            var resource = await _db.LocalizationResources
                .FirstOrDefaultAsync(x => x.SourceKey == sourceKey, cancellationToken);

            if (resource == null)
            {
                _db.LocalizationResources.Add(new LocalizationResource
                {
                    SourceKey = sourceKey,
                    SourceText = normalizedSource,
                    FirstSeenPath = expectedPath,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    HitCount = 1,
                    IsIgnored = false
                });
            }
            else
            {
                // SourceKey is the permanent identity. Once the Turkish source editor has
                // changed SourceText, rediscovering the original product HTML must not undo
                // that canonical source revision. Only discovery metadata is refreshed here.
                resource.FirstSeenPath = expectedPath;
                resource.LastSeenAt = now;
                resource.IsIgnored = false;
            }

            await _db.SaveChangesAsync(cancellationToken);
            return sourceKey;
        }

        [HttpPost("{code}/Translations/SaveMany")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveManyTranslations(
            string code,
            string[] sourceKeys,
            string[] values,
            string? q = null,
            int page = 1)
        {
            var language = await _db.SiteLanguages.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
            if (language == null)
                return NotFound();

            sourceKeys ??= Array.Empty<string>();
            values ??= Array.Empty<string>();
            var count = Math.Min(sourceKeys.Length, values.Length);
            if (count == 0)
            {
                TempData["Error"] = "Kaydedilecek çeviri bulunamadı.";
                return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
            }

            var keys = sourceKeys.Take(count)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var validKeys = (await _db.LocalizationResources.AsNoTracking()
                    .Where(x => keys.Contains(x.SourceKey))
                    .Select(x => x.SourceKey)
                    .ToListAsync())
                .ToHashSet(StringComparer.Ordinal);

            if (IsSourceLanguage(language.Code))
            {
                var changed = 0;
                var invalidated = 0;
                var queued = 0;
                for (var i = 0; i < count; i++)
                {
                    var sourceKey = sourceKeys[i];
                    var sourceValue = (values[i] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(sourceKey)
                        || !validKeys.Contains(sourceKey)
                        || string.IsNullOrWhiteSpace(sourceValue))
                    {
                        continue;
                    }

                    var result = await _translationQueue.UpdateSourceTextAsync(
                        sourceKey,
                        sourceValue,
                        HttpContext.RequestAborted);
                    if (!result.Changed)
                        continue;

                    changed++;
                    invalidated += result.InvalidatedTranslations;
                    queued += result.QueuedJobs;
                }

                TempData["Success"] = $"{changed} ana Türkçe kaynak güncellendi; {invalidated} eski hedef çeviri geçersizleştirildi ve {queued} yeni çeviri işi kuyruğa bağlandı.";
                return RedirectToAction(nameof(Translations), new { code = language.Code, q, page = 1 });
            }

            var translations = (await _db.LocalizationTranslations
                    .Where(x => x.LanguageCode == language.Code && keys.Contains(x.SourceKey))
                    .ToListAsync())
                .ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
            var jobs = (await _db.LocalizationTranslationJobs
                    .Where(x => x.LanguageCode == language.Code && keys.Contains(x.SourceKey))
                    .ToListAsync())
                .ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
            var now = DateTime.Now;
            var saved = 0;

            for (var i = 0; i < count; i++)
            {
                var sourceKey = sourceKeys[i];
                if (string.IsNullOrWhiteSpace(sourceKey) || !validKeys.Contains(sourceKey))
                    continue;

                var value = LocalizationDisplayNormalizer.Normalize((values[i] ?? string.Empty).Trim(), language.Code);
                if (!translations.TryGetValue(sourceKey, out var translation))
                {
                    translation = new LocalizationTranslation
                    {
                        SourceKey = sourceKey,
                        LanguageCode = language.Code
                    };
                    _db.LocalizationTranslations.Add(translation);
                    translations[sourceKey] = translation;
                }

                translation.Value = value;
                translation.IsMachineTranslated = false;
                translation.IsReviewed = !string.IsNullOrWhiteSpace(value);
                translation.IsLocked = !string.IsNullOrWhiteSpace(value);
                translation.UpdatedAt = now;

                if (!jobs.TryGetValue(sourceKey, out var job))
                {
                    job = new LocalizationTranslationJob
                    {
                        SourceKey = sourceKey,
                        LanguageCode = language.Code,
                        CreatedAt = now
                    };
                    _db.LocalizationTranslationJobs.Add(job);
                    jobs[sourceKey] = job;
                }

                job.Status = string.IsNullOrWhiteSpace(value) ? "Pending" : "Completed";
                job.CompletedAt = string.IsNullOrWhiteSpace(value) ? null : now;
                job.LastError = null;
                job.NextAttemptAt = null;
                job.UpdatedAt = now;
                saved++;
            }

            await _db.SaveChangesAsync();
            _catalog.InvalidateTranslations(language.Code);
            TempData["Success"] = $"Ürün açıklamasındaki {saved} çeviri parçası tek işlemde kaydedildi.";
            return RedirectToAction(nameof(Translations), new { code = language.Code, q, page = 1 });
        }

        [HttpPost("{code}/Translations/Save")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTranslation(string code, string sourceKey, string? value, string? q = null, int page = 1)
        {
            var language = await _db.SiteLanguages.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
            if (language == null)
                return NotFound();

            if (!await _db.LocalizationResources.AnyAsync(x => x.SourceKey == sourceKey))
                return NotFound();

            if (IsSourceLanguage(language.Code))
            {
                var sourceValue = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sourceValue))
                {
                    TempData["Error"] = "Ana Türkçe kaynak metin boş bırakılamaz.";
                    return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
                }

                var sourceUpdate = await _translationQueue.UpdateSourceTextAsync(
                    sourceKey,
                    sourceValue,
                    HttpContext.RequestAborted);

                if (!sourceUpdate.Changed)
                {
                    TempData["Success"] = "Ana Türkçe kaynakta değişiklik yok; mevcut hedef çeviriler korundu.";
                }
                else if (sourceUpdate.MachineTranslationAllowed)
                {
                    TempData["Success"] = $"Ana Türkçe kaynak güncellendi. {sourceUpdate.InvalidatedTranslations} eski hedef çeviri geçersizleştirildi, {sourceUpdate.QueuedJobs} iş güncel kaynakla yeniden kuyruğa alındı.";
                }
                else
                {
                    TempData["Success"] = $"Ana Türkçe kaynak güncellendi. {sourceUpdate.InvalidatedTranslations} eski hedef çeviri geçersizleştirildi; güvenlik kuralı nedeniyle bu metin makine çevirisine gönderilmedi.";
                }

                return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
            }

            value = (value ?? string.Empty).Trim();
            value = LocalizationDisplayNormalizer.Normalize(value, language.Code);
            var translation = await _db.LocalizationTranslations.FirstOrDefaultAsync(x => x.SourceKey == sourceKey && x.LanguageCode == language.Code);
            if (translation == null)
            {
                translation = new LocalizationTranslation
                {
                    SourceKey = sourceKey,
                    LanguageCode = language.Code
                };
                _db.LocalizationTranslations.Add(translation);
            }

            translation.Value = value;
            translation.IsMachineTranslated = false;
            translation.IsReviewed = !string.IsNullOrWhiteSpace(value);
            translation.IsLocked = !string.IsNullOrWhiteSpace(value);
            translation.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();

            var job = await _db.LocalizationTranslationJobs
                .FirstOrDefaultAsync(x => x.SourceKey == sourceKey && x.LanguageCode == language.Code);
            if (job == null)
            {
                job = new LocalizationTranslationJob
                {
                    SourceKey = sourceKey,
                    LanguageCode = language.Code,
                    CreatedAt = DateTime.Now
                };
                _db.LocalizationTranslationJobs.Add(job);
            }

            job.Status = string.IsNullOrWhiteSpace(value) ? "Pending" : "Completed";
            job.CompletedAt = string.IsNullOrWhiteSpace(value) ? null : DateTime.Now;
            job.LastError = null;
            job.NextAttemptAt = null;
            job.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
            _catalog.InvalidateTranslations(language.Code);

            TempData["Success"] = "Çeviri kaydedildi.";
            return RedirectToAction(nameof(Translations), new { code = language.Code, q, page });
        }
        private static bool IsSourceLanguage(string? languageCode)
            => !string.IsNullOrWhiteSpace(languageCode)
               && string.Equals(languageCode, "tr-TR", StringComparison.OrdinalIgnoreCase);

        private static (string Html, string PlainText)? FindMatchingDescriptionBlock(string? html, string query)
        {
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(query))
                return null;

            var normalizedHtml = ProductContentSanitizer.NormalizeForEditor(html);
            var blocks = Regex.Matches(
                normalizedHtml,
                @"<(?<tag>p|h2|h3|h4|li|blockquote|caption|th|td)\b[^>]*>(?<content>.*?)</\k<tag>>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

            foreach (Match block in blocks)
            {
                var blockHtml = block.Groups["content"].Value;
                var plain = ProductContentSanitizer.ToPlainText(blockHtml);
                if (ContainsSearchText(plain, query))
                    return (blockHtml, plain);
            }

            var fullPlain = ProductContentSanitizer.ToPlainText(normalizedHtml);
            return ContainsSearchText(fullPlain, query)
                ? (normalizedHtml, fullPlain)
                : null;
        }

        private static IEnumerable<string> ExtractLocalizationSegments(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                yield break;

            var value = html.Trim();
            if (!value.Contains('<'))
            {
                var plain = LocalizationTextKey.Normalize(value);
                if (!string.IsNullOrWhiteSpace(plain))
                    yield return plain;
                yield break;
            }

            foreach (Match match in Regex.Matches(
                value,
                @"(?:^|>)(?<text>[^<]+)(?=<|$)",
                RegexOptions.CultureInvariant))
            {
                var text = LocalizationTextKey.Normalize(System.Net.WebUtility.HtmlDecode(match.Groups["text"].Value));
                if (!string.IsNullOrWhiteSpace(text) && text.Any(char.IsLetter))
                    yield return text;
            }
        }

        private static bool ContainsSearchText(string source, string query)
        {
            var normalizedSource = LocalizationTextKey.Normalize(source);
            var normalizedQuery = LocalizationTextKey.Normalize(query);
            if (normalizedQuery.Length == 0)
                return false;

            if (normalizedSource.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            // Admin may paste text copied from the English public page while that page is
            // only partially translated (for example "New NSX Cloud ve Mobil..."), and
            // inline <strong>/<em> formatting may appear as **markdown** in the clipboard.
            // Search the visible words with a high-overlap score instead of requiring every
            // token to be byte-for-byte identical. This keeps long product-description
            // searches precise while tolerating a few translated words, NBSP and punctuation.
            var sourceTokens = GetSearchTokens(normalizedSource);
            var queryTokens = GetSearchTokens(normalizedQuery);
            if (sourceTokens.Count == 0 || queryTokens.Count == 0)
                return false;

            var matched = queryTokens.Count(token => sourceTokens.Contains(token));
            if (queryTokens.Count <= 4)
                return matched == queryTokens.Count;

            var coverage = matched / (double)queryTokens.Count;
            if (coverage >= 0.78d)
                return true;

            // Long copied paragraphs can contain several already-translated UI words.
            // Requiring at least 12 shared meaningful words and 65% coverage is still
            // selective enough to avoid matching an unrelated product paragraph.
            return queryTokens.Count >= 12 && matched >= 12 && coverage >= 0.65d;
        }

        private static string NormalizeAdminSearchQuery(string? value)
        {
            var normalized = LocalizationTextKey.Normalize(System.Net.WebUtility.HtmlDecode(value ?? string.Empty));
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            normalized = Regex.Replace(normalized, @"<[^>]+>", " ", RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"[\*_~`]+", string.Empty, RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
            return normalized;
        }

        private static HashSet<string> GetSearchTokens(string value)
        {
            return Regex.Matches(value, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(x => NormalizeSearchToken(x.Value))
                .Where(x => x.Length >= 2)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeSearchToken(string value)
        {
            // Fold common clipboard/localization variants so a partially translated public
            // sentence can still resolve back to its Turkish source description.
            var token = value.Trim().ToLowerInvariant();
            return token switch
            {
                "new" => "yeni",
                "and" => "ve",
                "mobile" => "mobil",
                "management" => "yönetim",
                "cloud" => "cloud",
                _ => token
            };
        }

        private static string NormalizeLanguageCode(string? value)
        {
            var raw = (value ?? string.Empty).Trim().Replace('_', '-');
            if (raw.Length == 0)
                return string.Empty;

            var parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return string.Empty;

            parts[0] = parts[0].ToLowerInvariant();
            for (var i = 1; i < parts.Length; i++)
            {
                parts[i] = parts[i].Length == 4
                    ? char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant()
                    : parts[i].ToUpperInvariant();
            }
            return string.Join('-', parts);
        }

        private static string NormalizeFlagEmoji(string? value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;

            if (raw.Length == 2 && raw.All(char.IsLetter))
            {
                var countryCode = raw.ToUpperInvariant();
                if (countryCode.All(c => c is >= 'A' and <= 'Z'))
                {
                    return string.Concat(countryCode.Select(c =>
                        char.ConvertFromUtf32(0x1F1E6 + (c - 'A'))));
                }
            }

            return raw;
        }

        private static string NormalizeUrlCode(string? value)
            => (value ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();

    }
}
