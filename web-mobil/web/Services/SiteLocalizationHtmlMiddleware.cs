using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NSYazilim.Web.Services
{
    public sealed partial class SiteLocalizationHtmlMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SiteLocalizationHtmlMiddleware> _logger;

        public SiteLocalizationHtmlMiddleware(RequestDelegate next, ILogger<SiteLocalizationHtmlMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(
            HttpContext context,
            SiteLocalizationService localization,
            LocalizationCatalog catalog,
            SeoLanguageUrlService seoUrls,
            LocalizationCaptureService capture)
        {
            if (ShouldSkipPath(context.Request.Path.Value))
            {
                await _next(context);
                return;
            }

            var language = await localization.GetCurrentLanguageAsync(context.RequestAborted);
            if (!context.Response.HasStarted)
                context.Response.Headers["Content-Language"] = language.Code;

            var isSourceLanguage = language.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase);

            // Türkçe kaynak dil zaten Razor/DB tarafında doğal haliyle üretiliyor. Kaynak dili
            // yeniden MemoryStream'e alıp bütün HTML'i regex + sözlük üzerinden geçirmek hem
            // TTFB'yi yükseltiyor hem de ziyaretçi takibi tamamlanana kadar ilk byte'ı tutuyordu.
            // Kaynak dilde hiçbir response buffering/HTML çevirisi yapma.
            if (isSourceLanguage)
            {
                await _next(context);
                return;
            }

            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context);
                buffer.Position = 0;

                if (!IsHtmlResponse(context.Response) || buffer.Length == 0)
                {
                    await CopyBufferAsync(buffer, originalBody, context.RequestAborted);
                    return;
                }

                using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var html = await reader.ReadToEndAsync(context.RequestAborted);

                // Every non-Turkish public request is also a self-healing inventory pass.
                // Newly published Razor/DB text is captured BEFORE translation, so the source
                // stays canonical Turkish and the background worker can queue it for every
                // active language. This closes the historical gap where new release text could
                // appear on the site without ever entering LocalizationResources.
                if (ShouldCapturePublicPath(context.Request.Path.Value))
                    CaptureRenderedPublicSources(html, context.Request.Path.Value, capture);

                // Render from the target-only map. GetTranslationMapAsync also contains Turkish
                // source fallbacks; using it here can expose Turkish words on a newly activated
                // language until its queue is complete. Missing target rows instead fall back to
                // the native English catalog, then to the small reviewed route-specific maps.
                var map = await catalog.GetNativeTranslationMapAsync(language.Code, context.RequestAborted);
                var fallbackMap = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    var englishMap = await catalog.GetNativeTranslationMapAsync("en-US", context.RequestAborted);
                    foreach (var pair in englishMap)
                        fallbackMap[pair.Key] = pair.Value;
                }

                var isPublicAuth = LocalizationPublicAuthSeedData.IsPublicAuthRequestPath(context.Request.Path.Value);
                IReadOnlyDictionary<string, string> curatedFallback;
                if (isPublicAuth)
                {
                    curatedFallback = LocalizationPublicAuthSeedData.EnglishFallbackByKey;
                }
                else if (LocalizationPublicAccountSeedData.IsAccountUiRequestPath(context.Request.Path.Value))
                {
                    curatedFallback = LocalizationPublicAccountSeedData.EnglishFallbackByKey;
                }
                else
                {
                    curatedFallback = LocalizationConversionFlowSeedData.EnglishFallbackByKey;
                }

                foreach (var pair in curatedFallback)
                    fallbackMap[pair.Key] = pair.Value;
                IReadOnlyDictionary<string, string>? fallbackTranslations = fallbackMap.Count == 0 ? null : fallbackMap;

                var translated = TranslateHtml(
                    html,
                    map,
                    language.Code,
                    isPublicAuth,
                    fallbackTranslations);

                // Non-default dil sayfalarında public <a href="/..."> linklerini aynı
                // kalıcı dil alanına taşır. Böylece /fr/blog içinden çıkan crawler tekrar
                // Türkçe kök URL'lere düşmez; statik/API/hesap/indirme linklerine dokunulmaz.
                translated = await seoUrls.RewritePublicAnchorLinksAsync(
                    translated,
                    language,
                    context.RequestAborted);

                var bytes = Encoding.UTF8.GetBytes(translated);

                context.Response.ContentLength = bytes.Length;
                context.Response.Body = originalBody;
                await originalBody.WriteAsync(bytes, context.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dil motoru HTML çevirisini uygulayamadı; özgün yanıt döndürülüyor.");
                context.Response.Body = originalBody;
                if (buffer.CanSeek)
                {
                    buffer.Position = 0;
                    await CopyBufferAsync(buffer, originalBody, context.RequestAborted);
                }
            }
            finally
            {
                context.Response.Body = originalBody;
            }
        }

        private static bool ShouldCapturePublicPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            return !path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/Dealer", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/Bayi", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/BankTransfer", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/Havale", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/update", StringComparison.OrdinalIgnoreCase);
        }

        private static void CaptureRenderedPublicSources(
            string html,
            string? path,
            LocalizationCaptureService capture)
        {
            if (string.IsNullOrWhiteSpace(html))
                return;

            // JavaScript user messages are translated by TranslateKnownScriptLiterals, so they
            // must also enter the same durable inventory. Technical literals/URLs are filtered
            // by IsLikelyHumanText + capture service invariance rules.
            foreach (Match scriptMatch in ScriptBlockRegex().Matches(html))
            {
                foreach (Match literalMatch in JavaScriptStringRegex().Matches(scriptMatch.Value))
                {
                    var raw = literalMatch.Groups["value"].Value;
                    if (raw.Contains('\\') || raw.Contains("${", StringComparison.Ordinal))
                        continue;

                    var normalized = LocalizationTextKey.Normalize(WebUtility.HtmlDecode(raw));
                    if (IsLikelyHumanText(normalized)
                        && LocalizationMachineTranslationService.LooksLikeTurkishText(normalized))
                    {
                        capture.Enqueue(normalized, path);
                    }
                }
            }

            var working = LocalizationOptOutBlockRegex().Replace(html, string.Empty);
            working = ProtectedBlockRegex().Replace(working, string.Empty);

            // Capture the exact semantic block keys used by TranslateHtml. Inline tags such as
            // <strong>/<em>/<br> therefore cannot split one visible sentence into several keys.
            working = TranslatableContentBlockRegex().Replace(working, match =>
            {
                var plain = ProductContentSanitizer.ToPlainText(match.Groups["content"].Value);
                if (IsLikelyHumanText(plain)
                    && LocalizationMachineTranslationService.LooksLikeTurkishText(plain))
                    capture.Enqueue(plain, path);
                return string.Empty;
            });

            // Capture remaining standalone text nodes outside p/h/li/etc. blocks.
            foreach (Match match in TextNodeRegex().Matches(working))
            {
                var raw = WebUtility.HtmlDecode(match.Groups["text"].Value);
                var normalized = LocalizationTextKey.Normalize(raw);
                if (IsLikelyHumanText(normalized)
                    && LocalizationMachineTranslationService.LooksLikeTurkishText(normalized))
                    capture.Enqueue(normalized, path);
            }

            // jQuery unobtrusive validation messages are user-facing language surface too.
            foreach (Match match in ValidationMessageAttributeRegex().Matches(working))
            {
                var normalized = LocalizationTextKey.Normalize(WebUtility.HtmlDecode(match.Groups["value"].Value));
                if (IsLikelyHumanText(normalized)
                    && LocalizationMachineTranslationService.LooksLikeTurkishText(normalized))
                {
                    capture.Enqueue(normalized, path);
                }
            }

            // Accessibility/SEO/user-facing attributes are part of the public language surface too.
            foreach (Match match in TranslatableAttributeRegex().Matches(working))
            {
                var normalized = LocalizationTextKey.Normalize(WebUtility.HtmlDecode(match.Groups["value"].Value));
                var attributePrefix = match.Groups["prefix"].Value;
                var technicalMeta = attributePrefix.StartsWith("content", StringComparison.OrdinalIgnoreCase)
                    && (normalized.Contains("width=", StringComparison.OrdinalIgnoreCase)
                        || normalized.Contains("initial-scale", StringComparison.OrdinalIgnoreCase)
                        || normalized.Contains("charset", StringComparison.OrdinalIgnoreCase));
                if (!technicalMeta
                    && IsLikelyHumanText(normalized)
                    && LocalizationMachineTranslationService.LooksLikeTurkishText(normalized))
                    capture.Enqueue(normalized, path);
            }
        }

        private static bool IsHtmlResponse(HttpResponse response)
        {
            if (response.StatusCode < 200 || response.StatusCode >= 300)
                return false;

            return response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static bool ShouldSkipPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // Admin panel always stays Turkish source text — no HTML rewrite / MT.
            return path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/nas", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/teknikservis", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/salontakip", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/veresiye", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/liveChatHub", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/presence", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/license", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/update", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/media", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/generated", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/sitemap.xml", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/robots.txt", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/videolar/veri", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/store/downloaddemo", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/store/downloadfree", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/store/freeactivationpayload", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/store/freeprogramfile", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/account/downloadfile", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/account/offlinelicensefile", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/account/licensetext", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/admin/licenseofflinefile", StringComparison.OrdinalIgnoreCase)
                || (path.StartsWith("/bayi/satis/", StringComparison.OrdinalIgnoreCase)
                    && path.Contains("/indir/", StringComparison.OrdinalIgnoreCase));
        }

        private static string TranslateHtml(
            string html,
            IReadOnlyDictionary<string, string> translations,
            string languageCode,
            bool translateValidationAttributes,
            IReadOnlyDictionary<string, string>? fallbackTranslations)
        {
            html = ScriptBlockRegex().Replace(html, match =>
                TranslateKnownScriptLiterals(match.Value, translations, languageCode));

            var protectedBlocks = new List<string>();
            string ProtectBlock(string value)
            {
                var token = $"@@NSX_SKIP_{protectedBlocks.Count}@@";
                protectedBlocks.Add(value);
                return token;
            }

            // Language identities (native names, language codes, flags) and any other
            // explicitly opted-out UI fragment must remain verbatim. `translate="no"` is
            // the HTML standard signal; data-nsx-localization="off" is our server-side
            // equivalent. Protect these blocks before text-node/attribute translation so
            // both visible text and accessibility attributes stay untouched.
            var working = LocalizationOptOutBlockRegex().Replace(html, match => ProtectBlock(match.Value));
            working = ProtectedBlockRegex().Replace(working, match => ProtectBlock(match.Value));

            // Product descriptions are edited in Admin as ONE stable full-description
            // translation. Resolve the entire detail-rich-copy container first so a long
            // multi-paragraph/list description is never split back into old node translations.
            working = ProductDescriptionContainerRegex().Replace(working, match =>
            {
                var sourceHtml = match.Groups["content"].Value;
                var plainSource = ProductContentSanitizer.ToPlainText(sourceHtml);
                if (string.IsNullOrWhiteSpace(plainSource))
                    return match.Value;

                var sourceKey = LocalizationTextKey.Create(plainSource);
                if (!translations.TryGetValue(sourceKey, out var fullTarget) || string.IsNullOrWhiteSpace(fullTarget))
                    return match.Value;

                fullTarget = LocalizationDisplayNormalizer.Normalize(fullTarget, languageCode);

                // Legacy DescriptionFull rows were historically stored as one plain sentence.
                // Applying such a value over a multi-block HTML description flattens every
                // <h2>/<h3>/<p>/<ul>/<li> into a single paragraph. Only let a whole-description
                // override win when it carries real block structure (HTML or Markdown/newline
                // paragraphs), or when the source itself contains only one semantic block.
                // Otherwise keep the source HTML shell and let the semantic block translator
                // below replace each heading/paragraph/list item independently.
                if (!CanUseFullProductDescriptionTranslation(sourceHtml, fullTarget))
                    return match.Value;

                return match.Groups["open"].Value
                    + RenderSafeRichTranslation(fullTarget)
                    + match.Groups["close"].Value;
            });

            // Product/editor content may contain inline <strong>/<em> tags. Admin can save
            // the whole visible block as one translation; when such a translation exists
            // it wins over individual text-node translations. This keeps editing one-piece
            // and prevents mixed Turkish/English output around formatting tags.
            working = TranslatableContentBlockRegex().Replace(working, match =>
            {
                var innerHtml = match.Groups["content"].Value;
                var plainSource = ProductContentSanitizer.ToPlainText(innerHtml);
                if (!IsLikelyHumanText(plainSource))
                    return match.Value;

                var sourceKey = LocalizationTextKey.Create(plainSource);
                var resolvedLanguageCode = languageCode;
                if (!translations.TryGetValue(sourceKey, out var fullTarget) || string.IsNullOrWhiteSpace(fullTarget))
                {
                    // Keep the public page fully readable while a newly published target
                    // translation is still in the durable queue. Whole-block English fallback
                    // avoids mixed Turkish/target-language output around <strong>/<em> tags.
                    if (fallbackTranslations == null
                        || !fallbackTranslations.TryGetValue(sourceKey, out fullTarget)
                        || string.IsNullOrWhiteSpace(fullTarget))
                    {
                        return match.Value;
                    }

                    resolvedLanguageCode = "en-US";
                }

                fullTarget = LocalizationDisplayNormalizer.Normalize(fullTarget, resolvedLanguageCode);
                return match.Groups["open"].Value
                    + RenderSafeInlineTranslation(fullTarget)
                    + match.Groups["close"].Value;
            });

            working = TextNodeRegex().Replace(working, match =>
            {
                var raw = match.Groups["text"].Value;
                if (raw.Contains("@@NSX_SKIP_", StringComparison.Ordinal))
                    return raw;

                return TranslateSegmentWithFallback(
                    raw,
                    translations,
                    fallbackTranslations,
                    languageCode,
                    isAttribute: false);
            });

            working = TranslatableAttributeRegex().Replace(working, match =>
            {
                var prefix = match.Groups["prefix"].Value;
                var quote = match.Groups["quote"].Value;
                var raw = match.Groups["value"].Value;
                var translated = TranslateSegmentWithFallback(
                    raw,
                    translations,
                    fallbackTranslations,
                    languageCode,
                    isAttribute: true);
                return prefix + quote + translated + quote;
            });

            // Anonymous auth forms use jQuery unobtrusive validation. Its messages live in
            // data-val-* attributes rather than visible text nodes. Translate those values
            // only on the curated public auth routes; parameter attributes such as max=150
            // contain no human text and are left unchanged by TranslateSegment.
            if (translateValidationAttributes)
            {
                working = ValidationMessageAttributeRegex().Replace(working, match =>
                {
                    var prefix = match.Groups["prefix"].Value;
                    var quote = match.Groups["quote"].Value;
                    var raw = match.Groups["value"].Value;
                    var translated = TranslateSegmentWithFallback(
                    raw,
                    translations,
                    fallbackTranslations,
                    languageCode,
                    isAttribute: true);
                    return prefix + quote + translated + quote;
                });
            }

            for (var i = 0; i < protectedBlocks.Count; i++)
                working = working.Replace($"@@NSX_SKIP_{i}@@", protectedBlocks[i], StringComparison.Ordinal);

            return working;
        }

        private static string TranslateKnownScriptLiterals(
            string script,
            IReadOnlyDictionary<string, string> translations,
            string languageCode)
        {
            return JavaScriptStringRegex().Replace(script, match =>
            {
                var quote = match.Groups["quote"].Value;
                var raw = match.Groups["value"].Value;
                if (raw.Contains('\\') || raw.Contains("${", StringComparison.Ordinal))
                    return match.Value;

                var normalized = LocalizationTextKey.Normalize(WebUtility.HtmlDecode(raw));
                if (!IsLikelyHumanText(normalized))
                    return match.Value;

                if (!translations.TryGetValue(LocalizationTextKey.Create(normalized), out var target)
                    || string.IsNullOrWhiteSpace(target))
                    return match.Value;

                target = LocalizationDisplayNormalizer.Normalize(target, languageCode);
                var escaped = target
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace(quote, "\\" + quote, StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
                    .Replace("\u2029", "\\u2029", StringComparison.Ordinal)
                    .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
                return quote + escaped + quote;
            });
        }

        private static string TranslateSegmentWithFallback(
            string raw,
            IReadOnlyDictionary<string, string> translations,
            IReadOnlyDictionary<string, string>? fallbackTranslations,
            string languageCode,
            bool isAttribute)
        {
            var translated = TranslateSegment(raw, translations, languageCode, isAttribute);
            if (fallbackTranslations == null || fallbackTranslations.Count == 0)
                return translated;

            // The HTML pipeline now uses a target-only translation map. Therefore an unchanged
            // segment means the target language has no usable row for this source key. Fall back
            // to reviewed/native English while the target queue catches up. Do not use character-
            // based Turkish detection here: German/French legitimately contain characters such as
            // ü/ö/ç and must never be mistaken for Turkish.
            if (!string.Equals(translated, raw, StringComparison.Ordinal))
                return translated;

            var fallback = TranslateSegment(raw, fallbackTranslations, "en-US", isAttribute);
            return string.Equals(fallback, raw, StringComparison.Ordinal) ? translated : fallback;
        }

        private static string TranslateSegment(
            string raw,
            IReadOnlyDictionary<string, string> translations,
            string languageCode,
            bool isAttribute)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            var start = 0;
            while (start < raw.Length && char.IsWhiteSpace(raw[start])) start++;
            var end = raw.Length - 1;
            while (end >= start && char.IsWhiteSpace(raw[end])) end--;
            if (end < start)
                return raw;

            var core = raw.Substring(start, end - start + 1);
            var normalized = LocalizationTextKey.Normalize(WebUtility.HtmlDecode(core));
            if (!IsLikelyHumanText(normalized))
                return raw;

            var key = LocalizationTextKey.Create(normalized);

            if (!translations.TryGetValue(key, out var target) || string.IsNullOrWhiteSpace(target))
            {
                if (!TryTranslateComposite(normalized, translations, languageCode, out target))
                    return raw;
            }

            target = LocalizationDisplayNormalizer.Normalize(target, languageCode);
            var encoded = WebUtility.HtmlEncode(target);
            if (!isAttribute)
                encoded = encoded.Replace("&#39;", "'", StringComparison.Ordinal);

            return raw[..start] + encoded + raw[(end + 1)..];
        }


        private static bool TryTranslateComposite(
            string normalized,
            IReadOnlyDictionary<string, string> translations,
            string languageCode,
            out string translated)
        {
            translated = string.Empty;

            var leading = LeadingDynamicTokenRegex().Match(normalized);
            if (leading.Success)
            {
                var source = LocalizationTextKey.Normalize(leading.Groups["text"].Value);
                if (translations.TryGetValue(LocalizationTextKey.Create(source), out var target)
                    && !string.IsNullOrWhiteSpace(target))
                {
                    translated = leading.Groups["dynamic"].Value
                        + LocalizationDisplayNormalizer.Normalize(target, languageCode);
                    return true;
                }
            }

            var trailing = TrailingDynamicTokenRegex().Match(normalized);
            if (trailing.Success)
            {
                var source = LocalizationTextKey.Normalize(trailing.Groups["text"].Value);
                if (translations.TryGetValue(LocalizationTextKey.Create(source), out var target)
                    && !string.IsNullOrWhiteSpace(target))
                {
                    translated = LocalizationDisplayNormalizer.Normalize(target, languageCode)
                        + trailing.Groups["dynamic"].Value;
                    return true;
                }
            }

            foreach (var suffix in CompositeSuffixes)
            {
                if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!translations.TryGetValue(LocalizationTextKey.Create(suffix), out var target)
                    || string.IsNullOrWhiteSpace(target))
                    continue;

                var prefix = normalized[..^suffix.Length];
                translated = prefix + LocalizationDisplayNormalizer.Normalize(target, languageCode);
                return true;
            }

            return false;
        }


        private static bool CanUseFullProductDescriptionTranslation(string sourceHtml, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return false;

            // A one-block source cannot lose heading/list hierarchy, so a plain translation is safe.
            var sourceBlockCount = TranslatableContentBlockRegex().Matches(sourceHtml).Count;
            if (sourceBlockCount <= 1)
                return true;

            // Explicit block-level HTML is the strongest signal that the target intentionally
            // defines its own rich structure. Inline-only tags such as <strong> are not enough
            // for a multi-section product description.
            if (RichBlockTagRegex().IsMatch(target))
                return true;

            var normalized = target.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            // Markdown headings/lists are converted by RenderSafeRichTranslation.
            if (MarkdownBlockLineRegex().IsMatch(normalized))
                return true;

            // Plain text with real paragraph boundaries is also safe: each paragraph remains
            // separate instead of becoming the single giant paragraph seen on legacy pages.
            return normalized.Contains("\n\n", StringComparison.Ordinal);
        }

        private static string RenderSafeRichTranslation(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            // Explicit HTML entered by the administrator is always sanitized through the
            // existing product-content allowlist before it reaches the page.
            if (value.Contains('<'))
                return ProductContentSanitizer.NormalizeForEditor(value);

            var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
            var html = new StringBuilder(value.Length + 256);
            var paragraph = new List<string>();
            var listMode = 0; // 0 none, 1 ul, 2 ol

            string FormatInline(string text)
            {
                var encoded = WebUtility.HtmlEncode(text.Trim());
                return BoldMarkerRegex().Replace(encoded, "<strong>${text}</strong>")
                    .Replace("&#39;", "'", StringComparison.Ordinal);
            }

            void FlushParagraph()
            {
                if (paragraph.Count == 0)
                    return;
                html.Append("<p>").Append(string.Join("<br>", paragraph.Select(FormatInline))).Append("</p>");
                paragraph.Clear();
            }

            void CloseList()
            {
                if (listMode == 1) html.Append("</ul>");
                else if (listMode == 2) html.Append("</ol>");
                listMode = 0;
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    FlushParagraph();
                    CloseList();
                    continue;
                }

                if (line.StartsWith("### ", StringComparison.Ordinal) || line.StartsWith("#### ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    CloseList();
                    var heading = line.StartsWith("#### ", StringComparison.Ordinal) ? line[5..] : line[4..];
                    html.Append("<h3>").Append(FormatInline(heading)).Append("</h3>");
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    CloseList();
                    html.Append("<h2>").Append(FormatInline(line[3..])).Append("</h2>");
                    continue;
                }

                if (line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    if (listMode != 1)
                    {
                        CloseList();
                        html.Append("<ul>");
                        listMode = 1;
                    }
                    html.Append("<li>").Append(FormatInline(line[2..])).Append("</li>");
                    continue;
                }

                var ordered = Regex.Match(line, @"^(?<n>\d+)\.\s+(?<text>.+)$", RegexOptions.CultureInvariant);
                if (ordered.Success)
                {
                    FlushParagraph();
                    if (listMode != 2)
                    {
                        CloseList();
                        html.Append("<ol>");
                        listMode = 2;
                    }
                    html.Append("<li>").Append(FormatInline(ordered.Groups["text"].Value)).Append("</li>");
                    continue;
                }

                CloseList();
                paragraph.Add(line);
            }

            FlushParagraph();
            CloseList();
            return html.ToString();
        }

        private static string RenderSafeInlineTranslation(string value)
        {
            var encoded = WebUtility.HtmlEncode(value);
            // Optional light formatting for admin-entered full-block translations.
            // HTML itself remains encoded, so this cannot inject arbitrary markup.
            encoded = BoldMarkerRegex().Replace(encoded, "<strong>${text}</strong>");
            encoded = encoded.Replace("\r\n", "<br>", StringComparison.Ordinal)
                .Replace("\n", "<br>", StringComparison.Ordinal)
                .Replace("&#39;", "'", StringComparison.Ordinal);
            return encoded;
        }

        private static readonly string[] CompositeSuffixes =
        {
            "ürün görseli",
            "görseli göster",
            "kez indirildi",
            "TL ve üzeri",
            "tarihine kadar"
        };

        private static bool IsLikelyHumanText(string normalized)
        {
            if (normalized.Length < 2 || normalized.Length > 9000 || !normalized.Any(char.IsLetter))
                return false;
            if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("#", StringComparison.Ordinal)
                || normalized.StartsWith(".", StringComparison.Ordinal))
                return false;

            // Kod/selector/JSON benzeri kısa teknik literal'leri kaynak havuzuna alma.
            if (!normalized.Contains(' ') && (normalized.Contains('/') || normalized.Contains('=') || normalized.Contains('{') || normalized.Contains('}')))
                return false;

            return true;
        }

        private static async Task CopyBufferAsync(MemoryStream buffer, Stream target, CancellationToken cancellationToken)
        {
            if (buffer.CanSeek)
                buffer.Position = 0;
            await buffer.CopyToAsync(target, cancellationToken);
        }

        [GeneratedRegex(@"^(?<dynamic>[\d\s.,:/%+\-₺$€£]+)(?<text>.+)$", RegexOptions.CultureInvariant)]
        private static partial Regex LeadingDynamicTokenRegex();

        [GeneratedRegex(@"^(?<text>.+?)(?<dynamic>[\s\d.,:/%+\-₺$€£]+)$", RegexOptions.CultureInvariant)]
        private static partial Regex TrailingDynamicTokenRegex();

        [GeneratedRegex(@"(?is)<(?<tag>[a-z][a-z0-9:-]*)\b(?=[^>]*(?:\bdata-nsx-localization\s*=\s*[""']off[""']|\btranslate\s*=\s*[""']no[""']))[^>]*>.*?</\k<tag>\s*>", RegexOptions.CultureInvariant)]
        private static partial Regex LocalizationOptOutBlockRegex();

        [GeneratedRegex(@"(?is)<(?<tag>script|style|svg|pre|code|textarea|noscript)\b[^>]*>.*?</\k<tag>\s*>", RegexOptions.CultureInvariant)]
        private static partial Regex ProtectedBlockRegex();

        [GeneratedRegex(@"(?is)<script\b[^>]*>.*?</script\s*>", RegexOptions.CultureInvariant)]
        private static partial Regex ScriptBlockRegex();

        [GeneratedRegex("(?s)(?<quote>[\\\"'])(?<value>(?:\\\\.|[^\\\"'\\\\])*)\\k<quote>", RegexOptions.CultureInvariant)]
        private static partial Regex JavaScriptStringRegex();

        [GeneratedRegex(@"(?is)(?<open><div\s+class=""detail-rich-copy""[^>]*>)(?<content>.*?)(?<close></div\s*>)", RegexOptions.CultureInvariant)]
        private static partial Regex ProductDescriptionContainerRegex();

        [GeneratedRegex(@"(?is)(?<open><(?<tag>p|h1|h2|h3|h4|li|blockquote|caption|th|td)\b[^>]*>)(?<content>.*?)(?<close></\k<tag>\s*>)", RegexOptions.CultureInvariant)]
        private static partial Regex TranslatableContentBlockRegex();


        [GeneratedRegex(@"(?is)</?(?:p|h1|h2|h3|h4|ul|ol|li|blockquote|table|caption|thead|tbody|tfoot|tr|th|td)\b", RegexOptions.CultureInvariant)]
        private static partial Regex RichBlockTagRegex();

        [GeneratedRegex(@"(?m)^\s*(?:#{2,4}\s+|[-*]\s+|\d+\.\s+)", RegexOptions.CultureInvariant)]
        private static partial Regex MarkdownBlockLineRegex();

        [GeneratedRegex(@"\*\*(?<text>.+?)\*\*", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex BoldMarkerRegex();

        [GeneratedRegex(@"(?s)(?<=>)(?<text>[^<>]+)(?=<)", RegexOptions.CultureInvariant)]
        private static partial Regex TextNodeRegex();

        [GeneratedRegex("(?is)(?<prefix>\\b(?:title|aria-label|placeholder|alt|content)\\s*=\\s*)(?<quote>[\\\"'])(?<value>.*?)(?=\\k<quote>)", RegexOptions.CultureInvariant)]
        private static partial Regex TranslatableAttributeRegex();

        [GeneratedRegex("(?is)(?<prefix>\\bdata-val-[a-z0-9-]+\\s*=\\s*)(?<quote>[\\\"'])(?<value>.*?)\\k<quote>", RegexOptions.CultureInvariant)]
        private static partial Regex ValidationMessageAttributeRegex();
    }
}
