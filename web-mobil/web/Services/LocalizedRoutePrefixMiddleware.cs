namespace NSYazilim.Web.Services
{
    public sealed class LocalizedRoutePrefixMiddleware
    {
        public const string RouteLanguageItemKey = "NSX.RouteLanguageCode";
        public const string RouteUrlCodeItemKey = "NSX.RouteUrlCode";

        private readonly RequestDelegate _next;

        public LocalizedRoutePrefixMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, SiteLocalizationService localization)
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var trimmed = path.Trim('/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                await _next(context);
                return;
            }

            var slash = trimmed.IndexOf('/');
            var firstSegment = slash >= 0 ? trimmed[..slash] : trimmed;
            if (firstSegment.Equals("api", StringComparison.OrdinalIgnoreCase)
                || firstSegment.Equals("teknikservis", StringComparison.OrdinalIgnoreCase)
                || firstSegment.Equals("salontakip", StringComparison.OrdinalIgnoreCase)
                || firstSegment.Equals("veresiye", StringComparison.OrdinalIgnoreCase)
                || firstSegment.Equals("liveChatHub", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            var languages = await localization.GetActiveLanguagesAsync(context.RequestAborted);
            var remainder = slash >= 0 ? "/" + trimmed[(slash + 1)..] : "/";

            // Previously published locale URLs must not die as 404s after the TR+EN cleanup.
            // Public content is permanently consolidated into English; private/account paths
            // lose the stale locale prefix while preserving the English UI preference.
            if (PublicLanguagePolicy.IsRetiredUrlCode(firstSegment))
            {
                var english = languages.FirstOrDefault(x =>
                    x.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.UrlCode, "en", StringComparison.OrdinalIgnoreCase));

                if (english != null && !context.Response.HasStarted)
                    localization.WriteLanguageCookie(context.Response, context.Request, english.Code);

                var target = remainder;
                if (english != null && SeoLanguageUrlService.IsRouteLanguagePath(remainder))
                    target = SeoLanguageUrlService.BuildLocalizedPath(
                        remainder,
                        english,
                        SeoLanguageUrlService.ResolveDefaultLanguage(languages));

                context.Response.StatusCode = HttpMethods.IsGet(context.Request.Method)
                    || HttpMethods.IsHead(context.Request.Method)
                        ? StatusCodes.Status308PermanentRedirect
                        : StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = target + context.Request.QueryString;
                return;
            }

            var language = languages.FirstOrDefault(x =>
                string.Equals((x.UrlCode ?? string.Empty).Trim('/'), firstSegment, StringComparison.OrdinalIgnoreCase));
            if (language == null)
            {
                await _next(context);
                return;
            }

            var defaultLanguage = SeoLanguageUrlService.ResolveDefaultLanguage(languages);

            // Account, ödeme, indirme, API ve statik yollar dil öneki taşımaz. Bir Razor
            // Url.Action/PathBase kombinasyonu yanlışlıkla /fr/Account/... gibi bir adres
            // üretse bile dili cookie ile koruyup tek kök URL'ye normalize et. POST için
            // 307 kullanmak body + metodu kaybetmez; GET/HEAD kalıcı 308 ile birleşir.
            if (!SeoLanguageUrlService.IsRouteLanguagePath(remainder))
            {
                if (!context.Response.HasStarted)
                    localization.WriteLanguageCookie(context.Response, context.Request, language.Code);

                context.Response.StatusCode = HttpMethods.IsGet(context.Request.Method)
                    || HttpMethods.IsHead(context.Request.Method)
                        ? StatusCodes.Status308PermanentRedirect
                        : StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = remainder + context.Request.QueryString;
                return;
            }

            // Varsayılan Türkçe mevcut kök URL'lerini korur. /tr/... gibi kopyalar tek bir
            // kalıcı URL'ye 308 ile birleşir; mevcut Google indeksleri değişmez.
            if (string.Equals(language.Code, defaultLanguage.Code, StringComparison.OrdinalIgnoreCase)
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = remainder + context.Request.QueryString;
                return;
            }

            context.Items[RouteLanguageItemKey] = language.Code;
            context.Items[RouteUrlCodeItemKey] = language.UrlCode;

            var originalPathBase = context.Request.PathBase;
            var originalPath = context.Request.Path;
            context.Request.PathBase = originalPathBase.Add(new PathString("/" + language.UrlCode.Trim('/')));
            context.Request.Path = new PathString(remainder);

            try
            {
                await _next(context);
            }
            finally
            {
                context.Request.PathBase = originalPathBase;
                context.Request.Path = originalPath;
            }
        }
    }
}
