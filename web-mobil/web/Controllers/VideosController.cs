using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers
{
    public class VideosController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly SiteLocalizationService _siteLocalization;
        private readonly LocalizationCatalog _localizationCatalog;
        private readonly ILogger<VideosController> _logger;

        public VideosController(
            ApplicationDbContext context,
            SiteLocalizationService siteLocalization,
            LocalizationCatalog localizationCatalog,
            ILogger<VideosController> logger)
        {
            _context = context;
            _siteLocalization = siteLocalization;
            _localizationCatalog = localizationCatalog;
            _logger = logger;
        }

        [HttpGet("videolar")]
        public IActionResult Index()
        {
            // Listeyi burada DB'den beklemiyoruz. Sayfa aninda acilir;
            // videolar /videolar/veri endpoint'inden kisa timeout ile yuklenir.
            ViewData["Title"] = "Program Videoları";
            ViewData["MetaTitle"] = "NSX Program Videoları | Kurulum, Kullanım ve Tanıtım";
            ViewData["MetaDescription"] = "NSX Yazılım programlarının kurulum, kullanım, eğitim ve tanıtım videolarını izleyin. İlgili programı inceleyin, ücretsiz indirin veya satın alın.";
            ViewData["Canonical"] = SeoTextHelper.AbsoluteUrl("/videolar");
            ViewData["MetaKeywords"] = "NSX program videoları, yazılım kurulum videoları, program kullanım videoları, NSX Yazılım";
            ViewData["SkipActiveCampaign"] = true;

            return View(new ProductVideosIndexViewModel());
        }

        [HttpGet("videolar/veri")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Data(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            try
            {
                _context.Database.SetCommandTimeout(4);

                var videos = await _context.ProductVideos
                    .AsNoTracking()
                    .Include(x => x.Product)
                    .Where(x => x.IsActive && x.Product != null && !x.Product.IsDeleted && x.Product.IsActive)
                    .OrderByDescending(x => x.IsFeatured)
                    .ThenBy(x => x.SortOrder)
                    .ThenByDescending(x => x.Id)
                    .Take(200)
                    .ToListAsync(timeout.Token);

                var cards = videos.Select(BuildCard).ToList();
                var language = await _siteLocalization.GetCurrentLanguageAsync(cancellationToken);
                if (!language.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                    await LocalizeCardsAsync(cards, language.Code, cancellationToken);

                return Json(new
                {
                    ok = true,
                    videos = cards
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Videolar listesi zaman asimina ugradi.");
                return Json(new { ok = false, videos = Array.Empty<object>(), message = "Video listesi şu anda alınamadı." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Videolar listesi okunamadi.");
                return Json(new { ok = false, videos = Array.Empty<object>(), message = "Video listesi şu anda alınamadı." });
            }
        }

        [HttpGet("videolar/{slug}")]
        public async Task<IActionResult> Detail(string slug)
        {
            var cleanSlug = (slug ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(cleanSlug))
                return NotFound();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                _context.Database.SetCommandTimeout(5);

                var video = await _context.ProductVideos
                    .AsNoTracking()
                    .Include(x => x.Product)
                    .FirstOrDefaultAsync(x =>
                        x.IsActive &&
                        x.Slug == cleanSlug &&
                        x.Product != null &&
                        !x.Product.IsDeleted &&
                        x.Product.IsActive,
                        timeout.Token);

                if (video?.Product == null)
                    return NotFound();

                var canonicalPath = $"/videolar/{video.Slug}";
                if (!string.Equals(Request.Path.Value, canonicalPath, StringComparison.Ordinal))
                    return RedirectPermanent($"{Request.PathBase}{canonicalPath}");

                var product = video.Product;
                var sourceDisplayTitle = GetDisplayTitle(video, product);
                var sourceDescription = GetProductDescription(product);
                var sourceVideoMetaDescription = BuildVideoMetaDescription(sourceDisplayTitle, product, video.VideoType);
                var sourceMetaTitle = $"{sourceDisplayTitle} | NSX Yazılım";
                var sourceOgImageAlt = $"{sourceDisplayTitle} video kapağı";
                var displayTitle = sourceDisplayTitle;
                var description = sourceDescription;
                var videoMetaDescription = sourceVideoMetaDescription;
                var metaTitle = sourceMetaTitle;
                var ogImageAlt = sourceOgImageAlt;
                var productUrl = GetProductPublicUrl(product);
                var thumbnailUrl = YouTubeVideoHelper.GetThumbnailUrl(video.YouTubeVideoId);

                var language = await _siteLocalization.GetCurrentLanguageAsync(timeout.Token);
                if (!language.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                {
                    displayTitle = await _siteLocalization.TranslateAsync(sourceDisplayTitle, language.Code, timeout.Token);
                    description = await _siteLocalization.TranslateAsync(sourceDescription, language.Code, timeout.Token);
                    videoMetaDescription = await _siteLocalization.TranslateAsync(sourceVideoMetaDescription, language.Code, timeout.Token);
                    metaTitle = await _siteLocalization.TranslateAsync(sourceMetaTitle, language.Code, timeout.Token);
                    ogImageAlt = await _siteLocalization.TranslateAsync(sourceOgImageAlt, language.Code, timeout.Token);
                }

                var related = await _context.ProductVideos
                    .AsNoTracking()
                    .Include(x => x.Product)
                    .Where(x =>
                        x.Id != video.Id &&
                        x.IsActive &&
                        x.Product != null &&
                        !x.Product.IsDeleted &&
                        x.Product.IsActive)
                    .OrderByDescending(x => x.ProductId == product.Id)
                    .ThenByDescending(x => x.IsFeatured)
                    .ThenBy(x => x.SortOrder)
                    .ThenByDescending(x => x.Id)
                    .Take(4)
                    .ToListAsync(timeout.Token);

                var relatedCards = related.Select(BuildCard).ToList();
                if (!language.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
                    await LocalizeCardsAsync(relatedCards, language.Code, timeout.Token);

                var model = new ProductVideoDetailViewModel
                {
                    Video = video,
                    Product = product,
                    DisplayTitle = displayTitle,
                    ProductDescription = description,
                    ProductUrl = productUrl,
                    EmbedUrl = YouTubeVideoHelper.GetEmbedUrl(video.YouTubeVideoId),
                    ThumbnailUrl = thumbnailUrl,
                    IsFreeProduct = product.YearlyPrice <= 0 && product.LifetimePrice <= 0,
                    HasStock = product.StockQuantity > 0,
                    RelatedVideos = relatedCards
                };

                ViewData["Title"] = displayTitle;
                ViewData["MetaTitle"] = metaTitle;
                ViewData["MetaDescription"] = videoMetaDescription;
                ViewData["Canonical"] = SeoTextHelper.AbsoluteUrl(canonicalPath);
                ViewData["OgType"] = "video.other";
                ViewData["OgImage"] = thumbnailUrl;
                ViewData["OgImageAlt"] = ogImageAlt;
                ViewData["OgImageWidth"] = 480;
                ViewData["OgImageHeight"] = 360;
                ViewData["MetaKeywords"] = $"{SeoTextHelper.ProductKeywords(product.Name)}, {video.VideoType} videosu, program kurulum videosu, program kullanım videosu";
                ViewData["JsonLd"] = BuildVideoJsonLd(
                    video,
                    product,
                    displayTitle,
                    videoMetaDescription,
                    canonicalPath,
                    thumbnailUrl,
                    Request.PathBase.Value ?? string.Empty);
                ViewData["SkipActiveCampaign"] = true;

                return View(model);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Video detay sayfasi zaman asimina ugradi. Slug: {Slug}", cleanSlug);
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video detay sayfasi acilamadi. Slug: {Slug}", cleanSlug);
                return RedirectToAction(nameof(Index));
            }
        }

        private async Task LocalizeCardsAsync(
            List<ProductVideoCardViewModel> cards,
            string languageCode,
            CancellationToken cancellationToken)
        {
            if (cards.Count == 0)
                return;

            var merged = await _localizationCatalog.GetTranslationMapAsync(languageCode, cancellationToken);

            string Localize(string source)
            {
                var normalized = LocalizationTextKey.Normalize(source);
                if (string.IsNullOrWhiteSpace(normalized))
                    return source;

                var key = LocalizationTextKey.Create(normalized);
                if (merged.TryGetValue(key, out var translatedValue)
                    && !string.IsNullOrWhiteSpace(translatedValue))
                    return translatedValue;
                return source;
            }

            foreach (var card in cards)
            {
                card.ProductName = Localize(card.ProductName);
                card.ProductDescription = Localize(card.ProductDescription);
                card.DisplayTitle = Localize(card.DisplayTitle);
                card.VideoType = Localize(card.VideoType);
            }
        }

        private static ProductVideoCardViewModel BuildCard(ProductVideo video)
        {
            var product = video.Product ?? new Product();
            return new ProductVideoCardViewModel
            {
                Id = video.Id,
                Slug = video.Slug,
                ProductName = product.Name,
                ProductDescription = GetProductDescription(product),
                ProductUrl = GetProductPublicUrl(product),
                DisplayTitle = GetDisplayTitle(video, product),
                VideoType = string.IsNullOrWhiteSpace(video.VideoType) ? "Program Videosu" : video.VideoType,
                YouTubeVideoId = video.YouTubeVideoId,
                ThumbnailUrl = YouTubeVideoHelper.GetThumbnailUrl(video.YouTubeVideoId),
                IsFeatured = video.IsFeatured,
                SortOrder = video.SortOrder,
                CreatedAt = video.CreatedAt
            };
        }

        private static string GetDisplayTitle(ProductVideo video, Product product)
        {
            if (string.Equals(product.Slug, "nsx-veresiye-takip-pro-free", StringComparison.OrdinalIgnoreCase))
            {
                var type = (video.VideoType ?? string.Empty).Trim();
                var isInstallVideo = type.Contains("kurulum", StringComparison.OrdinalIgnoreCase) ||
                                     (video.Title ?? string.Empty).Contains("kurulum", StringComparison.OrdinalIgnoreCase);

                return isInstallVideo
                    ? "NSX Veresiye Takip Pro v1.0.5 Kurulum ve Kullanım Videosu"
                    : "NSX Veresiye Takip Pro v1.0.5 Tanıtım ve Kullanım Videosu";
            }

            return !string.IsNullOrWhiteSpace(video.Title)
                ? video.Title.Trim()
                : $"{product.Name} {GetVideoTypeLabel(video.VideoType)}";
        }

        private static string GetVideoTypeLabel(string? videoType)
        {
            var value = (videoType ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(value) ? "Videosu" : $"{value} Videosu";
        }

        private static string GetProductDescription(Product product)
        {
            if (string.Equals(product.Slug, "nsx-veresiye-takip-pro-free", StringComparison.OrdinalIgnoreCase))
            {
                return "NSX Veresiye Takip Pro v1.0.5: Cari, borç, tahsilat ve bakiye yönetimi; QR kod ile mobil erişim ve anlık bulut senkronizasyonu.";
            }

            return SeoTextHelper.ProductDescription(product.Name, product.MetaDescription, product.Description);
        }

        private static string BuildVideoMetaDescription(string displayTitle, Product product, string? videoType)
        {
            if (string.Equals(product.Slug, "nsx-veresiye-takip-pro-free", StringComparison.OrdinalIgnoreCase))
            {
                return TrimForMeta($"{displayTitle} videosunu izleyin. NSX Veresiye Takip Pro v1.0.5 artık QR ile Bulut ve Mobil Yönetim, canlı senkronizasyon, mobil müşteri arama, cari hareket, borç ve tahsilat özelliklerini de içerir.", 290);
            }

            var type = string.IsNullOrWhiteSpace(videoType) ? "tanıtım ve kullanım" : videoType.Trim().ToLowerInvariant();
            return TrimForMeta($"{displayTitle} videosunu izleyin. {product.Name} için {type} adımlarını gerçek program ekranlarıyla görün; özellikleri öğrenin ve ürün detaylarını inceleyin.", 290);
        }

        private static string GetProductPublicUrl(Product product)
        {
            var slug = (product.Slug ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(slug)
                ? $"/urun/{slug.ToLowerInvariant()}"
                : $"/store/detail/{product.Id}";
        }

        private static string TrimForMeta(string value, int maxLength)
        {
            var clean = string.Join(' ', (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (clean.Length <= maxLength)
                return clean;

            return clean[..Math.Max(1, maxLength - 1)].TrimEnd() + "…";
        }

        private static string BuildVideoJsonLd(
            ProductVideo video,
            Product product,
            string displayTitle,
            string description,
            string canonicalPath,
            string thumbnailUrl,
            string pathPrefix)
        {
            var canonicalUrl = SeoTextHelper.AbsoluteUrl(pathPrefix + canonicalPath);
            var productUrl = SeoTextHelper.AbsoluteUrl(pathPrefix + GetProductPublicUrl(product));
            var createdAt = video.CreatedAt;
            var uploadDateValue = createdAt.Kind switch
            {
                DateTimeKind.Utc => new DateTimeOffset(createdAt, TimeSpan.Zero),
                DateTimeKind.Local => new DateTimeOffset(createdAt),
                _ => new DateTimeOffset(createdAt, TimeSpan.FromHours(3))
            };
            var uploadDate = uploadDateValue.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

            var graph = new Dictionary<string, object?>
            {
                ["@context"] = "https://schema.org",
                ["@graph"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["@type"] = "VideoObject",
                        ["name"] = displayTitle,
                        ["description"] = TrimForMeta(description, 500),
                        ["thumbnailUrl"] = new[] { thumbnailUrl },
                        ["uploadDate"] = uploadDate,
                        ["contentUrl"] = YouTubeVideoHelper.NormalizeWatchUrl(video.YouTubeVideoId),
                        ["embedUrl"] = YouTubeVideoHelper.GetEmbedUrl(video.YouTubeVideoId),
                        ["url"] = canonicalUrl,
                        ["isFamilyFriendly"] = true,
                        ["publisher"] = new Dictionary<string, object?>
                        {
                            ["@type"] = "Organization",
                            ["name"] = "NSX Yazılım",
                            ["url"] = "https://www.nsxyazilim.com"
                        },
                        ["about"] = new Dictionary<string, object?>
                        {
                            ["@type"] = "SoftwareApplication",
                            ["name"] = product.Name,
                            ["url"] = productUrl
                        }
                    },
                    new Dictionary<string, object?>
                    {
                        ["@type"] = "BreadcrumbList",
                        ["itemListElement"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["@type"] = "ListItem",
                                ["position"] = 1,
                                ["name"] = "Ana Sayfa",
                                ["item"] = SeoTextHelper.AbsoluteUrl(string.IsNullOrWhiteSpace(pathPrefix) ? "/" : pathPrefix)
                            },
                            new Dictionary<string, object?>
                            {
                                ["@type"] = "ListItem",
                                ["position"] = 2,
                                ["name"] = "Videolar",
                                ["item"] = SeoTextHelper.AbsoluteUrl($"{pathPrefix}/videolar")
                            },
                            new Dictionary<string, object?>
                            {
                                ["@type"] = "ListItem",
                                ["position"] = 3,
                                ["name"] = displayTitle,
                                ["item"] = canonicalUrl
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(graph, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            return $"<script type=\"application/ld+json\">{json}</script>";
        }
    }
}
