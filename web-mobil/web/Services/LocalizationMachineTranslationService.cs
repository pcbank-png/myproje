using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Completes translations for live DB content (product descriptions, guides, campaigns,
    /// videos and later admin-added content). Results are persisted in LocalizationTranslations,
    /// so a source text is translated once and then served from the local DB cache.
    /// </summary>
    public sealed class LocalizationMachineTranslationService
    {
        private readonly HttpClient _httpClient;
        private readonly ApplicationDbContext _db;
        private readonly LocalizationCatalog _catalog;
        private readonly LocalizationMachineTranslationOptions _options;
        private readonly ILogger<LocalizationMachineTranslationService> _logger;
        private readonly ConcurrentDictionary<string, int> _providerFailures = new(StringComparer.OrdinalIgnoreCase);
        private static long _myMemoryCooldownUntilUtcTicks;
        private static long _freeProviderCooldownUntilUtcTicks;

        public LocalizationMachineTranslationService(
            HttpClient httpClient,
            ApplicationDbContext db,
            LocalizationCatalog catalog,
            IOptions<LocalizationMachineTranslationOptions> options,
            ILogger<LocalizationMachineTranslationService> logger)
        {
            _httpClient = httpClient;
            _db = db;
            _catalog = catalog;
            _options = options.Value;
            _logger = logger;
        }

        public bool IsEnabledFor(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode)
                || languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                return false;

            var provider = GetEffectiveProvider();
            if (provider.Equals("FreeCascade", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("GoogleFree", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("MyMemory", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return _options.IsConfigured;
        }

        private string GetEffectiveProvider()
        {
            if (!_options.Enabled)
                return "Disabled";

            var provider = (_options.Provider ?? string.Empty).Trim();

            if (_options.Enabled
                && (provider.Equals("FreeCascade", StringComparison.OrdinalIgnoreCase)
                    || provider.Equals("GoogleFree", StringComparison.OrdinalIgnoreCase)
                    || provider.Equals("MyMemory", StringComparison.OrdinalIgnoreCase)))
                return provider;

            if (_options.Enabled
                && !string.IsNullOrWhiteSpace(_options.ApiKey)
                && (provider.Equals("GoogleCloud", StringComparison.OrdinalIgnoreCase)
                    || provider.Equals("DeepL", StringComparison.OrdinalIgnoreCase)))
                return provider;

            return "Disabled";
        }

        public async Task<Dictionary<string, string>> TranslateMissingAsync(
            IEnumerable<KeyValuePair<string, string>> sourceItems,
            string languageCode,
            IReadOnlyDictionary<string, string>? nativeTranslations = null,
            string? sourcePath = null,
            CancellationToken cancellationToken = default)
        {
            _providerFailures.Clear();
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!IsEnabledFor(languageCode))
                return output;

            var isEnglish = languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            var candidates = sourceItems
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => new KeyValuePair<string, string>(x.Key.Trim(), LocalizationTextKey.Normalize(x.Value)))
                .Where(x => IsMachineTranslationCandidate(x.Value))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Last())
                .Where(item =>
                {
                    if (nativeTranslations == null)
                        return true;

                    if (nativeTranslations.TryGetValue(item.Key, out var byStableKey)
                        && !string.IsNullOrWhiteSpace(byStableKey)
                        && (!isEnglish || !LooksLikeTurkishText(byStableKey)))
                    {
                        return false;
                    }

                    var currentTextKey = LocalizationTextKey.Create(item.Value);
                    return !nativeTranslations.TryGetValue(currentTextKey, out var byCurrentText)
                           || string.IsNullOrWhiteSpace(byCurrentText)
                           || (isEnglish && LooksLikeTurkishText(byCurrentText));
                })
                .ToList();

            if (candidates.Count == 0)
                return output;

            try
            {
                // Stable SourceKey is the identity. SourceText may change in the Turkish
                // source editor without changing this key, so every durable read/write is
                // keyed by SourceKey rather than by a new hash of the edited text.
                var keys = candidates.Select(x => x.Key).Distinct(StringComparer.Ordinal).ToArray();
                var existingRows = await _db.LocalizationTranslations
                    .AsNoTracking()
                    .Where(x => keys.Contains(x.SourceKey) && x.LanguageCode == languageCode && x.Value != "")
                    .Select(x => new { x.SourceKey, x.Value, x.IsReviewed, x.IsLocked })
                    .ToListAsync(cancellationToken);

                foreach (var row in existingRows)
                {
                    if (!isEnglish
                        || (row.IsReviewed && row.IsLocked)
                        || !LooksLikeTurkishText(row.Value))
                    {
                        output[row.SourceKey] = row.Value;
                    }
                }

                var existingKeys = existingRows
                    .Where(x => !isEnglish
                        || (x.IsReviewed && x.IsLocked)
                        || !LooksLikeTurkishText(x.Value))
                    .Select(x => x.SourceKey)
                    .ToHashSet(StringComparer.Ordinal);
                candidates = candidates.Where(x => !existingKeys.Contains(x.Key)).ToList();
                if (candidates.Count == 0)
                    return output;

                var remainingKeys = candidates.Select(x => x.Key).Distinct(StringComparer.Ordinal).ToArray();
                var resourceKeyList = await _db.LocalizationResources
                    .AsNoTracking()
                    .Where(x => remainingKeys.Contains(x.SourceKey))
                    .Select(x => x.SourceKey)
                    .ToListAsync(cancellationToken);
                var resourceKeys = resourceKeyList.ToHashSet(StringComparer.Ordinal);

                var translationRows = await _db.LocalizationTranslations
                    .Where(x => remainingKeys.Contains(x.SourceKey) && x.LanguageCode == languageCode)
                    .ToListAsync(cancellationToken);
                var translationByKey = translationRows
                    .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

                foreach (var batch in CreateSourceBatches(candidates))
                {
                    var batchTexts = batch.Select(x => PrepareSourceForMachineTranslation(x.Value)).ToList();
                    var translated = await TranslateBatchAsync(batchTexts, languageCode, cancellationToken);
                    if (translated.Count != batch.Count)
                        continue;

                    var now = DateTime.Now;
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var sourceKey = batch[i].Key;
                        var source = batch[i].Value;
                        var target = LocalizationTextKey.Normalize(WebUtility.HtmlDecode(translated[i] ?? string.Empty));
                        if (isEnglish)
                            target = NormalizeEnglishMachineOutput(target);
                        target = LocalizationDisplayNormalizer.Normalize(target, languageCode);
                        if (string.IsNullOrWhiteSpace(target))
                            continue;

                        var unchanged = string.Equals(source, target, StringComparison.Ordinal);
                        if (!languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
                            && !IsTurkicNearSourceLanguage(languageCode)
                            && LooksLikeUntranslatedTurkish(target))
                            continue;

                        // Free providers occasionally return the source unchanged. Previously
                        // only English rejected that result, so e.g. Italian could mark
                        // "Türkçe" as Completed even though the visible page stayed Turkish.
                        // For every non-Turkish target, unchanged Turkish source is a retry,
                        // except short NSX product/brand names which are intentionally stable.
                        if (unchanged
                            && !languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
                            && !IsTurkicNearSourceLanguage(languageCode)
                            && LooksLikeTurkishText(source)
                            && !IsLikelyNsxBrandName(source))
                        {
                            continue;
                        }

                        output[sourceKey] = target;

                        if (resourceKeys.Add(sourceKey))
                        {
                            _db.LocalizationResources.Add(new LocalizationResource
                            {
                                SourceKey = sourceKey,
                                SourceText = source,
                                FirstSeenPath = sourcePath,
                                FirstSeenAt = now,
                                LastSeenAt = now,
                                HitCount = 1
                            });
                        }

                        if (!translationByKey.TryGetValue(sourceKey, out var row))
                        {
                            row = new LocalizationTranslation
                            {
                                SourceKey = sourceKey,
                                LanguageCode = languageCode,
                                Value = target,
                                IsMachineTranslated = true,
                                IsReviewed = false,
                                IsLocked = false,
                                UpdatedAt = now
                            };
                            translationByKey[sourceKey] = row;
                            _db.LocalizationTranslations.Add(row);
                        }
                        else
                        {
                            var invalidEnglishRow = isEnglish
                                && !row.IsLocked
                                && !row.IsReviewed
                                && LooksLikeTurkishText(row.Value);
                            if ((!row.IsLocked && !row.IsReviewed) || invalidEnglishRow)
                            {
                                row.Value = target;
                                row.IsMachineTranslated = true;
                                if (invalidEnglishRow)
                                {
                                    row.IsReviewed = false;
                                    row.IsLocked = false;
                                }
                                row.UpdatedAt = now;
                            }
                        }
                    }

                    await _db.SaveChangesAsync(cancellationToken);
                }

                if (output.Count > 0)
                    _catalog.InvalidateTranslations(languageCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{LanguageCode} otomatik çeviri servisi başarısız oldu; kalıcı çeviri önbelleği korunuyor.", languageCode);
            }

            return output;
        }

        private List<List<KeyValuePair<string, string>>> CreateSourceBatches(List<KeyValuePair<string, string>> sourceItems)
        {
            var maxItems = Math.Clamp(_options.MaxTextsPerRequest, 1, 100);
            var maxChars = Math.Clamp(_options.MaxCharactersPerRequest, 2000, 100000);
            var batches = new List<List<KeyValuePair<string, string>>>();
            var current = new List<KeyValuePair<string, string>>();
            var chars = 0;

            foreach (var item in sourceItems)
            {
                if (current.Count > 0 && (current.Count >= maxItems || chars + item.Value.Length > maxChars))
                {
                    batches.Add(current);
                    current = new List<KeyValuePair<string, string>>();
                    chars = 0;
                }

                current.Add(item);
                chars += item.Value.Length;
            }

            if (current.Count > 0)
                batches.Add(current);

            return batches;
        }

        private async Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var provider = GetEffectiveProvider();
            if (provider.Equals("FreeCascade", StringComparison.OrdinalIgnoreCase))
                return await TranslateWithFreeCascadeAsync(texts, languageCode, cancellationToken);
            if (provider.Equals("GoogleFree", StringComparison.OrdinalIgnoreCase))
                return await TranslateWithGoogleFreeAsync(texts, languageCode, cancellationToken);
            if (provider.Equals("MyMemory", StringComparison.OrdinalIgnoreCase))
                return await TranslateWithMyMemoryAsync(texts, languageCode, cancellationToken);
            if (provider.Equals("DeepL", StringComparison.OrdinalIgnoreCase))
                return await TranslateWithDeepLAsync(texts, languageCode, cancellationToken);

            return await TranslateWithGoogleCloudAsync(texts, languageCode, cancellationToken);
        }

        private async Task<IReadOnlyList<string>> TranslateWithFreeCascadeAsync(
            IReadOnlyList<string> texts,
            string languageCode,
            CancellationToken cancellationToken)
        {
            // The old translate.googleapis.com and MyMemory free endpoints can both return
            // HTTP 429 for a shared hosting IP. The Chrome dictionary endpoint has an
            // independent quota, so FreeCascade starts there and retains the two existing
            // providers as fallbacks. Paid Google Cloud / DeepL behavior is unchanged.
            var primary = await TranslateWithGoogleClients5Async(texts, languageCode, cancellationToken);
            var output = primary.ToArray();
            var fallbackIndexes = GetFallbackIndexes(texts, output, languageCode);

            if (fallbackIndexes.Length == 0)
                return output;

            // When free Google/MyMemory already returned HTTP 429, do not dig the hole deeper.
            // Chrome dictionary (Clients5) remains the only attempt until cooldown expires.
            if (IsFreeProviderCoolingDown())
                return output;

            var fallbackSources = fallbackIndexes.Select(i => texts[i]).ToArray();
            var googleFallback = await TranslateWithGoogleFreeAsync(fallbackSources, languageCode, cancellationToken);
            ApplyFallback(output, fallbackIndexes, googleFallback, languageCode);

            fallbackIndexes = GetFallbackIndexes(texts, output, languageCode);
            if (fallbackIndexes.Length == 0 || IsFreeProviderCoolingDown())
                return output;

            fallbackSources = fallbackIndexes.Select(i => texts[i]).ToArray();
            var memoryFallback = await TranslateWithMyMemoryAsync(fallbackSources, languageCode, cancellationToken);
            ApplyFallback(output, fallbackIndexes, memoryFallback, languageCode);

            return output;
        }

        private static int[] GetFallbackIndexes(
            IReadOnlyList<string> texts,
            IReadOnlyList<string> output,
            string languageCode)
        {
            return Enumerable.Range(0, texts.Count)
                .Where(i => i >= output.Count
                            || string.IsNullOrWhiteSpace(output[i])
                            || (!languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
                                && !IsTurkicNearSourceLanguage(languageCode)
                                && LooksLikeUntranslatedTurkish(output[i]))
                            || (string.Equals(LocalizationTextKey.Normalize(output[i]), LocalizationTextKey.Normalize(texts[i]), StringComparison.Ordinal)
                                && LooksLikeTurkishText(texts[i])
                                && !languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
                                && !IsTurkicNearSourceLanguage(languageCode)))
                .ToArray();
        }

        private static void ApplyFallback(
            string[] output,
            IReadOnlyList<int> fallbackIndexes,
            IReadOnlyList<string> fallback,
            string languageCode)
        {
            for (var j = 0; j < fallbackIndexes.Count && j < fallback.Count; j++)
            {
                var value = LocalizationTextKey.Normalize(fallback[j]);
                if (!string.IsNullOrWhiteSpace(value)
                    && (languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
                        || IsTurkicNearSourceLanguage(languageCode)
                        || !LooksLikeUntranslatedTurkish(value)))
                {
                    output[fallbackIndexes[j]] = value;
                }
            }
        }

        private async Task<IReadOnlyList<string>> TranslateWithGoogleClients5Async(
            IReadOnlyList<string> texts,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var targetLanguage = ToGoogleLanguageCode(languageCode);

            // clients5 is the primary no-key provider. The canonical localization source is
            // Turkish, so short labels must not depend on automatic source-language detection.
            // Shared-hosting IPs are also safer with a single in-flight free request.
            var maxParallel = 1;
            using var gate = new SemaphoreSlim(maxParallel, maxParallel);
            var tasks = texts.Select(async text =>
            {
                if (targetLanguage == "en" && !LooksLikeTurkishText(text))
                    return text;

                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await TranslateGoogleClients5TextAsync(text, languageCode, cancellationToken) ?? string.Empty;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordProviderFailure("GoogleClients5", ex);
                    _logger.LogDebug(ex, "Google alternatif ücretsiz çeviri isteği başarısız oldu; diğer sağlayıcılar denenecek.");
                    return string.Empty;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            return await Task.WhenAll(tasks);
        }

        private async Task<string?> TranslateGoogleClients5TextAsync(
            string text,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var translatedChunks = new List<string>();
            foreach (var chunk in SplitForFreeProvider(text, 900))
            {
                var targetLanguage = ToGoogleLanguageCode(languageCode);
                var sourceLanguage = languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "auto" : "tr";
                var url = "https://clients5.google.com/translate_a/t"
                    + "?client=dict-chrome-ex&sl=" + Uri.EscapeDataString(sourceLanguage)
                    + "&tl=" + Uri.EscapeDataString(targetLanguage)
                    + "&q=" + Uri.EscapeDataString(chunk);

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(TimeSpan.FromSeconds(8));

                // dict-chrome-ex currently returns the Chrome dictionary JSON shape. A
                // browser-like request header also avoids the malformed/legacy response that
                // can be returned to generic HTTP clients. This header is scoped only to this
                // provider; the other translation providers keep the NSX client headers.
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
                request.Headers.Accept.ParseAdd("application/json,text/plain,*/*");

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCts.Token);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(requestCts.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestCts.Token);
                var value = ParseGoogleClients5Translation(document.RootElement);
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                translatedChunks.Add(WebUtility.HtmlDecode(value).Trim());
            }

            return translatedChunks.Count == 0 ? null : string.Join(" ", translatedChunks).Trim();
        }

        private static string? ParseGoogleClients5Translation(JsonElement root)
        {
            // Current dict-chrome-ex response:
            // { "sentences": [ { "trans": "...", "orig": "..." } ], ... }
            //
            // The previous NSX parser expected an old nested-array shape. Because that
            // mismatch returned null without throwing, the primary provider was silently
            // discarded and every remaining row fell through to GoogleFree/MyMemory, where
            // the shared Plesk IP was already HTTP 429 throttled. Support both shapes so an
            // upstream response variation cannot strand the queue again.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sentences", out var sentences)
                && sentences.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var sentence in sentences.EnumerateArray())
                {
                    if (sentence.ValueKind == JsonValueKind.Object
                        && sentence.TryGetProperty("trans", out var translated)
                        && translated.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(translated.GetString());
                    }
                }

                var currentValue = builder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(currentValue))
                    return currentValue;
            }

            // Backward compatibility for legacy array variants seen by some Google clients.
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var first = root[0];
                if (first.ValueKind == JsonValueKind.Array)
                {
                    var builder = new StringBuilder();
                    foreach (var sentence in first.EnumerateArray())
                    {
                        if (sentence.ValueKind == JsonValueKind.Array
                            && sentence.GetArrayLength() > 0
                            && sentence[0].ValueKind == JsonValueKind.String)
                        {
                            builder.Append(sentence[0].GetString());
                        }
                    }

                    var legacyValue = builder.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(legacyValue))
                        return legacyValue;
                }
            }

            return null;
        }

        private async Task<IReadOnlyList<string>> TranslateWithGoogleFreeAsync(
            IReadOnlyList<string> texts,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var targetLanguage = ToGoogleLanguageCode(languageCode);
            // Shared-hosting free endpoints are intentionally serialized to avoid 429 bursts.
            var maxParallel = 1;
            using var gate = new SemaphoreSlim(maxParallel, maxParallel);
            var tasks = texts.Select(async text =>
            {
                if (targetLanguage == "en" && !LooksLikeTurkishText(text))
                    return text;

                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await TranslateGoogleFreeTextAsync(text, languageCode, cancellationToken) ?? string.Empty;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordProviderFailure("GoogleFree", ex);
                    _logger.LogDebug(ex, "Ücretsiz Google çeviri isteği başarısız oldu; yedek sağlayıcı denenecek.");
                    return string.Empty;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            return await Task.WhenAll(tasks);
        }

        private async Task<string?> TranslateGoogleFreeTextAsync(string text, string languageCode, CancellationToken cancellationToken)
        {
            var provider = GetEffectiveProvider();
            var endpoint = provider.Equals("GoogleFree", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(_options.Endpoint)
                ? _options.Endpoint.Trim()
                : "https://translate.googleapis.com/translate_a/single";

            var translatedChunks = new List<string>();
            foreach (var chunk in SplitForFreeProvider(text, 1000))
            {
                var separator = endpoint.Contains('?') ? "&" : "?";
                var targetLanguage = ToGoogleLanguageCode(languageCode);
                var sourceLanguage = languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "auto" : "tr";
                var url = endpoint + separator
                    + "client=gtx&sl=" + Uri.EscapeDataString(sourceLanguage)
                    + "&tl=" + Uri.EscapeDataString(targetLanguage)
                    + "&dt=t&q=" + Uri.EscapeDataString(chunk);

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(TimeSpan.FromSeconds(6));
                using var response = await _httpClient.GetAsync(url, requestCts.Token);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(requestCts.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestCts.Token);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                    return null;

                var sentenceArray = document.RootElement[0];
                if (sentenceArray.ValueKind != JsonValueKind.Array)
                    return null;

                var builder = new StringBuilder();
                foreach (var item in sentenceArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0 && item[0].ValueKind == JsonValueKind.String)
                        builder.Append(item[0].GetString());
                }

                var value = WebUtility.HtmlDecode(builder.ToString()).Trim();
                if (string.IsNullOrWhiteSpace(value))
                    return null;
                translatedChunks.Add(value);
            }

            return translatedChunks.Count == 0 ? null : string.Join(" ", translatedChunks).Trim();
        }

        private async Task<IReadOnlyList<string>> TranslateWithMyMemoryAsync(
            IReadOnlyList<string> texts,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var targetLanguage = ToGoogleLanguageCode(languageCode);
            // MyMemory is the last fallback. On shared hosting its anonymous quota can be
            // exhausted by another tenant on the same IP, so never fan out requests.
            var maxParallel = 1;
            using var gate = new SemaphoreSlim(maxParallel, maxParallel);
            var tasks = texts.Select(async text =>
            {
                var sourceLanguage = languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "auto" : "tr";
                if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
                    return text;

                if (IsMyMemoryCoolingDown())
                    return string.Empty;

                await gate.WaitAsync(cancellationToken);
                try
                {
                    if (IsMyMemoryCoolingDown())
                        return string.Empty;

                    var translatedChunks = new List<string>();
                    foreach (var chunk in SplitForUtf8Bytes(text, 450))
                    {
                        if (IsMyMemoryCoolingDown())
                            return string.Empty;

                        var value = await TranslateMyMemoryChunkAsync(chunk, sourceLanguage, targetLanguage, cancellationToken);
                        if (string.IsNullOrWhiteSpace(value))
                            return string.Empty;
                        translatedChunks.Add(value.Trim());
                    }
                    return string.Join(" ", translatedChunks).Trim();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordProviderFailure("MyMemory", ex);
                    _logger.LogDebug(ex, "MyMemory ücretsiz çeviri isteği başarısız oldu.");
                    return string.Empty;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            return await Task.WhenAll(tasks);
        }

        private async Task<string?> TranslateMyMemoryChunkAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        {
            var provider = GetEffectiveProvider();
            var endpoint = provider.Equals("MyMemory", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(_options.Endpoint)
                ? _options.Endpoint.Trim()
                : "https://api.mymemory.translated.net/get";
            var separator = endpoint.Contains('?') ? "&" : "?";
            var languagePair = Uri.EscapeDataString(sourceLanguage + "|" + targetLanguage);
            var url = endpoint + separator + "q=" + Uri.EscapeDataString(text) + "&langpair=" + languagePair;

            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await _httpClient.GetAsync(url, requestCts.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                ActivateMyMemoryCooldown(response);
                throw new HttpRequestException(
                    "MyMemory free quota is temporarily throttled.",
                    null,
                    response.StatusCode);
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(requestCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestCts.Token);

            if (!document.RootElement.TryGetProperty("responseData", out var responseData)
                || !responseData.TryGetProperty("translatedText", out var translatedText))
                return null;

            var value = WebUtility.HtmlDecode(translatedText.GetString() ?? string.Empty).Trim();
            if (value.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
                || value.Contains("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase))
                return null;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static bool IsFreeProviderCoolingDown()
        {
            var untilTicks = Interlocked.Read(ref _freeProviderCooldownUntilUtcTicks);
            return untilTicks > DateTime.UtcNow.Ticks;
        }

        public static TimeSpan? GetFreeProviderCooldownRemaining()
        {
            var untilTicks = Interlocked.Read(ref _freeProviderCooldownUntilUtcTicks);
            var remaining = new DateTime(untilTicks, DateTimeKind.Utc) - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }

        public static void ActivateFreeProviderCooldown(TimeSpan? delay = null)
        {
            var retryDelay = delay ?? TimeSpan.FromMinutes(60);
            retryDelay = TimeSpan.FromMinutes(Math.Clamp(retryDelay.TotalMinutes, 15, 180));
            Interlocked.Exchange(
                ref _freeProviderCooldownUntilUtcTicks,
                DateTime.UtcNow.Add(retryDelay).Ticks);
        }

        private static bool IsMyMemoryCoolingDown()
        {
            var untilTicks = Interlocked.Read(ref _myMemoryCooldownUntilUtcTicks);
            return untilTicks > DateTime.UtcNow.Ticks;
        }

        private static void ActivateMyMemoryCooldown(HttpResponseMessage response)
        {
            var retryDelay = TimeSpan.FromMinutes(20);
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            {
                retryDelay = delta;
            }
            else if (retryAfter?.Date is { } retryDate)
            {
                var calculated = retryDate - DateTimeOffset.UtcNow;
                if (calculated > TimeSpan.Zero)
                    retryDelay = calculated;
            }

            retryDelay = TimeSpan.FromMinutes(Math.Clamp(retryDelay.TotalMinutes, 5, 60));
            Interlocked.Exchange(
                ref _myMemoryCooldownUntilUtcTicks,
                DateTime.UtcNow.Add(retryDelay).Ticks);
        }

        public string? GetLastProviderFailureSummary()
        {
            if (_providerFailures.IsEmpty)
                return null;

            return string.Join("; ", _providerFailures
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.Key}: {x.Value} istek başarısız"));
        }

        private void RecordProviderFailure(string provider, Exception exception)
        {
            var reason = exception switch
            {
                HttpRequestException { StatusCode: not null } http => $"HTTP {(int)http.StatusCode.Value}",
                OperationCanceledException => "zaman aşımı",
                _ => exception.GetType().Name
            };
            _providerFailures.AddOrUpdate($"{provider} {reason}", 1, (_, count) => count + 1);

            if (exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                || reason.Contains("429", StringComparison.Ordinal))
            {
                ActivateFreeProviderCooldown(TimeSpan.FromMinutes(60));
            }
        }


        private static IEnumerable<string> SplitForUtf8Bytes(string text, int maxBytes)
        {
            text = LocalizationTextKey.Normalize(text);
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                yield return text;
                yield break;
            }

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new StringBuilder();
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current.ToString() + " " + word;
                if (current.Length > 0 && Encoding.UTF8.GetByteCount(candidate) > maxBytes)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (Encoding.UTF8.GetByteCount(word) > maxBytes)
                {
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }

                    var fragment = new StringBuilder();
                    foreach (var rune in word.EnumerateRunes())
                    {
                        var runeText = rune.ToString();
                        if (fragment.Length > 0
                            && Encoding.UTF8.GetByteCount(fragment.ToString()) + Encoding.UTF8.GetByteCount(runeText) > maxBytes)
                        {
                            yield return fragment.ToString();
                            fragment.Clear();
                        }
                        fragment.Append(runeText);
                    }
                    if (fragment.Length > 0)
                        yield return fragment.ToString();
                    continue;
                }

                if (current.Length > 0)
                    current.Append(' ');
                current.Append(word);
            }

            if (current.Length > 0)
                yield return current.ToString();
        }

        private static IEnumerable<string> SplitForFreeProvider(string text, int maxLength)
        {
            text = LocalizationTextKey.Normalize(text);
            if (text.Length <= maxLength)
            {
                yield return text;
                yield break;
            }

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new StringBuilder();
            foreach (var word in words)
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > maxLength)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (current.Length > 0)
                    current.Append(' ');
                current.Append(word);
            }

            if (current.Length > 0)
                yield return current.ToString();
        }

        private async Task<IReadOnlyList<string>> TranslateWithGoogleCloudAsync(
            IReadOnlyList<string> texts,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint)
                ? "https://translation.googleapis.com/language/translate/v2"
                : _options.Endpoint.Trim();
            var target = ToGoogleLanguageCode(languageCode);
            var payload = JsonSerializer.Serialize(new
            {
                q = texts,
                target,
                format = "text"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _options.ApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return translations.EnumerateArray()
                .Select(x => x.TryGetProperty("translatedText", out var value) ? value.GetString() ?? string.Empty : string.Empty)
                .ToArray();
        }

        private async Task<IReadOnlyList<string>> TranslateWithDeepLAsync(
            IReadOnlyList<string> texts,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint)
                ? "https://api-free.deepl.com/v2/translate"
                : _options.Endpoint.Trim();

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _options.ApiKey);
            var fields = new List<KeyValuePair<string, string>>
            {
                new("target_lang", ToDeepLLanguageCode(languageCode)),
                new("preserve_formatting", "1")
            };
            fields.AddRange(texts.Select(x => new KeyValuePair<string, string>("text", x)));
            request.Content = new FormUrlEncodedContent(fields);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return translations.EnumerateArray()
                .Select(x => x.TryGetProperty("text", out var value) ? value.GetString() ?? string.Empty : string.Empty)
                .ToArray();
        }

        private static string PrepareSourceForMachineTranslation(string text)
        {
            // This public bank-transfer warning historically stalls on some free providers
            // because the compact "Havale/EFT" token is treated as a technical literal.
            // Keep the canonical Turkish UI/source key unchanged; only send an equivalent,
            // provider-friendly sentence while generating the target-language value.
            if (string.Equals(
                    LocalizationTextKey.Normalize(text),
                    "Lütfen Havale/EFT yaparken açıklama kısmını boş bırakınız.",
                    StringComparison.Ordinal))
            {
                return "Lütfen banka transferi yaparken açıklama veya referans alanını boş bırakınız.";
            }

            return text;
        }

        private static bool IsMachineTranslationCandidate(string text)
        {
            if (text.Length > 9000 || IsInvariantLocalizationText(text))
                return false;

            return true;
        }

        /// <summary>
        /// URLs, e-mail addresses and technical placeholders are language invariant.
        /// They must stay verbatim and must never occupy a translation queue slot that
        /// no provider is allowed to process.
        /// </summary>
        public static bool IsInvariantLocalizationText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var value = text.Trim();
            if (value.Length < 2 || !value.Any(char.IsLetter))
                return true;

            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                || (value.Contains('@') && !value.Contains(' '))
                || value.Contains("@@NSX_SKIP_", StringComparison.Ordinal);
        }

        private static string NormalizeEnglishMachineOutput(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            // Small safety glossary for domain terms that free translators occasionally
            // leave untouched. It is applied only to generated English output.
            var replacements = new (string Source, string Target)[]
            {
                ("NSX Yazılım", "NSX Software"),
                ("Kasa Defteri", "Cash Book"),
                ("Okul Plan", "School Plan"),
                ("Barkodlu Satış", "Barcode Sales"),
                ("Sigorta Acente", "Insurance Agency"),
                ("Teknik Servis", "Technical Service"),
                ("Oto Tamir", "Auto Repair"),
                ("Oto Galeri", "Auto Dealership"),
                ("Düğün Salonu", "Wedding Venue"),
                ("Veresiye", "Receivables"),
                ("Cari", "Account"),
                ("Tahsilat", "Collection"),
                ("Stok", "Stock"),
                ("Gider", "Expense"),
                ("Klinik", "Clinic"),
                ("Randevu", "Appointment")
            };

            var result = value;
            foreach (var pair in replacements)
                result = result.Replace(pair.Source, pair.Target, StringComparison.OrdinalIgnoreCase);
            return result;
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

            var wordCount = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return wordCount <= 6;
        }

        public static bool LooksLikeUntranslatedTurkish(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var inspected = text
                .Replace("Nevzat SÜRÜCÜ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Aynur SÜRÜCÜ", string.Empty, StringComparison.OrdinalIgnoreCase);

            if (inspected.IndexOfAny(new[] { 'ğ', 'Ğ', 'ı', 'İ', 'ş', 'Ş' }) >= 0)
                return true;

            var normalized = " " + LocalizationTextKey.Normalize(inspected).ToLowerInvariant() + " ";
            string[] strongMarkers =
            {
                " için ", " ile ", " veya ", " müşteri ", " musteri ", " yazılım ", " yazilim ",
                " ödeme ", " odeme ", " ürün ", " urun ", " tahsilat ", " borç ", " borc ",
                " alacak ", " kullanıcı ", " kullanici ", " yönetim ", " yonetim ", " kayıt ",
                " kayit ", " rapor ", " yedekleme ", " hatırlatma ", " hatirlatma ", " ücretsiz ",
                " ucretsiz ", " güvenli ", " guvenli ", " kurulum ", " sipariş ", " siparis ",
                " türkçe ", " turkce ", " ingilizce ", " almanca ", " ispanyolca ", " fransızca ",
                " fransizca ", " italyanca ", " rusça ", " rusca ",
                " düğün ", " dugun ", " poliçe ", " derslik ", " öğretmen ", " ogretmen "
            };
            return strongMarkers.Any(normalized.Contains);
        }

        public static bool LooksLikeTurkishText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Author names are proper nouns, not untranslated UI/content. Remove them
            // before language-leak detection so English copyright labels stay stable.
            var inspected = text
                .Replace("Nevzat SÜRÜCÜ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Aynur SÜRÜCÜ", string.Empty, StringComparison.OrdinalIgnoreCase);

            if (inspected.IndexOfAny(new[] { 'ç', 'Ç', 'ğ', 'Ğ', 'ı', 'İ', 'ö', 'Ö', 'ş', 'Ş', 'ü', 'Ü' }) >= 0)
                return true;

            var normalized = " " + LocalizationTextKey.Normalize(inspected).ToLowerInvariant() + " ";
            string[] markers =
            {
                " ve ", " için ", " ile ", " veya ", " bir ", " bu ", " şu ", " çok ", " daha ", " en ",
                " ürün ", " yazılım ", " lisans ", " müşteri ", " destek ", " ödeme ", " indirme ", " hesap ",
                " işlem ", " seç ", " görüntü ", " kurulum ", " teknik ", " bilgi ", " açıklama ",
                " yıllık ", " sınırsız ", " ömür ", " kullanım ", " satın ", " stok ", " teslimat ", " onay ",
                " güvenli ", " rehber ", " yorum ", " soru ", " cevap ", " havale ", " banka ", " kampanya ",
                " bilgisayar ", " performans ", " sistem ", " oyun ", " ekran ", " kart ", " hız ", " hızlı ",
                " yönetim ", " takip ", " kayıt ", " rapor ", " kullanıcı ", " kolay ", " otomatik ", " aktif ",
                " önce ", " sonra ", " tek ", " tık ", " tıkl ", " alın ", " incele ", " hakkında ", " yanında ",
                " arka ", " işlemle", " temizlen", " optimize ", " edilir ", " düşürül", " artırıl", " zorla ",
                " maksimum ", " akıllı ", " gerçek ", " her zaman ", " detaylı ", " özel ", " güç ", " modu ",
                " veresiye ", " cari ", " gider ", " bakkal ", " tahsilat ", " tahsilata ", " esnaflar ",
                " türkçe ", " turkce ", " ingilizce ", " almanca ", " ispanyolca ", " fransızca ",
                " fransizca ", " italyanca ", " rusça ", " rusca ",
                " stok ", " siparis ", " sipariş ", " buton ", " pasife ", " tekil ", " unuttum ",
                " carisi ", " gizlilik ", " kasa ", " defteri ", " okul ", " ders ", " satis ", " satış ",
                " borc ", " borç ", " alacak ", " arac ", " araç ", " randevu ", " klinik ", " dugun ",
                " düğün ", " salonu ", " acente ", " police ", " poliçe ", " rehberi ", " yonetim ", " yönetim "
            };
            return markers.Any(normalized.Contains);
        }

        private static string ToGoogleLanguageCode(string code)
        {
            if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            return code.Split('-', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        }

        private static bool IsTurkicNearSourceLanguage(string code)
        {
            var neutralCode = code.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
            return neutralCode.Equals("az", StringComparison.OrdinalIgnoreCase)
                || neutralCode.Equals("tk", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToDeepLLanguageCode(string code)
        {
            if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "EN-US";
            return code.Split('-', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        }
    }
}
