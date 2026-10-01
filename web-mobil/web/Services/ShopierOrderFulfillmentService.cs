using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed class ShopierFulfillmentResult
    {
        public bool Success { get; init; }
        public bool Completed { get; init; }
        public bool AlreadyCompleted { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public int? LocalOrderId { get; init; }
    }

    /// <summary>
    /// Shopier tarafında doğrulanmış kart siparişini NSX sipariş + lisans + e-posta teslimatına dönüştürür.
    /// Tüm kritik adımlar idempotenttir; aynı Shopier siparişi ikinci kez lisans üretemez.
    /// </summary>
    public sealed class ShopierOrderFulfillmentService
    {
        private const string CompletedStatus = "Completed";
        private const string WaitingUserStatus = "WaitingUser";
        private const string WaitingProductStatus = "WaitingProduct";
        private const string WaitingVariantStatus = "WaitingVariant";
        private const string PaymentPendingStatus = "PaymentPending";
        private const string AmountMismatchStatus = "AmountMismatch";
        private const string CurrencyMismatchStatus = "CurrencyMismatch";
        private const string StockProblemStatus = "StockProblem";
        private const string FailedStatus = "Failed";

        private readonly ApplicationDbContext _db;
        private readonly BackgroundMailQueue _mailQueue;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ShopierOrderFulfillmentService> _logger;

        public ShopierOrderFulfillmentService(
            ApplicationDbContext db,
            BackgroundMailQueue mailQueue,
            IConfiguration configuration,
            ILogger<ShopierOrderFulfillmentService> logger)
        {
            _db = db;
            _mailQueue = mailQueue;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Eski Shopier entegrasyon sürümlerinde lisans teslim edilmiş fakat Orders/OrderItems
        /// kaydı oluşturulmamış işlemleri güvenli şekilde sipariş geçmişine bağlar.
        /// Bu yöntem ASLA yeni lisans üretmez; yalnızca mevcut, siparişe bağlı olmayan lisansı kullanır.
        /// </summary>
        public async Task<int> RepairCustomerOrderHistoryAsync(
            int userId,
            string? userEmail,
            CancellationToken cancellationToken = default)
        {
            if (userId <= 0)
                return 0;

            try
            {
                var user = await _db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.Id == userId &&
                        !x.IsDeleted &&
                        x.IsActive &&
                        x.Role == "User",
                        cancellationToken);

                if (user == null)
                    return 0;

                var email = NormalizeEmail(userEmail);
                if (string.IsNullOrWhiteSpace(email))
                    email = NormalizeEmail(user.Email);

                if (string.IsNullOrWhiteSpace(email))
                    return 0;

                // LocalOrderId dolu olsa bile ilgili Orders satiri daha once admin tarafinda silinmis
                // olabilir. Bu nedenle yalniz NULL kayitlari degil, kullanicinin tamamlanmis Shopier
                // kayitlarini kontrol ediyoruz. Gecerli bag varsa dokunmuyor, yetim bag varsa onariyoruz.
                var records = await _db.ShopierOrders
                    .Where(x =>
                        x.CustomerEmail != null &&
                        x.CustomerEmail.ToLower() == email &&
                        (x.LocalFulfillmentStatus.ToLower() == "completed" || x.FulfilledAt.HasValue))
                    .OrderBy(x => x.ShopierCreatedAt)
                    .Take(50)
                    .ToListAsync(cancellationToken);

                var repairedCount = 0;
                foreach (var record in records)
                {
                    if (record.LocalOrderId.HasValue)
                    {
                        var linkedOrder = await _db.Orders
                            .AsNoTracking()
                            .Where(x => x.Id == record.LocalOrderId.Value)
                            .Select(x => new { x.Id, x.UserId })
                            .FirstOrDefaultAsync(cancellationToken);

                        if (linkedOrder != null)
                        {
                            // Bag gercekten varsa zaten siparis gecmisi saglamdir. Baska kullaniciya
                            // ait bir Order ise otomatik yeniden baglama yapmayip kaydi oldugu gibi birak.
                            if (linkedOrder.UserId != user.Id)
                            {
                                _logger.LogWarning(
                                    "Shopier siparisi farkli bir yerel kullanici siparisine bagli. ShopierOrderId={ShopierOrderId} LocalOrderId={LocalOrderId} ExpectedUserId={ExpectedUserId} ActualUserId={ActualUserId}",
                                    record.ShopierOrderId, linkedOrder.Id, user.Id, linkedOrder.UserId);
                            }

                            continue;
                        }

                        // Yetim LocalOrderId: Order silinmis ama Shopier referansi kalmis. Lisansi
                        // silmeden yalniz kopuk referansi temizleyip asagidaki guvenli onarima gec.
                        _logger.LogInformation(
                            "Shopier kaydinda yetim LocalOrderId bulundu ve onarima alindi. ShopierOrderId={ShopierOrderId} LocalOrderId={LocalOrderId}",
                            record.ShopierOrderId, record.LocalOrderId.Value);

                        record.LocalOrderId = null;
                        record.LastFulfillmentAttemptAt = DateTime.Now;
                        await _db.SaveChangesAsync(cancellationToken);
                    }

                    if (string.IsNullOrWhiteSpace(record.ShopierProductId))
                        continue;

                    var remoteProductId = record.ShopierProductId.Trim();
                    var mapping = await _db.ShopierProductMappings
                        .AsNoTracking()
                        .Include(x => x.Product)
                        .FirstOrDefaultAsync(x =>
                            x.IsActive && x.ShopierProductId == remoteProductId,
                            cancellationToken);

                    var product = mapping?.Product;
                    if (product == null || product.IsDeleted)
                        continue;

                    var quantity = Math.Max(1, record.Quantity);
                    var actualUnitPrice = record.TotalAmount > 0m
                        ? decimal.Round(record.TotalAmount / quantity, 2, MidpointRounding.AwayFromZero)
                        : 0m;

                    var pivot = record.FulfilledAt ?? record.ShopierCreatedAt;
                    if (pivot == default)
                        pivot = DateTime.Now;

                    string licenseType;
                    if (!TryNormalizeLicenseType(record.LicenseSelection, product, actualUnitPrice, out licenseType))
                    {
                        // Legacy Shopier kaydinda varyasyon secimi bos kalmis olabilir. Yeni lisans
                        // uretmeden, yalniz ayni kullanici + ayni urun + OrderId NULL mevcut lisans
                        // tek bir lisans turunu acikca isaret ediyorsa o bilgiyi kullan.
                        var inferred = await InferLegacyLicenseTypeFromExistingLicensesAsync(
                            user.Id, product.Id, quantity, pivot, cancellationToken);

                        if (string.IsNullOrWhiteSpace(inferred))
                            continue;

                        licenseType = inferred;
                    }

                    // Gecmis kayit onariminda urunun bugunku fiyatini degil, Shopier'de gercekten
                    // odenmis birim tutari siparis gecmisine yaziyoruz.
                    var displayUnitPrice = actualUnitPrice > 0m
                        ? actualUnitPrice
                        : (licenseType == "Lifetime" ? product.LifetimePrice : product.YearlyPrice);

                    var preparedLines = new List<PreparedLine>
                    {
                        new(product, quantity, licenseType, displayUnitPrice)
                    };

                    var repairedOrderId = await TryRepairLegacyCompletedOrderAsync(
                        record,
                        user,
                        preparedLines,
                        record.TotalAmount,
                        record.ShopierCreatedAt,
                        cancellationToken);

                    if (repairedOrderId.HasValue)
                        repairedCount++;
                }

                return repairedCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Shopier gecmis siparis onarimi tamamlanamadi. UserId={UserId}", userId);
                return 0;
            }
        }

        private async Task<string?> InferLegacyLicenseTypeFromExistingLicensesAsync(
            int userId,
            int productId,
            int quantity,
            DateTime pivot,
            CancellationToken cancellationToken)
        {
            var windowStart = pivot.AddHours(-48);
            var windowEnd = pivot.AddHours(48);

            var candidates = await _db.Licenses
                .AsNoTracking()
                .Where(x =>
                    x.UserId == userId &&
                    x.ProductId == productId &&
                    x.OrderId == null &&
                    x.CreatedAt >= windowStart &&
                    x.CreatedAt <= windowEnd)
                .Select(x => x.LicenseType)
                .ToListAsync(cancellationToken);

            var groups = candidates
                .Select(CanonicalLicenseType)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { LicenseType = g.Key, Count = g.Count() })
                .Where(x => x.Count >= quantity)
                .ToList();

            return groups.Count == 1 ? groups[0].LicenseType : null;
        }

        private static string? CanonicalLicenseType(string? value)
        {
            var token = NormalizeToken(value);
            if (token.Contains("sinirsiz") || token.Contains("sınırsız") || token.Contains("lifetime") || token.Contains("unlimited"))
                return "Lifetime";

            if (token.Contains("yillik") || token.Contains("yıllık") || token.Contains("yearly") || token.Contains("1year") || token.Contains("annual"))
                return "Yearly";

            return null;
        }

        internal async Task<ShopierFulfillmentResult> TryFulfillAsync(
            ShopierOrderDto order,
            bool verifiedOrderCreatedEvent,
            CancellationToken cancellationToken = default)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.Id))
                return Fail("Geçersiz Shopier siparişi.", FailedStatus);

            var shopierOrderId = order.Id.Trim();
            var record = await _db.ShopierOrders
                .FirstOrDefaultAsync(x => x.ShopierOrderId == shopierOrderId, cancellationToken);

            if (record == null)
                return Fail("Shopier sipariş kaydı henüz hazırlanmadı.", FailedStatus);

            record.LastFulfillmentAttemptAt = DateTime.Now;

            // Önceki turda lisanslar oluştu ancak kuyruklama kesildiyse e-postayı tekrar kuyruğa al.
            if (record.LocalOrderId.HasValue &&
                string.Equals(record.LocalFulfillmentStatus, CompletedStatus, StringComparison.OrdinalIgnoreCase))
            {
                await QueueDeliveryEmailIfNeededAsync(record, cancellationToken);
                return new ShopierFulfillmentResult
                {
                    Success = true,
                    Completed = true,
                    AlreadyCompleted = true,
                    Status = CompletedStatus,
                    Message = "Shopier siparişi daha önce lisanslandırılmış.",
                    LocalOrderId = record.LocalOrderId
                };
            }

            if (!IsPaymentConfirmed(order, verifiedOrderCreatedEvent))
            {
                await SetWaitingStateAsync(record, PaymentPendingStatus,
                    "Shopier ödeme durumu henüz başarılı olarak doğrulanmadı.", cancellationToken);
                return Fail(record.LocalFulfillmentMessage!, PaymentPendingStatus, record.LocalOrderId);
            }

            var currency = (order.Currency ?? string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(currency) && currency != "TRY" && currency != "TL" && currency != "TRL")
            {
                await SetWaitingStateAsync(record, CurrencyMismatchStatus,
                    $"Beklenmeyen para birimi: {currency}.", cancellationToken);
                return Fail(record.LocalFulfillmentMessage!, CurrencyMismatchStatus, record.LocalOrderId);
            }

            var email = NormalizeEmail(order.ShippingInfo?.Email ?? order.BillingInfo?.Email ?? record.CustomerEmail);
            if (string.IsNullOrWhiteSpace(email))
            {
                await SetWaitingStateAsync(record, WaitingUserStatus,
                    "Shopier siparişinde müşteri e-posta adresi bulunamadı.", cancellationToken);
                return Fail(record.LocalFulfillmentMessage!, WaitingUserStatus, record.LocalOrderId);
            }

            var user = await _db.Users
                .FirstOrDefaultAsync(x =>
                    !x.IsDeleted &&
                    x.IsActive &&
                    x.Role == "User" &&
                    x.Email.ToLower() == email,
                    cancellationToken);

            if (user == null)
            {
                await SetWaitingStateAsync(record, WaitingUserStatus,
                    $"Shopier e-postası ({email}) aktif bir NSX üyeliğiyle eşleşmedi. Lisans otomatik üretilmedi.", cancellationToken);
                return Fail(record.LocalFulfillmentMessage!, WaitingUserStatus, record.LocalOrderId);
            }

            var lineItems = order.LineItems?
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ProductId))
                .ToList() ?? new List<ShopierLineItemDto>();

            if (lineItems.Count == 0)
            {
                await SetWaitingStateAsync(record, WaitingProductStatus,
                    "Shopier siparişinde ürün satırı bulunamadı.", cancellationToken);
                return Fail(record.LocalFulfillmentMessage!, WaitingProductStatus, record.LocalOrderId);
            }

            var remoteProductIds = lineItems
                .Select(x => x.ProductId!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var mappings = await _db.ShopierProductMappings
                .Include(x => x.Product)
                .Where(x => x.IsActive && remoteProductIds.Contains(x.ShopierProductId))
                .ToListAsync(cancellationToken);

            var mappingByRemoteId = mappings
                .GroupBy(x => x.ShopierProductId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            var preparedLines = new List<PreparedLine>();
            decimal expectedTotal = 0m;

            foreach (var line in lineItems)
            {
                var remoteProductId = line.ProductId!.Trim();
                if (!mappingByRemoteId.TryGetValue(remoteProductId, out var mapping) || mapping.Product == null)
                {
                    await SetWaitingStateAsync(record, WaitingProductStatus,
                        $"Shopier ürün #{remoteProductId} henüz bir NSX ürünüyle eşleşmedi.", cancellationToken);
                    return Fail(record.LocalFulfillmentMessage!, WaitingProductStatus, record.LocalOrderId);
                }

                var product = mapping.Product;
                if (product.IsDeleted || !product.IsActive)
                {
                    await SetWaitingStateAsync(record, WaitingProductStatus,
                        $"{product.Name} NSX tarafında satışa kapalı olduğu için lisans üretilmedi.", cancellationToken);
                    return Fail(record.LocalFulfillmentMessage!, WaitingProductStatus, record.LocalOrderId);
                }

                var quantity = Math.Max(1, line.Quantity);
                var licenseSelection = ResolveLicenseSelection(line);
                var shopierUnitPrice = ParseMoney(line.Price);
                var shopierLineTotal = ParseMoney(line.Total);
                if (shopierUnitPrice <= 0m && shopierLineTotal > 0m)
                    shopierUnitPrice = decimal.Round(shopierLineTotal / quantity, 2, MidpointRounding.AwayFromZero);

                if (!TryNormalizeLicenseType(licenseSelection, product, shopierUnitPrice, out var licenseType))
                {
                    await SetWaitingStateAsync(record, WaitingVariantStatus,
                        $"{product.Name} için lisans türü doğrulanamadı. Shopier seçimi: {licenseSelection ?? "-"}.", cancellationToken);
                    return Fail(record.LocalFulfillmentMessage!, WaitingVariantStatus, record.LocalOrderId);
                }

                var unitPrice = licenseType == "Lifetime" ? product.LifetimePrice : product.YearlyPrice;
                if (unitPrice <= 0)
                {
                    await SetWaitingStateAsync(record, WaitingVariantStatus,
                        $"{product.Name} / {licenseType} için NSX satış fiyatı tanımlı değil.", cancellationToken);
                    return Fail(record.LocalFulfillmentMessage!, WaitingVariantStatus, record.LocalOrderId);
                }

                expectedTotal += unitPrice * quantity;
                preparedLines.Add(new PreparedLine(product, quantity, licenseType, unitPrice));
            }

            expectedTotal = decimal.Round(expectedTotal, 2, MidpointRounding.AwayFromZero);
            var actualTotal = ParseMoney(order.Totals?.Total);
            if (actualTotal <= 0m)
                actualTotal = decimal.Round(record.TotalAmount, 2, MidpointRounding.AwayFromZero);

            if (actualTotal <= 0m || Math.Abs(actualTotal - expectedTotal) > 0.50m)
            {
                await SetWaitingStateAsync(record, AmountMismatchStatus,
                    $"Shopier tutarı ile NSX ürün toplamı eşleşmedi. Shopier: {actualTotal:N2} TL, NSX: {expectedTotal:N2} TL.", cancellationToken);
                return Fail(record.LocalFulfillmentMessage!, AmountMismatchStatus, record.LocalOrderId);
            }

            // Eski sürümde "teslim edildi" işaretlenmiş fakat yerel Order oluşturulmamış olabilir.
            // Böyle bir kayıtta normal akışa devam etmek ikinci bir lisans üretebilir. Önce mevcut
            // lisansı Orders tablosuna bağlamayı deneriz; yeni lisans ASLA üretmeyiz.
            if (!record.LocalOrderId.HasValue &&
                string.Equals(record.LocalFulfillmentStatus, CompletedStatus, StringComparison.OrdinalIgnoreCase))
            {
                var repairedOrderId = await TryRepairLegacyCompletedOrderAsync(
                    record,
                    user,
                    preparedLines,
                    actualTotal,
                    ParseShopierDate(order.DateCreated) ?? record.ShopierCreatedAt,
                    cancellationToken);

                if (repairedOrderId.HasValue)
                {
                    return new ShopierFulfillmentResult
                    {
                        Success = true,
                        Completed = true,
                        AlreadyCompleted = true,
                        Status = CompletedStatus,
                        Message = "Eski Shopier teslimatı sipariş geçmişine güvenli şekilde bağlandı.",
                        LocalOrderId = repairedOrderId
                    };
                }

                var repairRecord = await _db.ShopierOrders
                    .FirstAsync(x => x.ShopierOrderId == shopierOrderId, cancellationToken);
                await SetWaitingStateAsync(
                    repairRecord,
                    FailedStatus,
                    "Shopier kaydı teslim edildi görünüyor ancak mevcut lisans yerel sipariş kaydıyla güvenli şekilde eşleştirilemedi. Yeni lisans üretilmedi.",
                    cancellationToken);
                return Fail(repairRecord.LocalFulfillmentMessage!, FailedStatus, repairRecord.LocalOrderId);
            }

            // Aynı Shopier siparişinin iki eşzamanlı webhook turunda iki kez lisans üretmesini önler.
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                record = await _db.ShopierOrders
                    .FirstAsync(x => x.ShopierOrderId == shopierOrderId, cancellationToken);
                await _db.Entry(record).ReloadAsync(cancellationToken);

                if (record.LocalOrderId.HasValue &&
                    string.Equals(record.LocalFulfillmentStatus, CompletedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.CommitAsync(cancellationToken);
                    await QueueDeliveryEmailIfNeededAsync(record, cancellationToken);
                    return new ShopierFulfillmentResult
                    {
                        Success = true,
                        Completed = true,
                        AlreadyCompleted = true,
                        Status = CompletedStatus,
                        Message = "Shopier siparişi daha önce lisanslandırılmış.",
                        LocalOrderId = record.LocalOrderId
                    };
                }

                foreach (var prepared in preparedLines)
                {
                    // Ürün satırı transaction öncesi yüklendi; stok değerini transaction içinde güncel haliyle tekrar al.
                    var product = await _db.Products.FirstAsync(x => x.Id == prepared.Product.Id, cancellationToken);
                    if (product.StockQuantity < prepared.Quantity)
                    {
                        record.LocalFulfillmentStatus = StockProblemStatus;
                        record.LocalFulfillmentMessage = $"{product.Name} için yeterli stok bulunamadı.";
                        record.LastFulfillmentAttemptAt = DateTime.Now;
                        await _db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return Fail(record.LocalFulfillmentMessage, StockProblemStatus, record.LocalOrderId);
                    }
                }

                var localOrder = new Order
                {
                    OrderNumber = BuildShopierOrderNumber(shopierOrderId),
                    UserId = user.Id,
                    TotalAmount = actualTotal,
                    PaymentStatus = "Paid",
                    OrderStatus = "Completed",
                    CreatedAt = ParseShopierDate(order.DateCreated) ?? DateTime.Now
                };

                foreach (var prepared in preparedLines)
                {
                    var product = await _db.Products.FirstAsync(x => x.Id == prepared.Product.Id, cancellationToken);
                    product.StockQuantity -= prepared.Quantity;

                    localOrder.OrderItems.Add(new OrderItem
                    {
                        ProductId = product.Id,
                        Quantity = prepared.Quantity,
                        UnitPrice = prepared.UnitPrice,
                        LicenseType = prepared.LicenseType,
                        Product = product
                    });

                    for (var i = 0; i < prepared.Quantity; i++)
                    {
                        localOrder.Licenses.Add(new License
                        {
                            UserId = user.Id,
                            ProductId = product.Id,
                            LicenseKey = await GenerateUniqueLicenseKeyAsync(cancellationToken),
                            LicenseType = prepared.LicenseType,
                            ProductCode = GetProductCodeForLicense(product),
                            LicenseStatus = "Active",
                            MaxDeviceCount = 1,
                            OfflineAllowed = true,
                            StartDate = DateTime.Now,
                            EndDate = prepared.LicenseType == "Lifetime" ? null : DateTime.Now.AddYears(1),
                            IsActive = true,
                            CreatedAt = DateTime.Now,
                            Product = product
                        });
                    }
                }

                _db.Orders.Add(localOrder);
                await _db.SaveChangesAsync(cancellationToken);

                record.LocalOrderId = localOrder.Id;
                record.LocalFulfillmentStatus = CompletedStatus;
                record.LocalFulfillmentMessage = null;
                record.FulfilledAt = DateTime.Now;
                record.LastFulfillmentAttemptAt = DateTime.Now;
                await _db.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                await QueueDeliveryEmailIfNeededAsync(record, cancellationToken);

                return new ShopierFulfillmentResult
                {
                    Success = true,
                    Completed = true,
                    Status = CompletedStatus,
                    Message = "Shopier ödemesi doğrulandı; NSX lisansı otomatik oluşturuldu ve kullanıcı hesabına tanımlandı.",
                    LocalOrderId = localOrder.Id
                };
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "Shopier siparişi lisanslandırılamadı. ShopierOrderId={ShopierOrderId}", shopierOrderId);

                // Ayrı bir kaydetme turunda hata durumunu görünür tutmaya çalış.
                try
                {
                    var failedRecord = await _db.ShopierOrders.FirstOrDefaultAsync(x => x.ShopierOrderId == shopierOrderId, cancellationToken);
                    if (failedRecord != null && !string.Equals(failedRecord.LocalFulfillmentStatus, CompletedStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        failedRecord.LocalFulfillmentStatus = FailedStatus;
                        failedRecord.LocalFulfillmentMessage = TrimTo(ex.Message, 1000);
                        failedRecord.LastFulfillmentAttemptAt = DateTime.Now;
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                }
                catch (Exception stateEx)
                {
                    _logger.LogWarning(stateEx, "Shopier lisans teslimat hata durumu kaydedilemedi. ShopierOrderId={ShopierOrderId}", shopierOrderId);
                }

                return Fail("Shopier siparişi lisanslandırılırken beklenmeyen bir hata oluştu.", FailedStatus, record.LocalOrderId);
            }
        }

        private async Task<int?> TryRepairLegacyCompletedOrderAsync(
            ShopierOrderRecord record,
            User user,
            IReadOnlyCollection<PreparedLine> preparedLines,
            decimal actualTotal,
            DateTime orderCreatedAt,
            CancellationToken cancellationToken)
        {
            if (record.LocalOrderId.HasValue || preparedLines.Count == 0)
                return record.LocalOrderId;

            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            try
            {
                var freshRecord = await _db.ShopierOrders
                    .FirstAsync(x => x.Id == record.Id, cancellationToken);
                await _db.Entry(freshRecord).ReloadAsync(cancellationToken);

                if (freshRecord.LocalOrderId.HasValue)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return freshRecord.LocalOrderId;
                }

                var orderNumber = BuildShopierOrderNumber(freshRecord.ShopierOrderId);
                var existingOrder = await _db.Orders
                    .FirstOrDefaultAsync(x => x.OrderNumber == orderNumber, cancellationToken);

                if (existingOrder != null)
                {
                    if (existingOrder.UserId != user.Id)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        _logger.LogWarning(
                            "Shopier geçmiş sipariş onarımında kullanıcı uyuşmazlığı. ShopierOrderId={ShopierOrderId} OrderId={OrderId}",
                            freshRecord.ShopierOrderId, existingOrder.Id);
                        return null;
                    }

                    freshRecord.LocalOrderId = existingOrder.Id;
                    freshRecord.LocalFulfillmentStatus = CompletedStatus;
                    freshRecord.LocalFulfillmentMessage = null;
                    freshRecord.FulfilledAt ??= existingOrder.CreatedAt;
                    // Legacy teslimat daha önce yapılmıştır; sonraki senkronun aynı e-postayı yeniden
                    // kuyruğa almaması için teslim işaretini koru.
                    freshRecord.DeliveryEmailQueuedAt ??= freshRecord.FulfilledAt ?? DateTime.Now;
                    freshRecord.LastFulfillmentAttemptAt = DateTime.Now;
                    await _db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return existingOrder.Id;
                }

                var pivot = freshRecord.FulfilledAt ?? orderCreatedAt;
                if (pivot == default)
                    pivot = freshRecord.ShopierCreatedAt == default ? DateTime.Now : freshRecord.ShopierCreatedAt;

                // Eski teslimat lisansı normalde ödeme anında üretilir. Geniş fakat güvenli bir pencere
                // kullanıp yalnız aynı kullanıcı + ürün + lisans tipi + OrderId NULL lisansları seçiyoruz.
                var windowStart = pivot.AddHours(-48);
                var windowEnd = pivot.AddHours(48);
                var selectedLicenses = new List<License>();
                var selectedIds = new HashSet<int>();

                foreach (var prepared in preparedLines)
                {
                    var candidates = await _db.Licenses
                        .Where(x =>
                            x.UserId == user.Id &&
                            x.ProductId == prepared.Product.Id &&
                            x.OrderId == null &&
                            x.CreatedAt >= windowStart &&
                            x.CreatedAt <= windowEnd)
                        .ToListAsync(cancellationToken);

                    var selected = candidates
                        .Where(x =>
                            !selectedIds.Contains(x.Id) &&
                            string.Equals(CanonicalLicenseType(x.LicenseType), prepared.LicenseType, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => Math.Abs((x.CreatedAt - pivot).TotalSeconds))
                        .Take(prepared.Quantity)
                        .ToList();

                    if (selected.Count != prepared.Quantity)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        _logger.LogInformation(
                            "Shopier legacy sipariş için güvenli mevcut lisans eşleşmesi bulunamadı. ShopierOrderId={ShopierOrderId} ProductId={ProductId}",
                            freshRecord.ShopierOrderId, prepared.Product.Id);
                        return null;
                    }

                    foreach (var license in selected)
                    {
                        selectedIds.Add(license.Id);
                        selectedLicenses.Add(license);
                    }
                }

                var total = actualTotal > 0m
                    ? decimal.Round(actualTotal, 2, MidpointRounding.AwayFromZero)
                    : decimal.Round(preparedLines.Sum(x => x.UnitPrice * x.Quantity), 2, MidpointRounding.AwayFromZero);

                var localOrder = new Order
                {
                    OrderNumber = orderNumber,
                    UserId = user.Id,
                    TotalAmount = total,
                    PaymentStatus = "Paid",
                    OrderStatus = "Completed",
                    CreatedAt = orderCreatedAt == default ? pivot : orderCreatedAt
                };

                foreach (var prepared in preparedLines)
                {
                    localOrder.OrderItems.Add(new OrderItem
                    {
                        ProductId = prepared.Product.Id,
                        Quantity = prepared.Quantity,
                        UnitPrice = prepared.UnitPrice,
                        LicenseType = prepared.LicenseType
                    });
                }

                _db.Orders.Add(localOrder);
                await _db.SaveChangesAsync(cancellationToken);

                foreach (var license in selectedLicenses)
                    license.OrderId = localOrder.Id;

                freshRecord.LocalOrderId = localOrder.Id;
                freshRecord.LocalFulfillmentStatus = CompletedStatus;
                freshRecord.LocalFulfillmentMessage = null;
                freshRecord.FulfilledAt ??= selectedLicenses
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => (DateTime?)x.CreatedAt)
                    .FirstOrDefault() ?? DateTime.Now;
                freshRecord.DeliveryEmailQueuedAt ??= freshRecord.FulfilledAt;
                freshRecord.LastFulfillmentAttemptAt = DateTime.Now;

                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Shopier legacy teslimatı yerel sipariş geçmişine bağlandı. ShopierOrderId={ShopierOrderId} OrderId={OrderId}",
                    freshRecord.ShopierOrderId, localOrder.Id);

                return localOrder.Id;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogWarning(ex,
                    "Shopier legacy sipariş geçmişi onarılamadı. ShopierOrderId={ShopierOrderId}", record.ShopierOrderId);
                return null;
            }
        }

        private async Task QueueDeliveryEmailIfNeededAsync(ShopierOrderRecord record, CancellationToken cancellationToken)
        {
            if (!record.LocalOrderId.HasValue || record.DeliveryEmailQueuedAt.HasValue)
                return;

            var order = await _db.Orders
                .AsNoTracking()
                .Include(x => x.User)
                .Include(x => x.OrderItems)
                    .ThenInclude(x => x.Product)
                .Include(x => x.Licenses)
                    .ThenInclude(x => x.Product)
                .FirstOrDefaultAsync(x => x.Id == record.LocalOrderId.Value, cancellationToken);

            if (order?.User == null || string.IsNullOrWhiteSpace(order.User.Email))
                return;

            var siteUrl = ResolvePublicSiteUrl();
            var subject = $"NSX Yazılım | Kart ödemeniz alındı, lisansınız hazır ({order.OrderNumber})";
            var queued = _mailQueue.TryQueue(new BackgroundMailMessage(
                order.User.Email,
                order.User.FullName,
                subject,
                EmailTemplates.OrderCompleted(order, siteUrl),
                "ShopierLicenseDelivery",
                $"Shopier siparişi {record.ShopierOrderId} için otomatik lisans teslimatı. NSX sipariş: {order.OrderNumber}"));

            if (!queued)
            {
                _logger.LogWarning("Shopier lisans e-postası kuyruğa alınamadı. ShopierOrderId={ShopierOrderId}", record.ShopierOrderId);
                return;
            }

            var trackedRecord = await _db.ShopierOrders.FirstOrDefaultAsync(x => x.Id == record.Id, cancellationToken);
            if (trackedRecord != null && !trackedRecord.DeliveryEmailQueuedAt.HasValue)
            {
                trackedRecord.DeliveryEmailQueuedAt = DateTime.Now;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        private async Task SetWaitingStateAsync(
            ShopierOrderRecord record,
            string status,
            string message,
            CancellationToken cancellationToken)
        {
            record.LocalFulfillmentStatus = status;
            record.LocalFulfillmentMessage = TrimTo(message, 1000);
            record.LastFulfillmentAttemptAt = DateTime.Now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        private async Task<string> GenerateUniqueLicenseKeyAsync(CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var key = GenerateLicenseKey();
                var exists = await _db.Licenses.AnyAsync(x => x.LicenseKey == key, cancellationToken);
                if (!exists)
                    return key;
            }

            throw new InvalidOperationException("Benzersiz lisans anahtarı üretilemedi.");
        }

        private static string GenerateLicenseKey()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = RandomNumberGenerator.GetBytes(20);
            var code = new char[20];

            for (var i = 0; i < code.Length; i++)
                code[i] = chars[bytes[i] % chars.Length];

            return $"NSX-{new string(code, 0, 5)}-{new string(code, 5, 5)}-{new string(code, 10, 5)}-{new string(code, 15, 5)}";
        }

        private static string BuildShopierOrderNumber(string shopierOrderId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(shopierOrderId));
            var token = Convert.ToHexString(hash.AsSpan(0, 8));
            return $"NSX-SHP-{token}";
        }

        private static bool IsPaymentConfirmed(ShopierOrderDto order, bool verifiedOrderCreatedEvent)
        {
            var status = NormalizeToken(order.PaymentStatus);
            if (status is "paid" or "completed" or "success" or "successful" or "captured" or "approved")
                return true;

            // Shopier'de order.created olayı, ödeme tamamlanıp sipariş oluştuğunda gelir.
            // İmzalı order.created payload'ında paymentStatus alanı boş gelirse event semantiğini kabul et.
            return verifiedOrderCreatedEvent && string.IsNullOrWhiteSpace(status);
        }

        private static string? ResolveLicenseSelection(ShopierLineItemDto line)
        {
            var selections = line.Selection ?? new List<ShopierSelectionDto>();
            var explicitLicense = selections.FirstOrDefault(x =>
                NormalizeToken(x.VariationTitle).Contains("lisans") ||
                NormalizeToken(x.VariationTitle).Contains("license"));

            return explicitLicense?.Title ?? selections.FirstOrDefault()?.Title;
        }

        private static bool TryNormalizeLicenseType(string? selection, Product product, decimal shopierUnitPrice, out string licenseType)
        {
            var normalized = NormalizeToken(selection);
            if (normalized.Contains("sinirsiz") || normalized.Contains("sınırsız") || normalized.Contains("lifetime") || normalized.Contains("unlimited"))
            {
                licenseType = "Lifetime";
                return true;
            }

            if (normalized.Contains("yillik") || normalized.Contains("yıllık") || normalized.Contains("yearly") || normalized.Contains("1year") || normalized.Contains("annual"))
            {
                licenseType = "Yearly";
                return true;
            }

            // Shopier varyasyonu yoksa yalnız tek ücretli lisans tipi bulunan ürünlerde güvenli şekilde çıkarım yap.
            if (product.YearlyPrice > 0 && product.LifetimePrice <= 0)
            {
                licenseType = "Yearly";
                return true;
            }

            if (product.LifetimePrice > 0 && product.YearlyPrice <= 0)
            {
                licenseType = "Lifetime";
                return true;
            }

            // Bazı Shopier siparişlerinde seçim dizisi boş gelebilir. Böyle bir durumda fiyat yalnızca
            // tek bir NSX lisans fiyatıyla açık biçimde eşleşiyorsa güvenli fiyat çıkarımı yap.
            if (shopierUnitPrice > 0m)
            {
                const decimal tolerance = 0.50m;
                var yearlyMatch = product.YearlyPrice > 0m && Math.Abs(shopierUnitPrice - product.YearlyPrice) <= tolerance;
                var lifetimeMatch = product.LifetimePrice > 0m && Math.Abs(shopierUnitPrice - product.LifetimePrice) <= tolerance;

                if (yearlyMatch && !lifetimeMatch)
                {
                    licenseType = "Yearly";
                    return true;
                }

                if (lifetimeMatch && !yearlyMatch)
                {
                    licenseType = "Lifetime";
                    return true;
                }
            }

            licenseType = string.Empty;
            return false;
        }

        private static string NormalizeToken(string? value)
        {
            return (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
        }

        private static string NormalizeEmail(string? email)
            => (email ?? string.Empty).Trim().ToLowerInvariant();

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

            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto)
                ? dto.LocalDateTime
                : null;
        }

        private static string GetProductCodeForLicense(Product product)
        {
            var code = NormalizeProductCode(product.ProductCode);
            if (!string.IsNullOrWhiteSpace(code))
                return code;

            code = NormalizeProductCode(product.Slug);
            if (!string.IsNullOrWhiteSpace(code))
                return code;

            code = NormalizeProductCode(product.Name);
            return string.IsNullOrWhiteSpace(code) ? $"NSXPRODUCT{product.Id}" : code;
        }

        private static string NormalizeProductCode(string? value)
        {
            return new string((value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private string ResolvePublicSiteUrl()
        {
            var configured = _configuration["Site:Url"];
            return string.IsNullOrWhiteSpace(configured)
                ? "https://www.nsxyazilim.com"
                : configured.Trim().TrimEnd('/');
        }

        private static ShopierFulfillmentResult Fail(string message, string status, int? localOrderId = null)
            => new()
            {
                Success = false,
                Completed = false,
                Status = status,
                Message = message,
                LocalOrderId = localOrderId
            };

        private static string? TrimTo(string? value, int maxLength)
        {
            var cleaned = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;
            return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
        }

        private sealed record PreparedLine(Product Product, int Quantity, string LicenseType, decimal UnitPrice);
    }
}
