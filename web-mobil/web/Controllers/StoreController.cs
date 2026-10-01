using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace NSYazilim.Web.Controllers
{
    public class StoreController : Controller
    {
        private static readonly HashSet<string> AllowedProgramPackageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".zip", ".rar", ".7z", ".msix", ".appx", ".msixbundle", ".appxbundle"
        };
        private const string BankTransferDescriptionInstruction = "Lütfen Havale/EFT yaparken açıklama Kısmını boş bırakınız.";
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;
        private readonly ClientIpService _clientIpService;
        private readonly SiteLocalizationService _siteLocalization;
        private readonly DownloadLocationUpdateQueue _downloadLocationQueue;
        private readonly BackgroundMailQueue _backgroundMailQueue;

        public StoreController(
            ApplicationDbContext context,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            ClientIpService clientIpService,
            SiteLocalizationService siteLocalization,
            DownloadLocationUpdateQueue downloadLocationQueue,
            BackgroundMailQueue backgroundMailQueue)
        {
            _context = context;
            _configuration = configuration;
            _environment = environment;
            _clientIpService = clientIpService;
            _siteLocalization = siteLocalization;
            _downloadLocationQueue = downloadLocationQueue;
            _backgroundMailQueue = backgroundMailQueue;
        }

        [HttpGet("store")]
        [HttpGet("store/index")]
        [HttpGet("yazilimlar")]
        public async Task<IActionResult> Index(string? q, string stock = "all", string sort = "recommended", string? category = null)
        {
            var requestPath = Request.Path.Value ?? "/store";
            if (!string.Equals(requestPath, "/store", StringComparison.OrdinalIgnoreCase))
                return RedirectPermanent($"{Request.PathBase}/store{Request.QueryString}");

            ViewData["Canonical"] = SeoTextHelper.AbsoluteUrl("/store");
            ViewData["Title"] = "Yazılımlar";
            ViewData["MetaTitle"] = "NSX İşletme Yazılımları | Satış, Finans, Servis ve Güvenlik";
            ViewData["MetaDescription"] = "NSX Kasa Defteri, Sigorta Acente ve Security Auditor Pro ile ücretsiz veresiye, barkodlu satış, cari takip, teknik servis, otomotiv ve klinik yazılımlarını inceleyin.";
            ViewData["MetaKeywords"] = SeoTextHelper.Keywords;
            if (Request.QueryString.HasValue)
                ViewData["Robots"] = "noindex, follow, noarchive";

            ViewBag.Query = q;
            ViewBag.Stock = stock;
            ViewBag.Sort = sort;
            var requestedCategory = ProductCategoryHelper.CreateKey(category);
            ViewBag.Category = requestedCategory;

            var query = _context.Products
                .Include(x => x.Images)
                .Where(x => !x.IsDeleted && x.IsActive);

            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                query = query.Where(x =>
                    x.Name.Contains(q) ||
                    (x.Description != null && x.Description.Contains(q)) ||
                    (x.MetaDescription != null && x.MetaDescription.Contains(q)));
            }

            if (stock == "in")
                query = query.Where(x => x.StockQuantity > 0);

            if (stock == "out")
                query = query.Where(x => x.StockQuantity <= 0);

            query = sort switch
            {
                "price_asc" => query.OrderBy(x => x.YearlyPrice),
                "price_desc" => query.OrderByDescending(x => x.YearlyPrice),
                "name" => query.OrderBy(x => x.Name),
                "rating" => query.OrderByDescending(x =>
                    _context.ProductComments
                        .Where(c => c.ProductId == x.Id && c.IsApproved && !c.IsDeleted)
                        .Select(c => (double?)c.Rating)
                        .Average() ?? 0),
                "new" => query.OrderByDescending(x => x.CreatedAt),
                _ => query.OrderByDescending(x => x.CreatedAt)
            };

            var products = await query.ToListAsync();
            if (!string.IsNullOrWhiteSpace(requestedCategory))
            {
                products = products
                    .Where(x => string.Equals(ProductCategoryHelper.GetKey(x), requestedCategory, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var ids = products.Select(x => x.Id).ToList();

            var ratingRaw = await _context.ProductComments
                .Where(x => ids.Contains(x.ProductId) && x.IsApproved && !x.IsDeleted)
                .GroupBy(x => x.ProductId)
                .Select(g => new
                {
                    ProductId = g.Key,
                    Average = g.Average(x => x.Rating),
                    Count = g.Count()
                })
                .ToListAsync();

            ViewBag.RatingAverage = ratingRaw.ToDictionary(x => x.ProductId, x => x.Average);
            ViewBag.RatingCount = ratingRaw.ToDictionary(x => x.ProductId, x => x.Count);

            var activeCampaigns = await GetActiveCampaignsAsync();
            ViewBag.ActiveCampaigns = activeCampaigns;
            ViewBag.ProductCampaigns = products.ToDictionary(x => x.Id, x => GetProductCampaign(activeCampaigns, x));

            return View(products);
        }

        [HttpGet("store/detail/{id:int}")]
        public async Task<IActionResult> Detail(int id)
        {
            var product = await _context.Products
                .Include(x => x.Images)
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted && x.IsActive);

            if (product == null)
                return NotFound();

            if (!string.IsNullOrWhiteSpace(product.Slug))
                return RedirectPermanent($"{Request.PathBase}{GetProductPublicUrl(product)}");

            return await ProductDetailView(product);
        }

        [HttpGet]
        public async Task<IActionResult> DetailBySlug(string slug)
        {
            var requestedSlug = (slug ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(requestedSlug))
                return NotFound();

            var requestedSlugLower = requestedSlug.ToLowerInvariant();

            var product = await _context.Products
                .Include(x => x.Images)
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x =>
                    !x.IsDeleted &&
                    x.IsActive &&
                    x.Slug != null &&
                    x.Slug.ToLower() == requestedSlugLower);

            if (product == null)
            {
                var candidates = await _context.Products
                    .Include(x => x.Images)
                    .Include(x => x.Files)
                    .Where(x => !x.IsDeleted && x.IsActive)
                    .ToListAsync();

                product = candidates.FirstOrDefault(x =>
                        string.Equals((x.Slug ?? string.Empty).Trim(), requestedSlug, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault(x =>
                        string.Equals(CreateSlug(x.Name), requestedSlugLower, StringComparison.OrdinalIgnoreCase));
            }

            if (product == null)
                return NotFound();

            var canonicalPath = GetProductPublicUrl(product);
            var requestedPath = $"/urun/{requestedSlug}";

            if (!string.Equals(canonicalPath, requestedPath, StringComparison.Ordinal))
                return RedirectPermanent($"{Request.PathBase}{canonicalPath}");

            return await ProductDetailView(product);
        }

        private static string GetProductPublicUrl(Product product)
        {
            var slug = (product.Slug ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(slug)
                ? $"/urun/{slug.ToLowerInvariant()}"
                : $"/store/detail/{product.Id}";
        }

        private static string CreateSlug(string text)
        {
            var value = (text ?? string.Empty).Trim().ToLowerInvariant();
            value = value.Replace("ç", "c").Replace("ğ", "g").Replace("ı", "i").Replace("ö", "o").Replace("ş", "s").Replace("ü", "u");
            value = value.Replace("Ç", "c").Replace("Ğ", "g").Replace("İ", "i").Replace("Ö", "o").Replace("Ş", "s").Replace("Ü", "u");

            var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
            var slug = new string(chars);

            while (slug.Contains("--"))
                slug = slug.Replace("--", "-");

            slug = slug.Trim('-');
            return string.IsNullOrWhiteSpace(slug) ? "urun" : slug;
        }

        private async Task<IActionResult> ProductDetailView(Product product)
        {
            var comments = await _context.ProductComments
                .Include(x => x.User)
                .Where(x => x.ProductId == product.Id && x.IsApproved && !x.IsDeleted)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            var averageRating = comments.Any()
                ? comments.Average(x => x.Rating)
                : 0;

            var mainImage = product.Images?
                .OrderByDescending(x => x.IsMain)
                .ThenBy(x => x.SortOrder)
                .FirstOrDefault()?.ImagePath;

            var seoProfile = ProductSeoCatalog.Find(product);
            var publicProductName = seoProfile?.ProductCode == "NSXVERESIYETAKIPPROFREE"
                ? seoProfile.Name
                : product.Name;
            ViewData["Title"] = publicProductName;
            ViewData["MetaTitle"] = seoProfile?.MetaTitle ?? SeoTextHelper.ProductTitle(publicProductName, product.MetaTitle);
            ViewData["MetaDescription"] = seoProfile?.MetaDescription ?? SeoTextHelper.ProductDescription(publicProductName, product.MetaDescription, product.Description);
            ViewData["MetaKeywords"] = seoProfile == null
                ? SeoTextHelper.ProductKeywords(publicProductName)
                : string.Join(", ", new[] { publicProductName }.Concat(seoProfile.Keywords).Append("NSX Yazılım").Distinct(StringComparer.OrdinalIgnoreCase));
            ViewData["Canonical"] = SeoTextHelper.AbsoluteUrl(GetProductPublicUrl(product));
            ViewData["OgType"] = "product";
            ViewData["OgImageAlt"] = $"{publicProductName} ürün görseli";
            if (!string.IsNullOrWhiteSpace(mainImage))
                ViewData["OgImage"] = SeoTextHelper.AbsoluteUrl(mainImage);

            var productUrl = SeoTextHelper.AbsoluteUrl(GetProductPublicUrl(product));
            var productDescription = seoProfile?.MetaDescription ?? SeoTextHelper.ProductDescription(product.Name, product.MetaDescription, product.Description);
            var productImage = !string.IsNullOrWhiteSpace(mainImage)
                ? SeoTextHelper.AbsoluteUrl(mainImage)
                : SeoTextHelper.AbsoluteUrl("/nsx-assets/logo-brand.webp");
            var activeCampaign = GetProductCampaign(await GetActiveCampaignsAsync(), product);
            var currentLanguage = await _siteLocalization.GetCurrentLanguageAsync(HttpContext.RequestAborted);
            var schemaProductName = await _siteLocalization.TranslateAsync(publicProductName, currentLanguage.Code, HttpContext.RequestAborted);
            // Schema açıklaması da ürünün gerçek açıklamasını korur; genel/kısa fallback yok.
            var schemaProductDescription = await _siteLocalization.TranslateAsync(productDescription, currentLanguage.Code, HttpContext.RequestAborted);
            var schemaCategory = await _siteLocalization.TranslateAsync(ProductCategoryHelper.GetLabel(product), currentLanguage.Code, HttpContext.RequestAborted);
            var schemaBrandName = await _siteLocalization.TranslateAsync("NSX Yazılım", currentLanguage.Code, HttpContext.RequestAborted);

            var productSchema = new Dictionary<string, object?>
            {
                ["@context"] = "https://schema.org",
                ["@type"] = new[] { "Product", "SoftwareApplication" },
                ["name"] = schemaProductName,
                ["description"] = schemaProductDescription,
                ["category"] = schemaCategory,
                ["applicationCategory"] = "BusinessApplication",
                ["operatingSystem"] = "Windows",
                ["image"] = productImage,
                ["url"] = productUrl,
                ["sku"] = string.IsNullOrWhiteSpace(product.ProductCode) ? $"NSX-{product.Id}" : product.ProductCode.Trim().ToUpperInvariant(),
                ["mpn"] = string.IsNullOrWhiteSpace(product.ProductCode) ? $"NSX-{product.Id}" : product.ProductCode.Trim().ToUpperInvariant(),
                ["brand"] = new Dictionary<string, object?>
                {
                    ["@type"] = "Brand",
                    ["name"] = schemaBrandName
                },
                ["offers"] = BuildProductOfferSchema(product, productUrl, activeCampaign, schemaBrandName)
            };

            if (seoProfile?.ProductCode == "NSXVERESIYETAKIPPROFREE")
            {
                var videoName = await _siteLocalization.TranslateAsync(
                    "NSX Veresiye Takip Pro Free Kurulum ve Kullanım",
                    currentLanguage.Code,
                    HttpContext.RequestAborted);
                var videoDescription = await _siteLocalization.TranslateAsync(
                    "NSX Veresiye Takip Pro Free kurulum ve temel kullanım videosu.",
                    currentLanguage.Code,
                    HttpContext.RequestAborted);
                productSchema["subjectOf"] = new Dictionary<string, object?>
                {
                    ["@type"] = "VideoObject",
                    ["name"] = videoName,
                    ["description"] = videoDescription,
                    ["thumbnailUrl"] = "https://img.youtube.com/vi/ypsXQY6iOl4/maxresdefault.jpg",
                    ["uploadDate"] = "2026-07-04",
                    ["duration"] = "PT6M33S",
                    ["embedUrl"] = "https://www.youtube-nocookie.com/embed/ypsXQY6iOl4",
                    ["contentUrl"] = "https://www.youtube.com/watch?v=ypsXQY6iOl4"
                };
            }

            if (comments.Any())
            {
                productSchema["aggregateRating"] = new Dictionary<string, object?>
                {
                    ["@type"] = "AggregateRating",
                    ["ratingValue"] = Math.Round(averageRating, 1),
                    ["reviewCount"] = comments.Count,
                    ["bestRating"] = 5,
                    ["worstRating"] = 1
                };

                var reviews = BuildProductReviewSchema(schemaProductName, comments);
                if (reviews.Any())
                    productSchema["review"] = reviews;
            }

            var productSchemaJson = JsonSerializer.Serialize(productSchema, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            ViewData["JsonLd"] = $"<script type=\"application/ld+json\">{productSchemaJson}</script>";

            ViewBag.Comments = comments;
            ViewBag.AverageRating = averageRating;
            ViewBag.CommentCount = comments.Count;
            ViewBag.ActiveCampaign = activeCampaign;
            PrepareBankTransferViewBag(product);

            return View("Detail", product);
        }

        private static Dictionary<string, object?> BuildProductOfferSchema(Product product, string productUrl, Campaign? campaign, string sellerName)
        {
            var basePrice = GetProductSchemaPrice(product);
            var discount = campaign == null ? 0 : CalculateCampaignDiscount(basePrice, campaign);
            var offer = new Dictionary<string, object?>
            {
                ["@type"] = "Offer",
                ["url"] = productUrl,
                ["priceCurrency"] = "TRY",
                ["price"] = Math.Max(0, basePrice - discount),
                ["availability"] = product.StockQuantity > 0 ? "https://schema.org/InStock" : "https://schema.org/OutOfStock",
                ["itemCondition"] = "https://schema.org/NewCondition",
                ["seller"] = new Dictionary<string, object?>
                {
                    ["@type"] = "Organization",
                    ["name"] = sellerName,
                    ["url"] = SeoTextHelper.AbsoluteUrl("/"),
                    ["telephone"] = "+90 543 462 42 26"
                },
                ["shippingDetails"] = BuildShippingDetailsSchema(),
                ["hasMerchantReturnPolicy"] = BuildMerchantReturnPolicySchema()
            };

            if (discount > 0 && campaign?.EndDate is DateTime endDate)
                offer["priceValidUntil"] = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            return offer;
        }

        private static decimal GetProductSchemaPrice(Product product)
        {
            if (product.YearlyPrice > 0)
                return product.YearlyPrice;

            if (product.LifetimePrice > 0)
                return product.LifetimePrice;

            return 0;
        }

        private static Dictionary<string, object?> BuildShippingDetailsSchema()
        {
            return new Dictionary<string, object?>
            {
                ["@type"] = "OfferShippingDetails",
                ["shippingRate"] = new Dictionary<string, object?>
                {
                    ["@type"] = "MonetaryAmount",
                    ["value"] = 0,
                    ["currency"] = "TRY"
                },
                ["shippingDestination"] = new Dictionary<string, object?>
                {
                    ["@type"] = "DefinedRegion",
                    ["addressCountry"] = "TR"
                },
                ["deliveryTime"] = new Dictionary<string, object?>
                {
                    ["@type"] = "ShippingDeliveryTime",
                    ["handlingTime"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "QuantitativeValue",
                        ["minValue"] = 0,
                        ["maxValue"] = 1,
                        ["unitCode"] = "DAY"
                    },
                    ["transitTime"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "QuantitativeValue",
                        ["minValue"] = 0,
                        ["maxValue"] = 0,
                        ["unitCode"] = "DAY"
                    }
                }
            };
        }

        private static Dictionary<string, object?> BuildMerchantReturnPolicySchema()
        {
            return new Dictionary<string, object?>
            {
                ["@type"] = "MerchantReturnPolicy",
                ["applicableCountry"] = "TR",
                ["returnPolicyCategory"] = "https://schema.org/MerchantReturnNotPermitted",
                ["url"] = SeoTextHelper.AbsoluteUrl("/iade-politikasi")
            };
        }

        private static List<Dictionary<string, object?>> BuildProductReviewSchema(string productName, List<ProductComment> comments)
        {
            return comments
                .Where(x => x.Rating > 0 && !string.IsNullOrWhiteSpace(x.Comment))
                .OrderByDescending(x => x.CreatedAt)
                .Take(10)
                .Select(x => new Dictionary<string, object?>
                {
                    ["@type"] = "Review",
                    ["name"] = productName,
                    ["reviewBody"] = CleanSchemaText(x.Comment, productName),
                    ["datePublished"] = x.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["author"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "Person",
                        ["name"] = CleanSchemaText(x.FullName, CleanSchemaText(x.User?.FullName, "NSX Customer"))
                    },
                    ["reviewRating"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "Rating",
                        ["ratingValue"] = x.Rating,
                        ["bestRating"] = 5,
                        ["worstRating"] = 1
                    }
                })
                .ToList();
        }

        private static string CleanSchemaText(string? value, string fallback)
        {
            var cleaned = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
        }

        private void PrepareBankTransferViewBag(Product product)
        {
            var bankName = CleanConfig(_configuration["BankTransfer:BankName"]);
            var accountHolder = CleanConfig(_configuration["BankTransfer:AccountHolder"]);
            var iban = CleanConfig(_configuration["BankTransfer:Iban"]);
            var branch = CleanConfig(_configuration["BankTransfer:Branch"]);

            ViewBag.BankTransferBankName = bankName;
            ViewBag.BankTransferAccountHolder = accountHolder;
            ViewBag.BankTransferIban = iban;
            ViewBag.BankTransferBranch = branch;
            ViewBag.BankTransferYearlyDescription = BankTransferDescriptionInstruction;
            ViewBag.BankTransferLifetimeDescription = BankTransferDescriptionInstruction;
            ViewBag.HasBankTransferInfo = !string.IsNullOrWhiteSpace(bankName)
                && !string.IsNullOrWhiteSpace(accountHolder)
                && !string.IsNullOrWhiteSpace(iban);
        }

        private static string CleanConfig(string? value)
        {
            var cleaned = (value ?? string.Empty).Trim();
            if (cleaned.Contains("BURAYA", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return cleaned;
        }

        [HttpGet("store/downloaddemo")]
        public async Task<IActionResult> DownloadDemo(int productId)
        {
            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            Response.Headers["X-NSX-Demo-Fix"] = "2026-07-20-v3";

            if (User?.Identity?.IsAuthenticated != true || !User.IsInRole("User"))
            {
                TempData["Warning"] = "Demo indirmek için önce üye girişi yapmalısınız.";
                var returnUrl = Request.Path + Request.QueryString.ToString();
                var authLanguage = await _siteLocalization.GetCurrentLanguageAsync(HttpContext.RequestAborted);
                return RedirectToAction("Login", "Account", new { returnUrl, uiLang = authLanguage.Code });
            }

            var product = await _context.Products
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted && x.IsActive);

            if (product == null)
                return NotFound();

            if (product.StockQuantity <= 0)
            {
                TempData["Warning"] = "Bu ürün şu anda stokta yok. Demo indirme kapalıdır.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            var isFreeProduct = IsFreeProduct(product);

            // Ücretsiz NSX Veresiye ürününde hiçbir indirme yolu sabit setup dosyasını
            // doğrudan vermesin. Eski/demo bağlantıları da kişisel tek-EXE akışına gider.
            if (isFreeProduct &&
                string.Equals(GetProductCodeForLicense(product), "NSXVERESIYETAKIPPROFREE", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(FreeDownloadStart), new { productId = product.Id });
            }

            var candidateFiles = GetLatestProductFiles(product, "Demo").ToList();

            var selectedFile = candidateFiles
                .Where(IsProgramPackageFile)
                .Select(x => new
                {
                    File = x,
                    PhysicalPath = GetProductFilePhysicalPath(x.FilePath)
                })
                .FirstOrDefault(x => System.IO.File.Exists(x.PhysicalPath) && HasExpectedProgramPackageSignature(x.PhysicalPath, x.File));

            if (selectedFile == null)
            {
                TempData["Error"] = isFreeProduct
                    ? "İndirme dosyası sunucuda bulunamadı."
                    : "Demo dosyası sunucuda bulunamadı.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            var filePath = selectedFile.PhysicalPath;
            var downloadName = string.IsNullOrWhiteSpace(selectedFile.File.OriginalFileName)
                ? Path.GetFileName(filePath)
                : selectedFile.File.OriginalFileName;

            var userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(userIdText, out var userId))
            {
                var currentUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);

                var customerEmail = currentUser?.Email ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
                var customerName = currentUser?.FullName ?? User.FindFirstValue(ClaimTypes.Name) ?? "Değerli müşterimiz";
                var demoDownload = CreateDownloadAudit(product.Id, userId, customerName, customerEmail, currentUser?.Phone ?? User.FindFirstValue(ClaimTypes.MobilePhone));

                _context.DemoDownloads.Add(demoDownload);

                License? freeLicense = null;
                if (isFreeProduct)
                    freeLicense = await EnsureFreeLicenseAsync(userId, product.Id);

                await _context.SaveChangesAsync();
                _downloadLocationQueue.TryQueue(demoDownload.Id);

                if (isFreeProduct && freeLicense != null)
                    QueueFreeLicenseEmail(customerEmail, customerName, product, freeLicense);
            }

            return PhysicalFile(filePath, "application/octet-stream", downloadName);
        }

        [HttpGet("store/free-download-start")]
        public async Task<IActionResult> FreeDownloadStart(int productId)
        {
            var user = await GetCurrentCustomerAsync();
            if (user == null)
            {
                TempData["Warning"] = "Ücretsiz indirmeyi başlatmak için hesabınıza giriş yapın. Ödeme veya kart bilgisi gerekmez.";
                var authLanguage = await _siteLocalization.GetCurrentLanguageAsync(HttpContext.RequestAborted);
                return RedirectToAction("Login", "Account", new
                {
                    returnUrl = $"/store/free-download-start?productId={productId}",
                    uiLang = authLanguage.Code
                });
            }

            var product = await _context.Products
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted && x.IsActive);

            if (product == null)
                return NotFound();

            if (!IsFreeProduct(product))
            {
                TempData["Warning"] = "Bu ürün ücretsiz indirme kapsamında değil.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            if (product.StockQuantity <= 0)
            {
                TempData["Warning"] = "Bu ürün şu anda stokta yok. İndirme kapalıdır.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            var hasDownloadFile = GetFreeDownloadFiles(product)
                .Where(IsProgramPackageFile)
                .Select(x => new
                {
                    File = x,
                    PhysicalPath = GetProductFilePhysicalPath(x.FilePath)
                })
                .Any(x => System.IO.File.Exists(x.PhysicalPath) && HasExpectedProgramPackageSignature(x.PhysicalPath, x.File));

            if (!hasDownloadFile)
            {
                TempData["Error"] = "Dosya sunucuda bulunamadı.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            var seoProfile = ProductSeoCatalog.Find(product);
            ViewData["Title"] = "Ücretsiz İndirme Hazırlanıyor";
            ViewData["MetaDescription"] = "NSX ücretsiz yazılım indirmeniz güvenli şekilde hazırlanıyor.";
            ViewData["Robots"] = "noindex, nofollow, noarchive";
            ViewData["SkipActiveCampaign"] = true;
            ViewBag.PublicProductName = seoProfile?.ProductCode == "NSXVERESIYETAKIPPROFREE"
                ? seoProfile.Name
                : product.Name;
            ViewBag.DownloadTrackingToken = Guid.NewGuid().ToString("N");
            ViewBag.RegistrationCompleted = string.Equals(
                TempData["NSX.RegistrationCompleted"]?.ToString(),
                "1",
                StringComparison.Ordinal);

            return View(product);
        }

        [HttpGet("store/downloadfree")]
        public async Task<IActionResult> DownloadFree(int productId, string? trackingToken = null)
        {
            var user = await GetCurrentCustomerAsync();

            if (user == null)
            {
                TempData["Warning"] = "Ücretsiz indirmek için önce üye girişi yapmalısınız.";
                var authLanguage = await _siteLocalization.GetCurrentLanguageAsync(HttpContext.RequestAborted);
                return RedirectToAction("Login", "Account", new
                {
                    returnUrl = $"/store/downloadfree?productId={productId}",
                    uiLang = authLanguage.Code
                });
            }

            var product = await _context.Products
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted && x.IsActive);

            if (product == null)
                return NotFound();

            if (!IsFreeProduct(product))
            {
                TempData["Warning"] = "Bu ürün ücretsiz indirme kapsamında değil.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            if (product.StockQuantity <= 0)
            {
                TempData["Warning"] = "Bu ürün şu anda stokta yok. İndirme kapalıdır.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            var selectedFile = GetFreeDownloadFiles(product)
                .Where(IsProgramPackageFile)
                .Select(x => new
                {
                    File = x,
                    PhysicalPath = GetProductFilePhysicalPath(x.FilePath)
                })
                .FirstOrDefault(x => System.IO.File.Exists(x.PhysicalPath) && HasExpectedProgramPackageSignature(x.PhysicalPath, x.File));

            if (selectedFile == null)
            {
                TempData["Error"] = "Dosya sunucuda bulunamadı.";
                return LocalRedirect(GetProductPublicUrl(product));
            }

            var freeLicense = await EnsureFreeLicenseAsync(user.Id, product.Id);
            var productCode = GetProductCodeForLicense(product);
            var isPersonalizedFreeVeresiye = string.Equals(productCode, "NSXVERESIYETAKIPPROFREE", StringComparison.OrdinalIgnoreCase);

            string? rawToken = null;
            if (isPersonalizedFreeVeresiye)
            {
                rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _context.FreeLicenseActivationTokens.Add(new FreeLicenseActivationToken
                {
                    UserId = user.Id,
                    ProductId = product.Id,
                    LicenseId = freeLicense.Id,
                    TokenHash = FreeLicenseActivationController.HashToken(rawToken),
                    ProductCode = "NSXVERESIYETAKIPPROFREE",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24),
                    CreatedIpAddress = _clientIpService.GetClientIp(HttpContext)
                });
            }

            // Özel Free akışı eskiden DemoDownloads tablosuna hiç kayıt atmıyordu.
            // Bu yüzden gerçek ücretsiz indirmeler admin istatistiklerinde sıfır görünebiliyordu.
            var downloadAudit = CreateDownloadAudit(product.Id, user.Id, user.FullName, user.Email, user.Phone);
            _context.DemoDownloads.Add(downloadAudit);
            await _context.SaveChangesAsync();

            _downloadLocationQueue.TryQueue(downloadAudit.Id);
            QueueFreeLicenseEmail(user.Email, user.FullName, product, freeLicense);

            var filePath = selectedFile.PhysicalPath;
            var programFile = selectedFile.File;

            // Yalnızca NSX Ücretsiz Veresiye için kişisel tek-EXE aktivasyon paketi hazırlanır.
            if (isPersonalizedFreeVeresiye && !string.IsNullOrWhiteSpace(rawToken))
            {
                MarkFreeDownloadSuccess(trackingToken);
                return await WritePersonalizedFreeInstallerAsync(filePath, programFile, rawToken);
            }

            // Başka bir ücretsiz ürün eklenirse DownloadFree artık 404 vermez; doğrulanmış
            // program paketini normal güvenli indirme olarak sunar.
            var downloadName = string.IsNullOrWhiteSpace(programFile.OriginalFileName)
                ? Path.GetFileName(filePath)
                : Path.GetFileName(programFile.OriginalFileName);

            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            MarkFreeDownloadSuccess(trackingToken);
            return PhysicalFile(filePath, "application/octet-stream", downloadName);
        }

        private void MarkFreeDownloadSuccess(string? trackingToken)
        {
            var token = (trackingToken ?? string.Empty).Trim();
            if (token.Length != 32 || token.Any(ch => !Uri.IsHexDigit(ch)))
                return;

            Response.Cookies.Append(
                "NSX.FreeDownloadSuccess",
                token.ToLowerInvariant(),
                new CookieOptions
                {
                    HttpOnly = false,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    MaxAge = TimeSpan.FromMinutes(2),
                    IsEssential = true
                });
        }


        [HttpGet("store/freeactivationpayload")]
        public async Task<IActionResult> FreeActivationPayload(string token)
        {
            var user = await GetCurrentCustomerAsync();
            if (user == null) return Unauthorized();
            var hash = FreeLicenseActivationController.HashToken((token ?? string.Empty).Trim());
            var row = await _context.FreeLicenseActivationTokens.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TokenHash == hash && x.UserId == user.Id && x.ProductCode == "NSXVERESIYETAKIPPROFREE" && x.UsedAt == null && x.ExpiresAt >= DateTime.UtcNow);
            if (row == null) return NotFound();
            var json = JsonSerializer.Serialize(new { token });
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "nsx-free-license.json");
        }

        [HttpGet("store/freeprogramfile")]
        public async Task<IActionResult> FreeProgramFile(int productId, int fileId, string? token)
        {
            var user = await GetCurrentCustomerAsync();
            if (user == null) return Unauthorized();

            var normalizedToken = (token ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedToken)) return NotFound();

            var hash = FreeLicenseActivationController.HashToken(normalizedToken);
            var valid = await _context.FreeLicenseActivationTokens.AsNoTracking()
                .AnyAsync(x => x.TokenHash == hash && x.UserId == user.Id && x.ProductId == productId && x.ProductCode == "NSXVERESIYETAKIPPROFREE" && x.UsedAt == null && x.ExpiresAt >= DateTime.UtcNow);
            if (!valid) return NotFound();
            var product = await _context.Products.Include(x => x.Files).FirstOrDefaultAsync(x => x.Id == productId && x.IsActive && !x.IsDeleted);
            if (product == null || !string.Equals(GetProductCodeForLicense(product), "NSXVERESIYETAKIPPROFREE", StringComparison.OrdinalIgnoreCase)) return NotFound();
            var file = product.Files.FirstOrDefault(x => x.Id == fileId);
            if (file == null) return NotFound();
            var path = GetProductFilePhysicalPath(file.FilePath);
            if (!System.IO.File.Exists(path)) return NotFound();

            return await WritePersonalizedFreeInstallerAsync(path, file, normalizedToken);
        }


        private async Task<IActionResult> WritePersonalizedFreeInstallerAsync(string setupPath, ProductFile file, string token)
        {
            // Dosyanın sonuna eklenen veri Inno Setup tarafından yok sayılır. Kurulum, çalıştırılan
            // kaynak EXE'yi ProgramData'ya kopyalar; masaüstü programı tokenı buradan okuyup eşleşir.
            var payloadJson = JsonSerializer.Serialize(new
            {
                token,
                productCode = "NSXVERESIYETAKIPPROFREE",
                createdAtUtc = DateTime.UtcNow
            });
            var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payloadJson);
            var lengthBytes = BitConverter.GetBytes(payloadBytes.Length);
            var markerBytes = System.Text.Encoding.ASCII.GetBytes("NSXFREEACTV1");

            var originalName = string.IsNullOrWhiteSpace(file.OriginalFileName)
                ? Path.GetFileName(setupPath)
                : Path.GetFileName(file.OriginalFileName);

            // Kullanıcı alıştığı normal dosya adını görür; içerik yine hesabına özel tokenlıdır.
            var safeDownloadName = string.IsNullOrWhiteSpace(originalName)
                ? "NSX_Veresiye_Defteri_Free_Setup_v1.0.2.exe"
                : originalName.Replace("\"", string.Empty);

            var fileInfo = new FileInfo(setupPath);
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = fileInfo.Length + payloadBytes.Length + lengthBytes.Length + markerBytes.Length;
            Response.Headers.ContentDisposition = $"attachment; filename=\"{safeDownloadName}\"";
            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            Response.Headers["X-NSX-Personalized-Installer"] = "1";

            await using (var setupStream = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            {
                await setupStream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
            }

            await Response.Body.WriteAsync(payloadBytes, HttpContext.RequestAborted);
            await Response.Body.WriteAsync(lengthBytes, HttpContext.RequestAborted);
            await Response.Body.WriteAsync(markerBytes, HttpContext.RequestAborted);
            return new EmptyResult();
        }



        private DemoDownload CreateDownloadAudit(int productId, int userId, string? fullName, string? email, string? phone)
        {
            return new DemoDownload
            {
                ProductId = productId,
                UserId = userId,
                FullName = (fullName ?? string.Empty).Trim(),
                Email = (email ?? string.Empty).Trim(),
                Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
                IpAddress = _clientIpService.GetClientIp(HttpContext),
                UserAgent = Request.Headers["User-Agent"].ToString(),
                DownloadedAt = DateTime.Now
            };
        }

        private async Task<List<Campaign>> GetActiveCampaignsAsync()
        {
            var now = DateTime.Now;
            var campaigns = new List<Campaign>();

            try
            {
                var couponCampaigns = await _context.CampaignCoupons
                    .AsNoTracking()
                    .Where(x => x.IsActive && !x.IsDeleted)
                    .Where(x => x.StartDate == null || x.StartDate <= now)
                    .Where(x => x.EndDate == null || x.EndDate >= now)
                    .Where(x => x.UsageLimit == null || x.UsageLimit <= 0 || x.UsedCount < x.UsageLimit)
                    .OrderByDescending(x => x.Id)
                    .Select(x => new Campaign
                    {
                        Id = -x.Id,
                        Code = x.Code,
                        Name = x.Title,
                        Description = x.Title,
                        MinimumCartTotal = x.MinimumCartAmount ?? 0,
                        DiscountType = x.DiscountType,
                        DiscountValue = x.DiscountValue,
                        IsActive = true,
                        StartDate = x.StartDate,
                        EndDate = x.EndDate,
                        CreatedAt = x.CreatedAt
                    })
                    .ToListAsync();

                campaigns.AddRange(couponCampaigns);
            }
            catch
            {
            }

            try
            {
                var legacyCampaigns = await _context.Campaigns
                    .AsNoTracking()
                    .Where(x => x.IsActive)
                    .Where(x => x.StartDate == null || x.StartDate <= now)
                    .Where(x => x.EndDate == null || x.EndDate >= now)
                    .OrderByDescending(x => x.Id)
                    .ToListAsync();

                campaigns.AddRange(legacyCampaigns);
            }
            catch
            {
            }

            return campaigns
                .Where(x => x.DiscountValue > 0)
                .Where(x => !string.IsNullOrWhiteSpace(x.Code) || !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Code) ? $"ID:{x.Id}" : x.Code.Trim().ToUpperInvariant())
                .Select(g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First())
                .OrderByDescending(x => x.DiscountValue)
                .ThenByDescending(x => x.CreatedAt)
                .ToList();
        }

        private Campaign? GetProductCampaign(List<Campaign> campaigns, Product product)
        {
            if (campaigns == null || !campaigns.Any())
                return null;

            var productPrice = Math.Max(product.YearlyPrice, product.LifetimePrice);

            return campaigns
                .Where(x => x.MinimumCartTotal <= 0 || x.MinimumCartTotal <= productPrice)
                .OrderByDescending(x => CalculateCampaignDiscount(productPrice, x))
                .ThenByDescending(x => x.CreatedAt)
                .FirstOrDefault();
        }

        private static decimal CalculateCampaignDiscount(decimal amount, Campaign campaign)
        {
            if (amount <= 0 || campaign.DiscountValue <= 0 || (campaign.MinimumCartTotal > 0 && amount < campaign.MinimumCartTotal))
                return 0;

            var fixedDiscount = string.Equals(campaign.DiscountType, "Fixed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(campaign.DiscountType, "FixedAmount", StringComparison.OrdinalIgnoreCase)
                || string.Equals(campaign.DiscountType, "Amount", StringComparison.OrdinalIgnoreCase);
            var discount = fixedDiscount ? campaign.DiscountValue : amount * campaign.DiscountValue / 100m;
            return Math.Round(Math.Clamp(discount, 0, amount), 2, MidpointRounding.AwayFromZero);
        }

        private async Task<User?> GetCurrentCustomerAsync()
        {
            var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var email = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;

            if (int.TryParse(idValue, out var id))
            {
                return await _context.Users.FirstOrDefaultAsync(x =>
                    x.Id == id &&
                    !x.IsDeleted &&
                    x.IsActive &&
                    x.Role == "User");
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                return await _context.Users.FirstOrDefaultAsync(x =>
                    x.Email == email &&
                    !x.IsDeleted &&
                    x.IsActive &&
                    x.Role == "User");
            }

            return null;
        }

        private static IEnumerable<ProductFile> GetFreeDownloadFiles(Product product)
        {
            return GetLatestProductFiles(product, "Program")
                .Concat(GetLatestProductFiles(product, "Demo"));
        }

        private static ProductFile? GetLatestProductFile(Product product, string fileType)
        {
            return GetLatestProductFiles(product, fileType).FirstOrDefault();
        }

        private static IEnumerable<ProductFile> GetLatestProductFiles(Product product, string fileType)
        {
            return product.Files
                .Where(x => string.Equals((x.FileType ?? string.Empty).Trim(), fileType, StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
                .OrderByDescending(x => x.UploadedAt)
                .ThenByDescending(x => x.Id);
        }

        private static bool IsProgramPackageFile(ProductFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
                return false;

            var displayName = string.IsNullOrWhiteSpace(file.OriginalFileName)
                ? file.FilePath
                : file.OriginalFileName;

            return AllowedProgramPackageExtensions.Contains(Path.GetExtension(displayName));
        }

        private static bool HasExpectedProgramPackageSignature(string physicalPath, ProductFile file)
        {
            try
            {
                var displayName = string.IsNullOrWhiteSpace(file.OriginalFileName)
                    ? file.FilePath
                    : file.OriginalFileName;
                var extension = Path.GetExtension(displayName);
                var header = new byte[8];

                using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var read = stream.Read(header, 0, header.Length);

                if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    return read >= 2 && header[0] == 0x4D && header[1] == 0x5A;

                if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                    return read >= 8 && header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

                if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                    return read >= 7 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 && header[4] == 0x1A && header[5] == 0x07;

                if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                    return read >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C;

                return read >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                       ((header[2] == 0x03 && header[3] == 0x04) ||
                        (header[2] == 0x05 && header[3] == 0x06) ||
                        (header[2] == 0x07 && header[3] == 0x08));
            }
            catch
            {
                return false;
            }
        }

        private string GetProductFilePhysicalPath(string filePath)
        {
            var relativePath = filePath
                .TrimStart('/', '\\')
                .Replace("/", Path.DirectorySeparatorChar.ToString())
                .Replace("\\", Path.DirectorySeparatorChar.ToString());

            return Path.Combine(_environment.WebRootPath, relativePath);
        }

        private async Task<License> EnsureFreeLicenseAsync(int userId, int productId)
        {
            var existingLicense = await _context.Licenses
                .Where(x =>
                    x.UserId == userId &&
                    x.ProductId == productId &&
                    x.IsActive &&
                    (x.EndDate == null || x.EndDate >= DateTime.Now))
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            if (existingLicense != null)
                return existingLicense;

            var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted);
            var license = new License
            {
                UserId = userId,
                ProductId = productId,
                LicenseKey = await GenerateUniqueLicenseKeyAsync(),
                LicenseType = "Lifetime",
                ProductCode = GetProductCodeForLicense(product),
                LicenseStatus = "Active",
                MaxDeviceCount = 1,
                OfflineAllowed = true,
                StartDate = DateTime.Now,
                EndDate = null,
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _context.Licenses.Add(license);
            await _context.SaveChangesAsync();
            return license;
        }

        private void QueueFreeLicenseEmail(
            string? customerEmail,
            string? customerName,
            Product product,
            License license)
        {
            var email = (customerEmail ?? string.Empty).Trim();
            var fullName = string.IsNullOrWhiteSpace(customerName)
                ? "Değerli müşterimiz"
                : customerName.Trim();
            var languageCode = Request.Cookies[SiteLocalizationService.LanguageCookieName] ?? "tr-TR";
            var nonTurkish = !languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
            var productName = string.IsNullOrWhiteSpace(product.Name)
                ? (nonTurkish ? "NSX Software Product" : "NSX Yazılım Ürünü")
                : product.Name.Trim();
            var subject = nonTurkish
                ? $"NSX Software | Your free license code - {productName}"
                : $"NSX Yazılım | Ücretsiz lisans kodunuz - {productName}";
            var html = EmailTemplates.FreeLicenseDelivery(
                fullName,
                productName,
                license.LicenseKey,
                GetPublicSiteUrl(),
                languageCode);

            _backgroundMailQueue.TryQueue(new BackgroundMailMessage(
                email,
                fullName,
                subject,
                html,
                "FreeLicense",
                $"Ürün: {productName} | Lisans: {license.LicenseKey}"));
        }

        private string GetPublicSiteUrl()
        {
            var configuredSiteUrl = _configuration["Site:Url"];
            if (!string.IsNullOrWhiteSpace(configuredSiteUrl))
                return configuredSiteUrl.TrimEnd('/');

            var host = Request.Host.Value ?? string.Empty;
            if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return "https://www.nsxyazilim.com";
            }

            return $"{Request.Scheme}://{Request.Host}".TrimEnd('/');
        }

        private async Task<string> GenerateUniqueLicenseKeyAsync()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var key = GenerateLicenseKey();
                var exists = await _context.Licenses.AnyAsync(x => x.LicenseKey == key);
                if (!exists)
                    return key;
            }

            throw new InvalidOperationException("Benzersiz lisans anahtarı üretilemedi. Lütfen tekrar deneyin.");
        }

        private static string GenerateLicenseKey()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = RandomNumberGenerator.GetBytes(20);
            var parts = new List<string>();

            for (var block = 0; block < 5; block++)
            {
                var segment = new char[4];
                for (var i = 0; i < 4; i++)
                    segment[i] = chars[bytes[(block * 4) + i] % chars.Length];
                parts.Add(new string(segment));
            }

            return $"NSX-{string.Join("-", parts)}";
        }

        private static bool IsFreeProduct(Product product)
        {
            return product.YearlyPrice <= 0 && product.LifetimePrice <= 0;
        }
        private static string NormalizeProductCode(string? value)
        {
            return new string((value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private static string GetProductCodeForLicense(Product? product)
        {
            if (product == null)
                return "NSX";

            var code = NormalizeProductCode(product.ProductCode);
            if (!string.IsNullOrWhiteSpace(code))
                return code;

            code = NormalizeProductCode(product.Slug);
            if (!string.IsNullOrWhiteSpace(code))
                return code;

            code = NormalizeProductCode(product.Name);
            return string.IsNullOrWhiteSpace(code) ? $"NSXPRODUCT{product.Id}" : code;
        }

    }
}
