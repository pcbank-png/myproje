using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed class ShopierOptions
    {
        public string ApiBaseUrl { get; set; } = "https://api.shopier.com/v1/";
        public string PersonalAccessToken { get; set; } = string.Empty;
        public string WebhookToken { get; set; } = string.Empty;
        public string WebhookUrl { get; set; } = "https://www.nsxyazilim.com/api/shopier/webhook";
        public int SyncLimit { get; set; } = 50;
        public string ShopSlug { get; set; } = "nsxyazilim";
        public int ProductSyncMinutes { get; set; } = 10;
        public int ProductSyncLimit { get; set; } = 50;
        public Dictionary<string, string> ProductUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ShopierSyncResult
    {
        public bool Success { get; init; }
        public int Received { get; init; }
        public int Saved { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class ShopierWebhookRegistrationResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class ShopierProductSyncResult
    {
        public bool Success { get; init; }
        public int ShopierProductCount { get; init; }
        public int LocalProductCount { get; init; }
        public int MatchedCount { get; init; }
        public int NewMatchCount { get; init; }
        public int UnmatchedCount { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class ShopierService
    {
        private const string WebhookEventName = "order.created";
        private static readonly SemaphoreSlim ProductSyncGate = new(1, 1);
        private static DateTime _lastProductSyncAttemptUtc = DateTime.MinValue;
        private static DateTime _lastProductSyncSuccessUtc = DateTime.MinValue;
        private readonly HttpClient _httpClient;
        private readonly ApplicationDbContext _db;
        private readonly ShopierOptions _options;
        private readonly IDataProtector _protector;
        private readonly IWebHostEnvironment _environment;
        private readonly ShopierOrderFulfillmentService _fulfillmentService;
        private readonly ILogger<ShopierService> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public ShopierService(
            HttpClient httpClient,
            ApplicationDbContext db,
            IOptions<ShopierOptions> options,
            IDataProtectionProvider dataProtectionProvider,
            IWebHostEnvironment environment,
            ShopierOrderFulfillmentService fulfillmentService,
            ILogger<ShopierService> logger)
        {
            _httpClient = httpClient;
            _db = db;
            _options = options.Value;
            _protector = dataProtectionProvider.CreateProtector("NSYazilim.Shopier.WebhookToken.v1");
            _environment = environment;
            _fulfillmentService = fulfillmentService;
            _logger = logger;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(CleanSecret(_options.PersonalAccessToken));

        public bool IsWebhookConfigured => !string.IsNullOrWhiteSpace(GetWebhookToken());

        public string? GetProductCheckoutUrl(Product product)
        {
            if (product == null)
                return null;

            return GetConfiguredProductUrl(product) ?? GetLegacyProductCheckoutUrl(product);
        }

        public async Task<string?> GetProductCheckoutUrlAsync(Product product, CancellationToken cancellationToken = default)
        {
            if (product == null)
                return null;

            var configured = GetConfiguredProductUrl(product);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            // 1) Kalıcı eşleşme varsa en hızlı yol budur. Mapping tablosundaki geçici bir
            // Plesk/MySQL sorunu checkout'u tamamen kapatmamalı; hata yalnızca loglanır.
            try
            {
                var mapped = await _db.ShopierProductMappings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProductId == product.Id && x.IsActive, cancellationToken);

                if (mapped != null && IsSafeShopierProductUrl(mapped.ShopierUrl))
                    return mapped.ShopierUrl.Trim();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shopier ürün eşleştirme tablosu checkout sırasında okunamadı. ProductId={ProductId}", product.Id);
            }

            if (IsConfigured)
            {
                // 2) Yeni ürün eklenmiş olabilir: normal kalıcı senkronu zorla.
                try
                {
                    var sync = await SyncProductMappingsAsync(force: true, cancellationToken);
                    if (sync.Success)
                    {
                        try
                        {
                            var mapped = await _db.ShopierProductMappings
                                .AsNoTracking()
                                .FirstOrDefaultAsync(x => x.ProductId == product.Id && x.IsActive, cancellationToken);

                            if (mapped != null && IsSafeShopierProductUrl(mapped.ShopierUrl))
                                return mapped.ShopierUrl.Trim();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Shopier senkronundan sonra mapping okunamadı. ProductId={ProductId}", product.Id);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Shopier ürün senkronu checkout sırasında tamamlanamadı. ProductId={ProductId}", product.Id);
                }

                // 3) Pro güvenlik ağı: DB mapping tablosu henüz oluşmamış olsa bile Shopier'deki
                // yüksek güvenli birebir ürün eşleşmesini aynı checkout isteğinde doğrudan kullan.
                // Böylece yeni Shopier ürünü için publish/link yapıştırma gerekmiyor.
                try
                {
                    var direct = await TryResolveProductCheckoutUrlDirectAsync(product, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(direct))
                        return direct;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Shopier doğrudan ürün çözümleme başarısız. ProductId={ProductId}", product.Id);
                }
            }

            // Eski iki canlı ürün için son acil durum geri dönüşü. Yeni ürünler otomatik çözümlemeden gelir.
            return GetLegacyProductCheckoutUrl(product);
        }

        public async Task<ShopierProductSyncResult> SyncProductMappingsAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                return new ShopierProductSyncResult
                {
                    Success = false,
                    Message = "Shopier ürün eşleştirmesi için kişisel erişim anahtarı tanımlı değil."
                };
            }

            var nowUtc = DateTime.UtcNow;
            var syncMinutes = Math.Clamp(_options.ProductSyncMinutes <= 0 ? 10 : _options.ProductSyncMinutes, 2, 120);
            if (!force && _lastProductSyncSuccessUtc != DateTime.MinValue && nowUtc - _lastProductSyncSuccessUtc < TimeSpan.FromMinutes(syncMinutes))
                return await BuildCachedProductSyncResultAsync("Shopier ürün eşleştirmesi güncel.", cancellationToken);

            await ProductSyncGate.WaitAsync(cancellationToken);
            try
            {
                nowUtc = DateTime.UtcNow;
                if (!force && _lastProductSyncSuccessUtc != DateTime.MinValue && nowUtc - _lastProductSyncSuccessUtc < TimeSpan.FromMinutes(syncMinutes))
                    return await BuildCachedProductSyncResultAsync("Shopier ürün eşleştirmesi güncel.", cancellationToken);

                // force=true yalnızca eşleşmesi olmayan checkout/admin senkronunda kullanılır.
                // Yeni Shopier ürünü eklendikten hemen sonra eski 20 sn önbelleğe takılmaması için
                // zorunlu senkronu geciktirmiyoruz. Semaphore aynı anda yinelenen çağrıları zaten seri hale getirir.
                _lastProductSyncAttemptUtc = nowUtc;

                var fetch = await FetchShopierProductsAsync(cancellationToken, includeStorefrontFallback: force);
                if (!fetch.Success)
                {
                    return new ShopierProductSyncResult
                    {
                        Success = false,
                        Message = fetch.Message
                    };
                }

                var shopierProducts = fetch.Products;
                var localProducts = await _db.Products
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .OrderBy(x => x.Id)
                    .ToListAsync(cancellationToken);

                // API başarılı görünse bile boş ürün listesi dönerse mevcut canlı eşleşmeleri silme/pasifleştirme.
                // Bu koruma, geçici Shopier listeleme anomalilerinde kartlı ödeme bağlantılarının düşmesini engeller.
                if (shopierProducts.Count == 0)
                {
                    var currentMatches = await _db.ShopierProductMappings
                        .AsNoTracking()
                        .CountAsync(x => x.IsActive, cancellationToken);

                    return new ShopierProductSyncResult
                    {
                        Success = true,
                        ShopierProductCount = 0,
                        LocalProductCount = localProducts.Count,
                        MatchedCount = currentMatches,
                        UnmatchedCount = Math.Max(0, localProducts.Count - currentMatches),
                        Message = currentMatches > 0
                            ? "Shopier ürün listesi bu turda boş döndü; mevcut kartlı ödeme eşleşmeleri güvenlik için korundu."
                            : "Shopier bağlantısı başarılı ancak eşleştirilecek Shopier ürünü bulunamadı."
                    };
                }

                var existingMappings = await _db.ShopierProductMappings
                    .ToListAsync(cancellationToken);

                var localIds = localProducts.Select(x => x.Id).ToHashSet();
                var orphanMappings = existingMappings.Where(x => !localIds.Contains(x.ProductId)).ToList();
                if (orphanMappings.Count > 0)
                {
                    _db.ShopierProductMappings.RemoveRange(orphanMappings);
                    existingMappings = existingMappings.Except(orphanMappings).ToList();
                }

                var remoteById = shopierProducts
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

                var existingByProductId = existingMappings.ToDictionary(x => x.ProductId);
                var usedRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var assignedLocalIds = new HashSet<int>();
                var assignments = new List<ShopierProductAssignment>();

                // Önce halen Shopier'de bulunan mevcut eşleşmeleri koru. Böylece isim değişikliği yanlış ürüne kaydırmaz.
                foreach (var local in localProducts)
                {
                    if (!existingByProductId.TryGetValue(local.Id, out var existing))
                        continue;

                    if (!remoteById.TryGetValue(existing.ShopierProductId, out var remote))
                    {
                        existing.IsActive = false;
                        existing.LastSyncedAt = DateTime.Now;
                        continue;
                    }

                    assignments.Add(new ShopierProductAssignment(local, remote, 100, "kayitli"));
                    usedRemoteIds.Add(remote.Id);
                    assignedLocalIds.Add(local.Id);
                }

                // Sonra yeni ürünleri otomatik olarak isim / ürün kodu benzerliği ile eşleştir.
                var candidates = new List<ShopierProductAssignment>();
                foreach (var local in localProducts.Where(x => !assignedLocalIds.Contains(x.Id)))
                {
                    foreach (var remote in shopierProducts.Where(x => !usedRemoteIds.Contains(x.Id)))
                    {
                        var score = CalculateProductMatchScore(local, remote, out var method);
                        if (score >= 94)
                            candidates.Add(new ShopierProductAssignment(local, remote, score, method));
                    }
                }

                foreach (var candidate in candidates
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.LocalProduct.Id)
                    .ThenBy(x => x.RemoteProduct.Id, StringComparer.OrdinalIgnoreCase))
                {
                    if (assignedLocalIds.Contains(candidate.LocalProduct.Id) || usedRemoteIds.Contains(candidate.RemoteProduct.Id))
                        continue;

                    assignments.Add(candidate);
                    assignedLocalIds.Add(candidate.LocalProduct.Id);
                    usedRemoteIds.Add(candidate.RemoteProduct.Id);
                }

                var newMatchCount = 0;
                var syncNow = DateTime.Now;
                foreach (var assignment in assignments)
                {
                    var url = BuildShopierProductUrl(assignment.RemoteProduct);
                    if (!IsSafeShopierProductUrl(url))
                        continue;

                    if (!existingByProductId.TryGetValue(assignment.LocalProduct.Id, out var mapping))
                    {
                        mapping = new ShopierProductMapping
                        {
                            ProductId = assignment.LocalProduct.Id,
                            CreatedAt = syncNow
                        };
                        _db.ShopierProductMappings.Add(mapping);
                        existingByProductId[assignment.LocalProduct.Id] = mapping;
                        newMatchCount++;
                    }

                    mapping.ShopierProductId = assignment.RemoteProduct.Id;
                    mapping.ShopierTitle = TrimTo(assignment.RemoteProduct.Title, 300) ?? assignment.LocalProduct.Name;
                    mapping.ShopierUrl = url!;
                    mapping.MatchMethod = TrimTo(assignment.Method, 40) ?? "auto";
                    mapping.MatchScore = assignment.Score;
                    mapping.IsActive = true;
                    mapping.LastSyncedAt = syncNow;
                }

                // Bu turda Shopier'de bulunamayan eşleşmeler pasif kalır; checkout bunları kullanmaz.
                foreach (var mapping in existingMappings)
                {
                    if (!assignments.Any(x => x.LocalProduct.Id == mapping.ProductId))
                    {
                        mapping.IsActive = false;
                        mapping.LastSyncedAt = syncNow;
                    }
                }

                await _db.SaveChangesAsync(cancellationToken);
                _lastProductSyncSuccessUtc = DateTime.UtcNow;

                var activeMatches = await _db.ShopierProductMappings
                    .AsNoTracking()
                    .CountAsync(x => x.IsActive, cancellationToken);
                var unmatched = Math.Max(0, localProducts.Count - activeMatches);

                return new ShopierProductSyncResult
                {
                    Success = true,
                    ShopierProductCount = shopierProducts.Count,
                    LocalProductCount = localProducts.Count,
                    MatchedCount = activeMatches,
                    NewMatchCount = newMatchCount,
                    UnmatchedCount = unmatched,
                    Message = newMatchCount > 0
                        ? $"Shopier otomatik eşleştirme tamamlandı. {newMatchCount} yeni ürün eşleşti; toplam {activeMatches} kartlı ödeme bağlantısı aktif."
                        : $"Shopier otomatik eşleştirme tamamlandı. {activeMatches} ürün eşleşmiş durumda."
                };
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ShopierProductSyncResult
                {
                    Success = false,
                    Message = "Shopier ürün eşleştirmesi zaman aşımına uğradı."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shopier ürünleri otomatik eşleştirilirken hata oluştu.");
                return new ShopierProductSyncResult
                {
                    Success = false,
                    Message = "Shopier ürünleri otomatik eşleştirilirken beklenmeyen bir hata oluştu."
                };
            }
            finally
            {
                ProductSyncGate.Release();
            }
        }

        private async Task<ShopierProductSyncResult> BuildCachedProductSyncResultAsync(string message, CancellationToken cancellationToken)
        {
            var localCount = await _db.Products.AsNoTracking().CountAsync(x => !x.IsDeleted, cancellationToken);
            var matchedCount = await _db.ShopierProductMappings.AsNoTracking().CountAsync(x => x.IsActive, cancellationToken);
            return new ShopierProductSyncResult
            {
                Success = true,
                LocalProductCount = localCount,
                MatchedCount = matchedCount,
                UnmatchedCount = Math.Max(0, localCount - matchedCount),
                Message = message
            };
        }

        private async Task<ShopierProductFetchResult> FetchShopierProductsAsync(CancellationToken cancellationToken, bool includeStorefrontFallback = false)
        {
            var products = new List<ShopierProductDto>();
            var limit = Math.Clamp(_options.ProductSyncLimit <= 0 ? 50 : _options.ProductSyncLimit, 1, 50);
            const int maxPages = 20;
            string? apiError = null;

            for (var page = 1; page <= maxPages; page++)
            {
                using var request = CreateRequest(HttpMethod.Get, $"products?page={page}&limit={limit}");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Shopier ürün listesi alınamadı. HTTP {StatusCode}. Body={Body}",
                        (int)response.StatusCode, TrimTo(json, 500));
                    var detail = (int)response.StatusCode is 401 or 403
                        ? " Shopier PAT hesabınızda ürün okuma yetkisini kontrol edin."
                        : string.Empty;
                    apiError = $"Shopier ürün listesi alınamadı: HTTP {(int)response.StatusCode}.{detail}";
                    break;
                }

                var parsed = ParseProductPage(json);
                products.AddRange(parsed.Products);

                // Shopier sayfalama bilgisini response header'larında döndürüyor.
                var currentPageHeader = ReadHeaderInt(response, "shopier-pagination-page");
                var totalPagesHeader = ReadHeaderInt(response, "shopier-pagination-total-pages");
                if (totalPagesHeader > 0)
                {
                    var currentPage = currentPageHeader > 0 ? currentPageHeader : page;
                    if (currentPage >= totalPagesHeader)
                        break;
                    continue;
                }

                if (parsed.TotalPages > 0)
                {
                    if (page >= parsed.TotalPages)
                        break;
                }
                else if (parsed.Products.Count < limit)
                {
                    break;
                }
            }

            // Shopier API bazı hesaplarda yeni listelenen ürünü birkaç dakika gecikmeli döndürebiliyor.
            // Bu durumda public mağaza sayfasındaki ürün linklerini de okuyup eksik ID/adları tamamla.
            // Burada ödeme/tutar bilgisi alınmaz; yalnızca güvenli ürün eşleştirme için public metadata kullanılır.
            if (includeStorefrontFallback)
            {
                try
                {
                    var storefront = await FetchPublicStoreProductsAsync(products, cancellationToken);
                    products.AddRange(storefront);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Shopier public mağaza ürün fallback'i tamamlanamadı.");
                }
            }

            products = products
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Title))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(p => !string.IsNullOrWhiteSpace(p.Description)).First())
                .ToList();

            if (products.Count == 0 && !string.IsNullOrWhiteSpace(apiError))
                return new ShopierProductFetchResult(false, products, apiError);

            return new ShopierProductFetchResult(true, products, string.Empty);
        }

        private async Task<List<ShopierProductDto>> FetchPublicStoreProductsAsync(
            IReadOnlyCollection<ShopierProductDto> apiProducts,
            CancellationToken cancellationToken)
        {
            var result = new List<ShopierProductDto>();
            var slug = NormalizeShopSlug(_options.ShopSlug);
            if (string.IsNullOrWhiteSpace(slug))
                return result;

            var knownIds = apiProducts
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .Select(x => x.Id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var storeUrl = $"https://www.shopier.com/{slug}";
            using var storeRequest = new HttpRequestMessage(HttpMethod.Get, storeUrl);
            storeRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            storeRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; NSXYazilim-ShopierSync/1.0)");
            using var storeResponse = await _httpClient.SendAsync(storeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!storeResponse.IsSuccessStatusCode)
                return result;

            var html = await storeResponse.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
                return result;

            var normalizedHtml = WebUtility.HtmlDecode(html)
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase);

            var escapedSlug = Regex.Escape(slug);
            var linkRegex = new Regex(
                $"(?:https?:)?//(?:www\\.)?shopier\\.com/{escapedSlug}/(?<id>\\d{{4,}})|/{escapedSlug}/(?<id>\\d{{4,}})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var ids = linkRegex.Matches(normalizedHtml)
                .Select(m => m.Groups["id"].Value)
                .Where(id => !string.IsNullOrWhiteSpace(id) && !knownIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList();

            foreach (var id in ids)
            {
                var url = $"https://www.shopier.com/{slug}/{Uri.EscapeDataString(id)}";
                try
                {
                    using var detailRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    detailRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
                    detailRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; NSXYazilim-ShopierSync/1.0)");
                    using var detailResponse = await _httpClient.SendAsync(detailRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!detailResponse.IsSuccessStatusCode)
                        continue;

                    var detailHtml = await detailResponse.Content.ReadAsStringAsync(cancellationToken);
                    var title = ExtractHtmlTitle(detailHtml);
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    result.Add(new ShopierProductDto
                    {
                        Id = id,
                        Title = title,
                        Url = url
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Shopier public ürün detayı okunamadı. ProductId={ProductId}", id);
                }
            }

            return result;
        }

        private static string? ExtractHtmlTitle(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            foreach (var pattern in new[]
            {
                @"<meta[^>]+(?:property|name)=[""'](?:og:title|twitter:title)[""'][^>]+content=[""'](?<value>[^""']+)[""']",
                @"<meta[^>]+content=[""'](?<value>[^""']+)[""'][^>]+(?:property|name)=[""'](?:og:title|twitter:title)[""']",
                @"<title[^>]*>(?<value>.*?)</title>"
            })
            {
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
                if (!match.Success)
                    continue;

                var title = WebUtility.HtmlDecode(match.Groups["value"].Value);
                title = Regex.Replace(title, @"\s+", " ").Trim();
                title = Regex.Replace(title, @"\s*(?:[|\-–—]\s*)?Shopier\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }

            return null;
        }

        private async Task<string?> TryResolveProductCheckoutUrlDirectAsync(Product product, CancellationToken cancellationToken)
        {
            var fetch = await FetchShopierProductsAsync(cancellationToken, includeStorefrontFallback: true);
            if (!fetch.Success || fetch.Products.Count == 0)
                return null;

            var candidates = fetch.Products
                .Select(remote =>
                {
                    var score = CalculateProductMatchScore(product, remote, out var method);
                    return new { Remote = remote, Score = score, Method = method };
                })
                .Where(x => x.Score >= 94)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Remote.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
                return null;

            var best = candidates[0];
            if (candidates.Count > 1 && candidates[1].Score == best.Score)
            {
                _logger.LogWarning(
                    "Shopier doğrudan eşleştirme belirsiz bırakıldı. ProductId={ProductId} Name={ProductName} Score={Score}",
                    product.Id, product.Name, best.Score);
                return null;
            }

            var url = BuildShopierProductUrl(best.Remote);
            if (!IsSafeShopierProductUrl(url))
                return null;

            _logger.LogInformation(
                "Shopier checkout doğrudan eşleşti. ProductId={ProductId} ShopierProductId={ShopierProductId} Method={Method} Score={Score}",
                product.Id, best.Remote.Id, best.Method, best.Score);
            return url;
        }

        private ShopierProductPage ParseProductPage(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ShopierProductPage(new List<ShopierProductDto>(), 0);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var productElements = new List<JsonElement>();

            CollectProductElements(root, productElements);

            var result = new List<ShopierProductDto>();
            foreach (var element in productElements)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ReadFlexibleString(element, "id")
                    ?? ReadFlexibleString(element, "productId");
                var title = ReadFlexibleString(element, "title")
                    ?? ReadFlexibleString(element, "name")
                    ?? ReadFlexibleString(element, "productTitle");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                    continue;

                result.Add(new ShopierProductDto
                {
                    Id = id.Trim(),
                    Title = title.Trim(),
                    Url = ReadFlexibleString(element, "url")
                        ?? ReadFlexibleString(element, "productUrl")
                        ?? ReadFlexibleString(element, "link"),
                    Description = ReadFlexibleString(element, "description")
                });
            }

            var totalPages = ReadJsonInt(root, "totalPages");
            if (totalPages <= 0
                && root.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(root, "pagination", out var pagination)
                && pagination.ValueKind == JsonValueKind.Object)
            {
                totalPages = ReadJsonInt(pagination, "totalPages");
                if (totalPages <= 0)
                    totalPages = ReadJsonInt(pagination, "last_page");
            }

            // Shopier v1 ürün cevabında pagination sıklıkla meta.last_page altında gelir.
            if (totalPages <= 0
                && root.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(root, "meta", out var meta)
                && meta.ValueKind == JsonValueKind.Object)
            {
                totalPages = ReadJsonInt(meta, "last_page");
                if (totalPages <= 0)
                    totalPages = ReadJsonInt(meta, "totalPages");
            }

            return new ShopierProductPage(result, totalPages);
        }

        private static void CollectProductElements(JsonElement element, List<JsonElement> destination)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                destination.AddRange(element.EnumerateArray());
                return;
            }

            if (element.ValueKind != JsonValueKind.Object)
                return;

            // Tek ürün cevabı da kabul edilsin.
            if ((TryGetPropertyIgnoreCase(element, "id", out _) || TryGetPropertyIgnoreCase(element, "productId", out _))
                && (TryGetPropertyIgnoreCase(element, "title", out _)
                    || TryGetPropertyIgnoreCase(element, "name", out _)
                    || TryGetPropertyIgnoreCase(element, "productTitle", out _)))
            {
                destination.Add(element);
                return;
            }

            // Shopier'in mevcut { data:[...] } cevabı yanında farklı envelope biçimlerine karşı toleranslı ol.
            foreach (var key in new[] { "data", "products", "items", "results", "result" })
            {
                if (!TryGetPropertyIgnoreCase(element, key, out var nested))
                    continue;

                if (nested.ValueKind == JsonValueKind.Array)
                {
                    destination.AddRange(nested.EnumerateArray());
                    return;
                }

                if (nested.ValueKind == JsonValueKind.Object)
                {
                    foreach (var innerKey in new[] { "data", "products", "items", "results" })
                    {
                        if (TryGetPropertyIgnoreCase(nested, innerKey, out var inner)
                            && inner.ValueKind == JsonValueKind.Array)
                        {
                            destination.AddRange(inner.EnumerateArray());
                            return;
                        }
                    }
                }
            }
        }

        private static int ReadHeaderInt(HttpResponseMessage response, string headerName)
        {
            IEnumerable<string>? values = null;
            if (!response.Headers.TryGetValues(headerName, out values)
                && !response.Content.Headers.TryGetValues(headerName, out values))
                return 0;

            var raw = values?.FirstOrDefault();
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        private static int ReadJsonInt(JsonElement element, string propertyName)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                return 0;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            return value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                    ? number
                    : 0;
        }

        private static int CalculateProductMatchScore(Product local, ShopierProductDto remote, out string method)
        {
            method = "isim";
            var remoteTitle = remote.Title ?? string.Empty;
            var localName = local.Name ?? string.Empty;

            var localExact = NormalizeProductMatchText(localName, compact: true);
            var remoteExact = NormalizeProductMatchText(remoteTitle, compact: true);
            if (!string.IsNullOrWhiteSpace(localExact) && string.Equals(localExact, remoteExact, StringComparison.Ordinal))
            {
                method = "ad-birebir";
                return 100;
            }

            // Shopier başlığına açıklayıcı bir alt başlık eklense bile (örn.
            // "NSX Veri Kurtarma Pro - Profesyonel Veri Kurtarma Yazılımı")
            // yerel ürün adı başta birebir korunuyorsa yüksek güvenli eşleşme say.
            // Bu kural "Cari Takip Pro" ile "Komisyonlu Cari Takip Pro" gibi
            // isimleri karıştırmaz; ek kelime ürün adının önüne girerse prefix oluşmaz.
            if (localExact.Length >= 8
                && remoteExact.Length > localExact.Length
                && remoteExact.StartsWith(localExact, StringComparison.Ordinal))
            {
                method = "ad-baslangic-birebir";
                return 97;
            }

            var localCore = NormalizeProductCore(localName);
            var remoteCore = NormalizeProductCore(remoteTitle);
            if (!string.IsNullOrWhiteSpace(localCore) && string.Equals(localCore, remoteCore, StringComparison.Ordinal))
            {
                method = "ad-sadelestirilmis";
                return 98;
            }

            if (localCore.Length >= 6
                && remoteCore.Length > localCore.Length
                && remoteCore.StartsWith(localCore, StringComparison.Ordinal))
            {
                method = "ad-sade-baslangic";
                return 96;
            }

            var productCode = NormalizeProductMatchText(local.ProductCode, compact: true);
            var remoteDescription = NormalizeProductMatchText(remote.Description, compact: true);
            if (!string.IsNullOrWhiteSpace(productCode)
                && productCode.Length >= 8
                && remoteDescription.Contains(productCode, StringComparison.Ordinal))
            {
                method = "urun-kodu";
                return 99;
            }

            var localTokens = GetProductMatchTokens(localName);
            var remoteTokens = GetProductMatchTokens(remoteTitle);
            if (localTokens.Count == 0 || remoteTokens.Count == 0)
                return 0;

            var common = localTokens.Intersect(remoteTokens, StringComparer.Ordinal).Count();
            if (common < 2)
                return 0;

            var coverage = common / (double)Math.Min(localTokens.Count, remoteTokens.Count);
            var balance = common / (double)Math.Max(localTokens.Count, remoteTokens.Count);
            var score = (int)Math.Round(((coverage * 0.55) + (balance * 0.45)) * 100d, MidpointRounding.AwayFromZero);

            method = "ad-benzerligi";
            return score;
        }

        private static string NormalizeProductCore(string? value)
        {
            var ignored = new HashSet<string>(StringComparer.Ordinal)
            {
                "nsx", "pro", "program", "programi", "yazilim", "yazilimi", "uygulama", "uygulamasi"
            };

            return string.Concat(GetProductMatchTokens(value).Where(x => !ignored.Contains(x)));
        }

        private static HashSet<string> GetProductMatchTokens(string? value)
        {
            var normalized = NormalizeProductMatchText(value, compact: false);
            return normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length >= 2)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string NormalizeProductMatchText(string? value, bool compact)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var source = value.Trim().ToLowerInvariant()
                .Replace('ı', 'i')
                .Replace('ğ', 'g')
                .Replace('ş', 's')
                .Replace('ç', 'c')
                .Replace('ö', 'o')
                .Replace('ü', 'u');

            var decomposed = source.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            var lastWasSpace = false;
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                    lastWasSpace = false;
                }
                else if (!compact && !lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }

            return compact ? builder.ToString() : builder.ToString().Trim();
        }

        private string? BuildShopierProductUrl(ShopierProductDto remote)
        {
            if (IsSafeShopierProductUrl(remote.Url))
                return remote.Url!.Trim();

            var slug = NormalizeShopSlug(_options.ShopSlug);
            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(remote.Id))
                return null;

            return $"https://www.shopier.com/{slug}/{Uri.EscapeDataString(remote.Id.Trim())}";
        }

        private static string NormalizeShopSlug(string? value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                raw = "nsxyazilim";

            var chars = raw.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray();
            return new string(chars);
        }

        private string? GetConfiguredProductUrl(Product product)
        {
            var productCode = (product.ProductCode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(productCode))
                return null;

            foreach (var pair in _options.ProductUrls ?? new Dictionary<string, string>())
            {
                if (string.Equals(pair.Key?.Trim(), productCode, StringComparison.OrdinalIgnoreCase)
                    && IsSafeShopierProductUrl(pair.Value))
                {
                    return pair.Value.Trim();
                }
            }

            return null;
        }

        private static string? GetLegacyProductCheckoutUrl(Product product)
        {
            var productCode = (product.ProductCode ?? string.Empty).Trim();

            if (string.Equals(productCode, "NSXCARITAKIPPRO", StringComparison.OrdinalIgnoreCase)
                || string.Equals(productCode, "NSXCARITAKIPPRO2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(product.Name?.Trim(), "NSX Cari Takip Pro", StringComparison.OrdinalIgnoreCase))
            {
                return "https://www.shopier.com/nsxyazilim/50570664";
            }

            if (string.Equals(productCode, "NSXTURBOBILGISAYARPERFORMANSPRO", StringComparison.OrdinalIgnoreCase)
                || string.Equals(product.Name?.Trim(), "NSX Turbo Performans Programı", StringComparison.OrdinalIgnoreCase)
                || string.Equals(product.Name?.Trim(), "NSX Turbo Performans Pro", StringComparison.OrdinalIgnoreCase))
            {
                return "https://www.shopier.com/nsxyazilim/50572058";
            }

            return null;
        }

        public async Task<ShopierSyncResult> SyncRecentOrdersAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                return new ShopierSyncResult
                {
                    Success = false,
                    Message = "Shopier kişisel erişim anahtarı sunucu ayarlarında tanımlı değil."
                };
            }

            try
            {
                var limit = Math.Clamp(_options.SyncLimit <= 0 ? 50 : _options.SyncLimit, 1, 50);
                var dateEnd = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                var dateStart = DateTime.UtcNow.AddDays(-45).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                using var request = CreateRequest(HttpMethod.Get,
                    $"orders?limit={limit}&sort=dateDesc&dateStart={Uri.EscapeDataString(dateStart)}&dateEnd={Uri.EscapeDataString(dateEnd)}");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Shopier sipariş senkronizasyonu başarısız. HTTP {StatusCode}", (int)response.StatusCode);
                    return new ShopierSyncResult
                    {
                        Success = false,
                        Message = $"Shopier API yanıtı başarısız: HTTP {(int)response.StatusCode}."
                    };
                }

                var orders = ParseOrderCollection(json);
                var saved = 0;
                foreach (var order in orders)
                {
                    if (string.IsNullOrWhiteSpace(order.Id))
                        continue;

                    await UpsertOrderAsync(order, null, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);

                    // Webhook kaçırılmışsa manuel/periodik sipariş senkronu lisans teslimatını da tamamlayabilsin.
                    await _fulfillmentService.TryFulfillAsync(order, verifiedOrderCreatedEvent: false, cancellationToken: cancellationToken);
                    saved++;
                }

                return new ShopierSyncResult
                {
                    Success = true,
                    Received = orders.Count,
                    Saved = saved,
                    Message = orders.Count == 0
                        ? "Shopier bağlantısı başarılı. Son dönemde kartlı sipariş bulunamadı."
                        : $"Shopier bağlantısı başarılı. {saved} sipariş güncellendi."
                };
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ShopierSyncResult
                {
                    Success = false,
                    Message = "Shopier API zaman aşımına uğradı. Lütfen tekrar deneyin."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shopier siparişleri senkronize edilirken hata oluştu.");
                return new ShopierSyncResult
                {
                    Success = false,
                    Message = "Shopier siparişleri alınırken beklenmeyen bir hata oluştu."
                };
            }
        }

        public async Task<bool> ProcessOrderCreatedWebhookAsync(string rawBody, string? webhookId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rawBody))
                return false;

            var webhookOrder = ParseSingleOrder(rawBody);
            if (webhookOrder == null || string.IsNullOrWhiteSpace(webhookOrder.Id))
                return false;

            // İmzalı webhook güvenilir olsa da mümkünse siparişi Shopier API'den tekrar okuyup
            // ürün/tutar/müşteri bilgisini sunucu-sunucu doğrularız. API anlık cevap vermezse
            // imzası doğrulanmış order.created payload'ı ile devam edilir.
            var authoritativeOrder = await TryFetchOrderByIdAsync(webhookOrder.Id, cancellationToken) ?? webhookOrder;
            if (string.IsNullOrWhiteSpace(authoritativeOrder.PaymentStatus))
                authoritativeOrder.PaymentStatus = "paid";

            await UpsertOrderAsync(authoritativeOrder, webhookId, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            var fulfillment = await _fulfillmentService.TryFulfillAsync(
                authoritativeOrder,
                verifiedOrderCreatedEvent: true,
                cancellationToken: cancellationToken);

            // Shopier ürünü checkout öncesinde henüz eşleşmemişse webhook anında ürün listesini
            // bir kez zorla yenileyip lisans teslimatını tekrar dene. Yanlış ürüne lisans basmak yerine
            // yüksek güvenli otomatik eşleştirme kurallarına bağlı kalır.
            if (!fulfillment.Completed
                && string.Equals(fulfillment.Status, "WaitingProduct", StringComparison.OrdinalIgnoreCase)
                && IsConfigured)
            {
                var productSync = await SyncProductMappingsAsync(force: true, cancellationToken);
                if (productSync.Success)
                {
                    fulfillment = await _fulfillmentService.TryFulfillAsync(
                        authoritativeOrder,
                        verifiedOrderCreatedEvent: true,
                        cancellationToken: cancellationToken);
                }
            }

            if (!fulfillment.Completed)
            {
                _logger.LogWarning(
                    "Shopier siparişi alındı ancak otomatik lisans teslimatı tamamlanmadı. ShopierOrderId={ShopierOrderId} Status={Status} Message={Message}",
                    authoritativeOrder.Id, fulfillment.Status, fulfillment.Message);

                // Teknik hata ise Shopier'in webhook retry mekanizmasına 500 döndürmek için üst katmana taşı.
                // Üye/ürün/tutar eşleşmesi gibi işletimsel durumlar admin panelinde bekleyen olarak kalır.
                if (string.Equals(fulfillment.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(fulfillment.Message);
            }

            return true;
        }

        public bool VerifyWebhookSignature(string rawBody, string? signature)
        {
            var webhookToken = GetWebhookToken();
            if (string.IsNullOrWhiteSpace(webhookToken) || string.IsNullOrWhiteSpace(signature))
                return false;

            var provided = signature.Trim();
            if (provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
                provided = provided[7..].Trim();

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookToken));
            var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody ?? string.Empty));

            if (TryDecodeSignature(provided, out var providedBytes))
                return CryptographicOperations.FixedTimeEquals(computed, providedBytes);

            return false;
        }

        public async Task<ShopierWebhookRegistrationResult> RegisterOrderCreatedWebhookAsync(string notificationUrl, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                return new ShopierWebhookRegistrationResult
                {
                    Success = false,
                    Message = "Önce Shopier kişisel erişim anahtarını sunucu ayarlarına ekleyin."
                };
            }

            if (IsWebhookConfigured)
            {
                return new ShopierWebhookRegistrationResult
                {
                    Success = true,
                    Message = "Shopier order.created webhook doğrulama anahtarı zaten hazır."
                };
            }

            if (!Uri.TryCreate(notificationUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return new ShopierWebhookRegistrationResult
                {
                    Success = false,
                    Message = "Webhook adresi geçerli bir HTTPS adresi olmalıdır."
                };
            }

            try
            {
                using var request = CreateRequest(HttpMethod.Post, "webhooks");
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    @event = WebhookEventName,
                    url = notificationUrl
                }), Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Shopier webhook kaydı başarısız. HTTP {StatusCode}", (int)response.StatusCode);
                    return new ShopierWebhookRegistrationResult
                    {
                        Success = false,
                        Message = $"Shopier webhook kaydı başarısız: HTTP {(int)response.StatusCode}."
                    };
                }

                using var document = JsonDocument.Parse(json);
                var token = FindStringProperty(document.RootElement, "token");
                if (string.IsNullOrWhiteSpace(token))
                {
                    return new ShopierWebhookRegistrationResult
                    {
                        Success = false,
                        Message = "Webhook oluşturuldu ancak doğrulama anahtarı yanıtta bulunamadı. Shopier panelinden webhook durumunu kontrol edin."
                    };
                }

                SaveWebhookToken(token);
                return new ShopierWebhookRegistrationResult
                {
                    Success = true,
                    Message = "Shopier order.created webhook başarıyla etkinleştirildi."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shopier webhook kaydı sırasında hata oluştu.");
                return new ShopierWebhookRegistrationResult
                {
                    Success = false,
                    Message = "Shopier webhook kaydı sırasında beklenmeyen bir hata oluştu."
                };
            }
        }

        public string ResolveWebhookUrl(HttpRequest request)
        {
            var configured = (_options.WebhookUrl ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            return $"{request.Scheme}://{request.Host}{request.PathBase}/api/shopier/webhook";
        }

        private async Task<ShopierOrderDto?> TryFetchOrderByIdAsync(string orderId, CancellationToken cancellationToken)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(orderId))
                return null;

            try
            {
                using var request = CreateRequest(HttpMethod.Get, $"orders/{Uri.EscapeDataString(orderId.Trim())}");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Shopier sipariş doğrulama çağrısı başarısız. OrderId={OrderId} HTTP={StatusCode}", orderId, (int)response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseSingleOrder(json);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shopier siparişi API üzerinden tekrar doğrulanamadı. OrderId={OrderId}", orderId);
                return null;
            }
        }

        private ShopierOrderDto? ParseSingleOrder(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var document = JsonDocument.Parse(json);
            return ParseSingleOrderElement(document.RootElement);
        }

        private ShopierOrderDto? ParseSingleOrderElement(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(element, "id", out _))
                    return element.Deserialize<ShopierOrderDto>(_jsonOptions);

                foreach (var candidate in new[] { "data", "order", "result", "item" })
                {
                    if (TryGetPropertyIgnoreCase(element, candidate, out var nested))
                    {
                        var parsed = ParseSingleOrderElement(nested);
                        if (parsed != null)
                            return parsed;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var parsed = ParseSingleOrderElement(item);
                    if (parsed != null)
                        return parsed;
                }
            }

            return null;
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var baseUrl = string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
                ? "https://api.shopier.com/v1/"
                : _options.ApiBaseUrl.Trim();
            if (!baseUrl.EndsWith('/'))
                baseUrl += "/";

            var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CleanSecret(_options.PersonalAccessToken));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        private List<ShopierOrderDto> ParseOrderCollection(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<ShopierOrderDto>();

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return DeserializeArray(root);

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var candidate in new[] { "data", "orders", "items", "results" })
                {
                    if (root.TryGetProperty(candidate, out var value) && value.ValueKind == JsonValueKind.Array)
                        return DeserializeArray(value);
                }

                var discovered = FindOrderArray(root);
                if (discovered.HasValue)
                    return DeserializeArray(discovered.Value);
            }

            return new List<ShopierOrderDto>();
        }

        private static JsonElement? FindOrderArray(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                var first = element.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("id", out _)
                    && (first.TryGetProperty("paymentStatus", out _) || first.TryGetProperty("lineItems", out _)))
                {
                    return element;
                }

                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindOrderArray(item);
                    if (nested.HasValue)
                        return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindOrderArray(property.Value);
                    if (nested.HasValue)
                        return nested;
                }
            }

            return null;
        }

        private List<ShopierOrderDto> DeserializeArray(JsonElement array)
        {
            var result = new List<ShopierOrderDto>();
            foreach (var element in array.EnumerateArray())
            {
                var item = element.Deserialize<ShopierOrderDto>(_jsonOptions);
                if (item != null)
                    result.Add(item);
            }
            return result;
        }

        private async Task UpsertOrderAsync(ShopierOrderDto order, string? webhookId, CancellationToken cancellationToken)
        {
            var entity = await _db.ShopierOrders.FirstOrDefaultAsync(x => x.ShopierOrderId == order.Id, cancellationToken);
            var now = DateTime.Now;
            var firstLine = order.LineItems?.FirstOrDefault();
            var shipping = order.ShippingInfo;
            var billing = order.BillingInfo;
            var selection = firstLine?.Selection?.FirstOrDefault(x =>
                                string.Equals(x.VariationTitle, "Lisans Türü", StringComparison.OrdinalIgnoreCase))
                            ?? firstLine?.Selection?.FirstOrDefault();

            if (entity == null)
            {
                entity = new ShopierOrderRecord
                {
                    ShopierOrderId = order.Id,
                    FirstSeenAt = now
                };
                _db.ShopierOrders.Add(entity);
            }

            entity.PaymentStatus = TrimTo(order.PaymentStatus, 40) ?? "unknown";
            entity.FulfillmentStatus = TrimTo(order.Status, 40) ?? "unfulfilled";
            entity.PaymentMethod = TrimTo(order.PaymentMethod, 40) ?? string.Empty;
            entity.IsInstallments = order.Installments;
            entity.Currency = TrimTo(order.Currency, 8) ?? "TRY";
            entity.TotalAmount = ParseMoney(order.Totals?.Total);
            entity.CustomerFirstName = TrimTo(shipping?.FirstName ?? billing?.FirstName, 120);
            entity.CustomerLastName = TrimTo(shipping?.LastName ?? billing?.LastName, 120);
            entity.CustomerEmail = TrimTo(shipping?.Email ?? billing?.Email, 180)?.ToLowerInvariant();
            entity.CustomerPhone = TrimTo(shipping?.Phone ?? billing?.Phone, 60);
            entity.Note = TrimTo(order.Note, 1000);
            entity.ShopierProductId = TrimTo(firstLine?.ProductId, 80);
            entity.ProductTitle = BuildProductTitle(order.LineItems);
            entity.LicenseSelection = TrimTo(selection?.Title, 120);
            entity.Quantity = Math.Max(1, firstLine?.Quantity ?? 1);
            entity.ShopierCreatedAt = ParseShopierDate(order.DateCreated) ?? entity.ShopierCreatedAt;
            entity.LastSyncedAt = now;
            if (!string.IsNullOrWhiteSpace(webhookId))
                entity.LastWebhookId = TrimTo(webhookId, 80);
        }

        private string? GetWebhookToken()
        {
            var configured = CleanSecret(_options.WebhookToken);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            try
            {
                var path = GetProtectedWebhookTokenPath();
                if (!File.Exists(path))
                    return null;

                var protectedValue = File.ReadAllText(path, Encoding.UTF8).Trim();
                return string.IsNullOrWhiteSpace(protectedValue) ? null : _protector.Unprotect(protectedValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shopier webhook doğrulama anahtarı okunamadı.");
                return null;
            }
        }

        private void SaveWebhookToken(string token)
        {
            var path = GetProtectedWebhookTokenPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var protectedValue = _protector.Protect(token.Trim());
            File.WriteAllText(path, protectedValue, Encoding.UTF8);
        }

        private string GetProtectedWebhookTokenPath()
        {
            return Path.Combine(_environment.ContentRootPath, "App_Data", "Shopier", "order-created-webhook.token");
        }

        private static bool TryDecodeSignature(string signature, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            try
            {
                if (signature.Length == 64 && signature.All(Uri.IsHexDigit))
                {
                    bytes = Convert.FromHexString(signature);
                    return true;
                }

                var normalized = signature.Replace('-', '+').Replace('_', '/');
                var padding = normalized.Length % 4;
                if (padding == 2) normalized += "==";
                else if (padding == 3) normalized += "=";
                bytes = Convert.FromBase64String(normalized);
                return bytes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string? ReadFlexibleString(JsonElement element, string propertyName)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static string? FindStringProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }

                    var nested = FindStringProperty(property.Value, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindStringProperty(item, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }

            return null;
        }

        private static string BuildProductTitle(List<ShopierLineItemDto>? items)
        {
            if (items == null || items.Count == 0)
                return "Shopier ürünü";

            var titles = items
                .Select(x => (x.Title ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (titles.Count == 0)
                return "Shopier ürünü";
            if (titles.Count == 1)
                return TrimTo(titles[0], 300) ?? "Shopier ürünü";

            return TrimTo($"{titles[0]} +{titles.Count - 1} ürün", 300) ?? titles[0];
        }

        private static decimal ParseMoney(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0m;

            var raw = value.Trim();
            decimal amount;
            if (raw.Contains(',') && !raw.Contains('.'))
            {
                if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out amount))
                    return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
            }

            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
                return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out amount)
                ? decimal.Round(amount, 2, MidpointRounding.AwayFromZero)
                : 0m;
        }

        private static DateTime? ParseShopierDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto))
                return dto.LocalDateTime;

            return null;
        }

        private static string? TrimTo(string? value, int maxLength)
        {
            var cleaned = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;
            return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
        }

        private static bool IsSafeShopierProductUrl(string? value)
        {
            if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
                return false;

            return uri.Scheme == Uri.UriSchemeHttps
                && (string.Equals(uri.Host, "shopier.com", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Host, "www.shopier.com", StringComparison.OrdinalIgnoreCase));
        }

        private sealed record ShopierProductFetchResult(bool Success, List<ShopierProductDto> Products, string Message);

        private sealed record ShopierProductPage(List<ShopierProductDto> Products, int TotalPages);

        private sealed record ShopierProductAssignment(Product LocalProduct, ShopierProductDto RemoteProduct, int Score, string Method);

        private sealed class ShopierProductDto
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string? Url { get; set; }
            public string? Description { get; set; }
        }

        private static string CleanSecret(string? value)
        {
            var cleaned = (value ?? string.Empty).Trim();
            if (cleaned.Contains("BURAYA", StringComparison.OrdinalIgnoreCase)
                || cleaned.Contains("CHANGE-THIS", StringComparison.OrdinalIgnoreCase)
                || cleaned.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return cleaned;
        }
    }

    internal sealed class ShopierOrderDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("paymentStatus")]
        public string? PaymentStatus { get; set; }

        [JsonPropertyName("installments")]
        public bool Installments { get; set; }

        [JsonPropertyName("dateCreated")]
        public string? DateCreated { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("paymentMethod")]
        public string? PaymentMethod { get; set; }

        [JsonPropertyName("totals")]
        public ShopierTotalsDto? Totals { get; set; }

        [JsonPropertyName("shippingInfo")]
        public ShopierCustomerInfoDto? ShippingInfo { get; set; }

        [JsonPropertyName("billingInfo")]
        public ShopierCustomerInfoDto? BillingInfo { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }

        [JsonPropertyName("lineItems")]
        public List<ShopierLineItemDto>? LineItems { get; set; }
    }

    internal sealed class ShopierTotalsDto
    {
        [JsonPropertyName("total")]
        public string? Total { get; set; }
    }

    internal sealed class ShopierCustomerInfoDto
    {
        [JsonPropertyName("firstName")]
        public string? FirstName { get; set; }

        [JsonPropertyName("lastName")]
        public string? LastName { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("phone")]
        public string? Phone { get; set; }
    }

    internal sealed class ShopierLineItemDto
    {
        [JsonPropertyName("productId")]
        public string? ProductId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("price")]
        public string? Price { get; set; }

        [JsonPropertyName("total")]
        public string? Total { get; set; }

        [JsonPropertyName("selection")]
        public List<ShopierSelectionDto>? Selection { get; set; }
    }

    internal sealed class ShopierSelectionDto
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("variationTitle")]
        public string? VariationTitle { get; set; }
    }
}
