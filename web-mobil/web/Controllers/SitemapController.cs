using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    public class SitemapController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly SiteLocalizationService _localization;

        public SitemapController(ApplicationDbContext context, SiteLocalizationService localization)
        {
            _context = context;
            _localization = localization;
        }

        [HttpGet("sitemap.xml")]
        public async Task<IActionResult> Index()
        {
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            XNamespace xhtml = "http://www.w3.org/1999/xhtml";
            var urlset = new XElement(
                ns + "urlset",
                new XAttribute(XNamespace.Xmlns + "xhtml", xhtml.NamespaceName));
            var addedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var publicSeoUpdated = new DateTimeOffset(2026, 9, 6, 19, 30, 0, TimeSpan.FromHours(3));

            var languages = await _localization.GetActiveLanguagesAsync(HttpContext.RequestAborted);
            var defaultLanguage = SeoLanguageUrlService.ResolveDefaultLanguage(languages);

            string HrefLang(SiteLanguage language)
            {
                var code = (language.Code ?? string.Empty).Trim().Replace('_', '-');
                return string.IsNullOrWhiteSpace(code) ? language.UrlCode : code;
            }

            string LocalizedAbsolute(string basePath, SiteLanguage language)
                => SeoTextHelper.AbsoluteUrl(
                    SeoLanguageUrlService.BuildLocalizedPath(basePath, language, defaultLanguage));

            void AddUrl(string basePath, DateTimeOffset? lastModified = null)
            {
                basePath = SeoTextHelper.CanonicalPath(basePath);
                var alternateUrls = languages
                    .Select(language => new
                    {
                        Language = language,
                        Url = LocalizedAbsolute(basePath, language)
                    })
                    .ToList();
                var xDefault = LocalizedAbsolute(basePath, defaultLanguage);

                foreach (var item in alternateUrls)
                {
                    if (!addedUrls.Add(item.Url))
                        continue;

                    var url = new XElement(ns + "url", new XElement(ns + "loc", item.Url));

                    if (lastModified.HasValue)
                        url.Add(new XElement(ns + "lastmod", lastModified.Value.ToString("yyyy-MM-dd")));

                    foreach (var alternate in alternateUrls)
                    {
                        url.Add(new XElement(
                            xhtml + "link",
                            new XAttribute("rel", "alternate"),
                            new XAttribute("hreflang", HrefLang(alternate.Language)),
                            new XAttribute("href", alternate.Url)));
                    }

                    url.Add(new XElement(
                        xhtml + "link",
                        new XAttribute("rel", "alternate"),
                        new XAttribute("hreflang", "x-default"),
                        new XAttribute("href", xDefault)));

                    urlset.Add(url);
                }
            }

            AddUrl("/", publicSeoUpdated);
            AddUrl("/store", publicSeoUpdated);
            AddUrl("/videolar", publicSeoUpdated);
            AddUrl("/urun/nsx-komisyonlu-cari-takip-pro", publicSeoUpdated);
            var blogLastModified = BlogController.Pages.Max(x => x.ModifiedAt);
            AddUrl("/blog", blogLastModified > publicSeoUpdated ? blogLastModified : publicSeoUpdated);

            foreach (var page in BlogController.Pages
                .Where(x => !string.Equals(x.Slug, "ucretsiz-veresiye-programi", StringComparison.OrdinalIgnoreCase)))
            {
                var path = page.IsLanding ? $"/{page.Slug}" : $"/blog/{page.Slug}";
                var pageLastModified = page.ModifiedAt > publicSeoUpdated ? page.ModifiedAt : publicSeoUpdated;
                AddUrl(path, pageLastModified);
            }

            AddUrl("/iletisim", publicSeoUpdated);
            AddUrl("/hakkimizda", publicSeoUpdated);
            AddUrl("/sikca-sorulan-sorular", publicSeoUpdated);
            AddUrl("/kvkk", publicSeoUpdated);
            AddUrl("/iade-politikasi", publicSeoUpdated);
            AddUrl("/gizlilik-politikasi", publicSeoUpdated);

            try
            {
                var products = await _context.Products
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted && x.IsActive)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => new
                    {
                        x.Id,
                        x.Slug,
                        x.CreatedAt
                    })
                    .ToListAsync(HttpContext.RequestAborted);

                foreach (var product in products)
                {
                    var slug = (product.Slug ?? string.Empty).Trim();
                    var path = !string.IsNullOrWhiteSpace(slug)
                        ? $"/urun/{slug.ToLowerInvariant()}"
                        : $"/store/detail/{product.Id}";

                    var productLastModified = new DateTimeOffset(product.CreatedAt) > publicSeoUpdated
                        ? new DateTimeOffset(product.CreatedAt)
                        : publicSeoUpdated;
                    AddUrl(path, productLastModified);
                }
            }
            catch
            {
                // Veritabanı geçici kapalı olsa bile statik SEO sayfaları sitemap'ten düşmesin.
            }

            try
            {
                var videos = await _context.ProductVideos
                    .AsNoTracking()
                    .Where(x => x.IsActive && x.Product != null && !x.Product.IsDeleted && x.Product.IsActive)
                    .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
                    .Select(x => new
                    {
                        x.Slug,
                        LastModified = x.UpdatedAt ?? x.CreatedAt
                    })
                    .ToListAsync(HttpContext.RequestAborted);

                foreach (var video in videos)
                {
                    var videoLastModified = new DateTimeOffset(video.LastModified);
                    AddUrl($"/videolar/{video.Slug}", videoLastModified > publicSeoUpdated ? videoLastModified : publicSeoUpdated);
                }
            }
            catch
            {
                // Video tablosu henüz oluşturulmadıysa sitemap üretimi devam etsin.
            }

            var document = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), urlset);
            return Content(document.ToString(), "application/xml; charset=utf-8", Encoding.UTF8);
        }
    }
}
