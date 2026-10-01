namespace NSYazilim.Web.Services
{
    public sealed class LocalizedSeoRedirectMiddleware
    {
        private readonly RequestDelegate _next;

        public LocalizedSeoRedirectMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(
            HttpContext context,
            SiteLocalizationService localization,
            SeoLanguageUrlService seoUrls)
        {
            if (!HttpMethods.IsGet(context.Request.Method)
                && !HttpMethods.IsHead(context.Request.Method))
            {
                await _next(context);
                return;
            }

            if (context.Items.ContainsKey(LocalizedRoutePrefixMiddleware.RouteLanguageItemKey)
                || !SeoLanguageUrlService.IsPublicLanguagePath(context.Request.Path.Value)
                || context.GetEndpoint() == null)
            {
                await _next(context);
                return;
            }

            var languages = await localization.GetActiveLanguagesAsync(context.RequestAborted);
            var defaultLanguage = SeoLanguageUrlService.ResolveDefaultLanguage(languages);
            var preferredLanguage = await localization.GetPreferredLanguageAsync(context.RequestAborted);
            if (preferredLanguage == null
                || string.Equals(preferredLanguage.Code, defaultLanguage.Code, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            // Öneksiz public URL hiçbir zaman tercih dilinde HTML üretmez. Kullanıcının
            // son seçimi yabancı dilse onu karşılık gelen kalıcı URL'ye taşı; bot/cookie'siz
            // istek ise canonical Türkçe kökte kalır.
            var returnUrl = $"{context.Request.Path}{context.Request.QueryString}";
            var target = await seoUrls.BuildLocalizedReturnUrlAsync(returnUrl, preferredLanguage, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = target;
        }

    }
}
