using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.Hubs;
using NSYazilim.Web.TeknikServisCloud;
using NSYazilim.Web.SalonTakipCloud;
using NSYazilim.Web.SalonTakipCloud.Services;
using NSYazilim.Web.VeresiyeFreeCloud;
using NSYazilim.Web.CaritakipCloud;
using NSYazilim.Web.CaritakipCloud.Services;
using SQLitePCL;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
}

// Razor/HTML/JSON çıktılarında Türkçe karakterler kaynak kodda HTML entity olarak görünmesin.
builder.Services.Configure<WebEncoderOptions>(options =>
{
    // Türkçe ve İngilizce içerikleri kaynakta okunabilir tut; HTML açısından
    // özel karakterler yine encoder tarafından korunur.
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
});

builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys");
Directory.CreateDirectory(dataProtectionPath);
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("NSYazilim.Web");
if (OperatingSystem.IsWindows())
    dataProtectionBuilder.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddScoped<CampaignDiscountService>();
builder.Services.AddScoped<OfflineLicenseService>();
builder.Services.AddScoped<InstallationVersionTelemetryService>();
builder.Services.AddSingleton<ClientIpService>();
builder.Services.AddScoped<OnlineVisitorTracker>();
builder.Services.AddScoped<SiteVisitTrackingService>();
builder.Services.AddSingleton<SiteVisitTrackingQueue>();
builder.Services.AddHostedService<SiteVisitTrackingWorker>();
builder.Services.AddSingleton<UserLocationUpdateQueue>();
builder.Services.AddHostedService<UserLocationUpdateWorker>();
builder.Services.AddSingleton<DownloadLocationUpdateQueue>();
builder.Services.AddHostedService<DownloadLocationUpdateWorker>();
builder.Services.AddSingleton<BackgroundMailQueue>();
builder.Services.AddHostedService<BackgroundMailWorker>();
builder.Services.AddHttpClient<IpGeolocationService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(4);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NSX-Yazilim-Web/1.0");
});
builder.Services.Configure<ShopierOptions>(builder.Configuration.GetSection("Shopier"));
builder.Services.AddScoped<ShopierOrderFulfillmentService>();
builder.Services.AddHttpClient<ShopierService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NSX-Yazilim-Shopier/1.0");
});
builder.Services.AddHostedService<ShopierProductSyncWorker>();
builder.Services.AddHostedService<SiteVisitLocationBackfillWorker>();
builder.Services.AddSingleton<LiveChatMemoryStore>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LocalizationCatalog>();
builder.Services.AddScoped<SiteLocalizationService>();
builder.Services.AddScoped<SeoLanguageUrlService>();
builder.Services.AddScoped<LocalizationTranslationQueueService>();
builder.Services.AddSingleton<LocalizationTranslationQueueSignal>();
builder.Services.AddSingleton<LocalizationCaptureService>();
builder.Services.AddHostedService<LocalizationCaptureWorker>();
builder.Services.AddScoped<LocalizationGuideTranslationService>();
builder.Services.Configure<LocalizationMachineTranslationOptions>(
    builder.Configuration.GetSection("Localization:MachineTranslation"));
builder.Services.AddHttpClient<LocalizationMachineTranslationService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NSX-Localization-Engine/3.0");
});
builder.Services.AddHostedService<LocalizationTranslationQueueWorker>();
builder.Services.AddHostedService<LocalizationStartupBootstrapWorker>();
builder.Services.AddSignalR();
builder.Services.AddNsxTeknikServisCloud();
builder.Services.AddNsxSalonTakipCloud();
builder.Services.AddNsxVeresiyeFreeCloud();
builder.Services.AddNsxCaritakipCloud();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DefaultConnection bağlantı bilgisi appsettings.json içinde bulunamadı.");
if (IsMissingOrPlaceholderConnectionString(connectionString))
{
    throw new InvalidOperationException("DefaultConnection guvenli bir MySQL baglanti bilgisi ile ayarlanmalidir.");
}

if (!builder.Environment.IsDevelopment()) {
    var database = new MySqlConnector.MySqlConnectionStringBuilder(connectionString);
    if (string.Equals(database.UserID,"root",StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Production requires a dedicated database user; root is not allowed.");
    if (database.Server is not ("localhost" or "127.0.0.1" or "::1") && database.SslMode != MySqlConnector.MySqlSslMode.VerifyFull)
        throw new InvalidOperationException("Remote production database requires SslMode=VerifyFull.");
}

// Büyük dosya upload limiti: 1 GB
const long maxUploadSize = 1_073_741_824;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadSize;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadSize;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = maxUploadSize;
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Admin/Login";
        options.AccessDeniedPath = "/Admin/Login";
        options.Cookie.Name = "NSYazilim.Admin.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context => {
            if (context.Request.Path.StartsWithSegments("/api/uzaktan-yardim")) { context.Response.StatusCode = 401; return Task.CompletedTask; }
            context.Response.Redirect(context.RedirectUri); return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context => {
            if (context.Request.Path.StartsWithSegments("/api/uzaktan-yardim")) { context.Response.StatusCode = 403; return Task.CompletedTask; }
            context.Response.Redirect(context.RedirectUri); return Task.CompletedTask;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, RemoteSupportAuthenticationHandler>(RemoteSupportAuthenticationHandler.SchemeName, _ => {});

builder.Services.AddAuthorization(options => options.AddPolicy(RemoteSupportAuthenticationHandler.PolicyName,
    policy => policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, RemoteSupportAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser().RequireRole("Admin", "Support")));

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "NSX.Client.Session";
    options.IdleTimeout = TimeSpan.FromDays(7);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

builder.Services.AddControllersWithViews(options =>
{
    options.MaxModelBindingCollectionSize = int.MaxValue;
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

var app = builder.Build();

// NSX Uzaktan Yardım API - ana domain altında signaling + uzak oturum WebSocket relay.
// Programların sunucu adresi: https://nsxyazilim.com/api/uzaktan-yardim/
var nsxRemoteClients = new ConcurrentDictionary<string, NsxRemoteClientState>(StringComparer.OrdinalIgnoreCase);
var nsxRemoteSupportRequests = new ConcurrentDictionary<Guid, NsxRemoteSupportRequestState>();
var nsxRemoteIncomingRequests = new ConcurrentDictionary<Guid, NsxRemoteIncomingRequestState>();
var nsxRemoteSessions = new ConcurrentDictionary<Guid, NsxRemoteSessionState>();


// Her publish paketine BAT tarafinda uretilen nsx-deploy.json kimligi eklenir.
// Boylece Plesk uzerinde gercekten hangi DLL paketinin calistigi aninda dogrulanabilir.
var deploymentIdentity = NsxDeploymentIdentity.Load(builder.Environment.ContentRootPath);
app.Logger.LogInformation(
    "NSX deploy kimligi: {BuildId} / {PublishedAtUtc}",
    deploymentIdentity.BuildId,
    deploymentIdentity.PublishedAtUtc);

// NSX Localization Engine: canlı DB'yi veri kaybı olmadan hazırlar.
// Dil/çeviri tabloları yoksa oluşturulur; mevcut içerik ve kullanıcı verileri korunur.
await LocalizationSchemaInitializer.EnsureAsync(app.Services, app.Logger);
await ShopierSchemaInitializer.EnsureAsync(app.Services, app.Logger);

// Salon Takip ayrı SQLite veritabanını site açılışında App_Data altında hazırla.
// Hata ana mağazayı kapatmaz; ayrıntı /api/salontakip/db-check üzerinden görülebilir.
try
{
    Batteries_V2.Init();
    var salonStore = app.Services.GetRequiredService<SalonTakipMySqlStore>();
    salonStore.EnsureSchema();
    app.Logger.LogInformation("Salon Takip SQLite veritabanı hazır: App_Data/nsx_salon_takip_cloud.db");
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Salon Takip SQLite veritabanı başlangıçta hazırlanamadı.");
}

// Cari Takip Cloud MySQL şema hatası ana web sitesinin açılışını engellemez.
// Sağlık durumu /Api/CaritakipCloud/health üzerinden izlenir.
try
{
    var cariStore = app.Services.GetRequiredService<CaritakipCloudStore>();
    await cariStore.EnsureSchemaAsync();
    var cariBackupStore = app.Services.GetRequiredService<CaritakipDatabaseBackupStore>();
    cariBackupStore.EnsureStorageReady();
    app.Logger.LogInformation("Cari Takip Cloud MySQL şeması hazır.");
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Cari Takip Cloud MySQL şeması başlangıçta hazırlanamadı.");
}

if (app.Environment.IsDevelopment())
{
    using var adminSeedScope = app.Services.CreateScope();
    var db = adminSeedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var adminEmail = builder.Configuration["AdminSeed:Email"] ?? string.Empty;
    var adminPassword = builder.Configuration["AdminSeed:Password"] ?? string.Empty;
    var adminFullName = builder.Configuration["AdminSeed:FullName"] ?? "NSX Admin";

    if (!string.IsNullOrWhiteSpace(adminEmail)
        && !string.IsNullOrWhiteSpace(adminPassword)
        && !db.Users.Any(x => x.Email == adminEmail))
    {
        db.Users.Add(new User
        {
            FullName = adminFullName,
            Email = adminEmail,
            PasswordHash = PasswordHasher.Hash(adminPassword),
            Role = "Admin",
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.Now
        });

        db.SaveChanges();
    }
}


// Ürün kategorisi eski canlı veritabanlarında bulunmayabilir. Kolonu ekle ve
// mevcut ürünleri ad/kod bilgisinden bir defaya mahsus sınıflandır.
{
    using var productCategoryScope = app.Services.CreateScope();
    try
    {
        var db = productCategoryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            connection.Open();

        var categoryColumnCreated = false;
        using (var columnCheck = connection.CreateCommand())
        {
            columnCheck.CommandText = @"SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND LOWER(TABLE_NAME) = LOWER('Products')
                  AND LOWER(COLUMN_NAME) = LOWER('Category');";
            var categoryColumnExists = Convert.ToInt32(columnCheck.ExecuteScalar()) > 0;
            if (!categoryColumnExists)
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE `Products` ADD COLUMN `Category` varchar(40) CHARACTER SET utf8mb4 NULL;");
                categoryColumnCreated = true;
            }
        }

        // Eski kurulumdan kategori kolonu ilk kez ekleniyorsa mevcut ürünleri yalnızca bir kez sınıflandır.
        // Admin daha sonra kategori alanını boş bırakır/değiştirirse uygulama açılışında tekrar müdahale etmez.
        if (categoryColumnCreated)
        {
            var uncategorizedProducts = db.Products
                .Where(x => x.Category == null || x.Category == "")
                .ToList();

            foreach (var product in uncategorizedProducts)
                product.Category = ProductCategoryHelper.Normalize(null, product.Name, product.Slug, product.ProductCode);

            if (uncategorizedProducts.Count > 0)
                db.SaveChanges();
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Ürün kategori kolonu hazırlanamadı.");
    }
}


// Kategoriler admin panelinden yönetilir.
// Varsayılan kategori kayıtları yalnızca ProductCategories tablosu ilk kez oluşturulurken eklenir.
// Var olan tabloda admin tarafından silinen bir kategori publish/restart sonrasında ASLA geri eklenmez.
{
    using var productCategoryCatalogScope = app.Services.CreateScope();
    try
    {
        var db = productCategoryCatalogScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        var categoryTableExisted = false;
        using (var tableCheck = connection.CreateCommand())
        {
            tableCheck.CommandText = @"SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                  AND LOWER(TABLE_NAME) = LOWER('ProductCategories');";
            categoryTableExisted = Convert.ToInt32(await tableCheck.ExecuteScalarAsync()) > 0;
        }

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `ProductCategories` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `Key` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
            `Label` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
            `SortOrder` int NOT NULL DEFAULT 0,
            `IsActive` tinyint(1) NOT NULL DEFAULT 1,
            `CreatedAt` datetime(6) NOT NULL,
            `UpdatedAt` datetime(6) NULL,
            PRIMARY KEY (`Id`),
            UNIQUE INDEX `IX_ProductCategories_Key` (`Key`),
            INDEX `IX_ProductCategories_IsActive_SortOrder` (`IsActive`, `SortOrder`)
        ) CHARACTER SET=utf8mb4;");

        if (!categoryTableExisted)
        {
            var sortOrder = 10;
            foreach (var option in ProductCategoryHelper.DefaultOptions)
            {
                db.ProductCategories.Add(new ProductCategory
                {
                    Key = option.Key,
                    Label = option.Label,
                    SortOrder = sortOrder,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                });
                sortOrder += 10;
            }

            await db.SaveChangesAsync();
        }

        // Eski sürümlerin otomatik eklediği `genel / İşletme Yazılımları` kategorisini,
        // aktif bir ürün kullanmıyorsa bir defaya mahsus temizle. Sonraki açılışlarda geri eklenmez.
        var legacyGeneralCategory = await db.ProductCategories
            .FirstOrDefaultAsync(x => x.Key == "genel");
        if (legacyGeneralCategory != null &&
            !await db.Products.AnyAsync(x => !x.IsDeleted && x.Category == "genel"))
        {
            db.ProductCategories.Remove(legacyGeneralCategory);
            await db.SaveChangesAsync();
        }

        var categoryOptions = await db.ProductCategories
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Label)
            .Select(x => new ProductCategoryHelper.Option(x.Key, x.Label, x.IsActive))
            .ToListAsync();
        ProductCategoryHelper.Configure(categoryOptions);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Ürün kategori kataloğu hazırlanamadı.");
    }
}


// Eski kurulumlarda bulunmayan legacy kampanya tablosunu veri kaybi olmadan hazirla.
// Yeni kampanya yonetimi CampaignCoupons kullanir; tablo eski yonetim rotalariyla uyumluluk icindir.
{
    using var campaignSchemaScope = app.Services.CreateScope();
    try
    {
        var db = campaignSchemaScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `Campaigns` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `Code` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `Name` varchar(120) CHARACTER SET utf8mb4 NOT NULL,
            `Description` varchar(300) CHARACTER SET utf8mb4 NULL,
            `IsActive` tinyint(1) NOT NULL DEFAULT 1,
            `MinimumCartTotal` decimal(18,2) NOT NULL DEFAULT 0,
            `DiscountType` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Percent',
            `DiscountValue` decimal(18,2) NOT NULL DEFAULT 0,
            `StartDate` datetime(6) NULL,
            `EndDate` datetime(6) NULL,
            `CreatedAt` datetime(6) NOT NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_Campaigns_Code` (`Code`),
            INDEX `IX_Campaigns_IsActive_Dates` (`IsActive`, `StartDate`, `EndDate`)
        ) CHARACTER SET=utf8mb4;");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Legacy kampanya tablosu hazirlanamadi; CampaignCoupons kullanilmaya devam edecek.");
    }
}


{
    using var userLocationScope = app.Services.CreateScope();

    try
    {
        var db = userLocationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userLocationAlterSql = new[]
        {
            "ALTER TABLE `Users` ADD COLUMN `LastIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Users` ADD COLUMN `LastCity` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Users` ADD COLUMN `LastRegion` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Users` ADD COLUMN `LastCountry` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Users` ADD COLUMN `LastGeoLookupAt` datetime(6) NULL;",
            "ALTER TABLE `Users` ADD COLUMN `LastLoginAt` datetime(6) NULL;"
        };

        foreach (var sql in userLocationAlterSql)
        {
            ExecuteMySqlAlterIfColumnMissing(db, sql);
        }
    }
    catch
    {
    }
}

{
    using var demoDownloadScope = app.Services.CreateScope();

    try
    {
        var db = demoDownloadScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `DemoDownloads` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `ProductId` int NOT NULL,
            `UserId` int NOT NULL,
            `FullName` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `Email` varchar(180) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `Phone` varchar(50) CHARACTER SET utf8mb4 NULL,
            `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `City` varchar(120) CHARACTER SET utf8mb4 NULL,
            `Region` varchar(120) CHARACTER SET utf8mb4 NULL,
            `Country` varchar(120) CHARACTER SET utf8mb4 NULL,
            `GeoLookupAt` datetime(6) NULL,
            `UserAgent` varchar(500) CHARACTER SET utf8mb4 NULL,
            `DownloadedAt` datetime(6) NOT NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_DemoDownloads_ProductId` (`ProductId`),
            INDEX `IX_DemoDownloads_UserId` (`UserId`),
            INDEX `IX_DemoDownloads_DownloadedAt` (`DownloadedAt`)
        ) CHARACTER SET=utf8mb4;");

        var demoDownloadLocationAlterSql = new[]
        {
            "ALTER TABLE `DemoDownloads` ADD COLUMN `City` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `DemoDownloads` ADD COLUMN `Region` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `DemoDownloads` ADD COLUMN `Country` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `DemoDownloads` ADD COLUMN `GeoLookupAt` datetime(6) NULL;"
        };

        foreach (var sql in demoDownloadLocationAlterSql)
        {
            ExecuteMySqlAlterIfColumnMissing(db, sql);
        }
    }
    catch
    {
    }
}

{
    using var freeActivationScope = app.Services.CreateScope();
    try
    {
        var db = freeActivationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `FreeLicenseActivationTokens` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `UserId` int NOT NULL,
            `ProductId` int NOT NULL,
            `LicenseId` int NOT NULL,
            `TokenHash` varchar(64) CHARACTER SET ascii NOT NULL,
            `ProductCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'NSXVERESIYETAKIPPROFREE',
            `CreatedAt` datetime(6) NOT NULL,
            `ExpiresAt` datetime(6) NOT NULL,
            `UsedAt` datetime(6) NULL,
            `UsedMachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
            `CreatedIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `UsedIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            PRIMARY KEY (`Id`),
            UNIQUE INDEX `IX_FreeLicenseActivationTokens_TokenHash` (`TokenHash`),
            INDEX `IX_FreeLicenseActivationTokens_ProductCode_ExpiresAt_UsedAt` (`ProductCode`,`ExpiresAt`,`UsedAt`)
        ) CHARACTER SET=utf8mb4;");
    }
    catch { }
}

{
    using var advertisementScope = app.Services.CreateScope();

    try
    {
        var db = advertisementScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `Advertisements` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `ProductId` int NULL,
            `ProductCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'NSXVERESIYEDEFTERI',
            `SlotCode` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'FREE_BOTTOM_728X90',
            `Title` varchar(180) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
            `AltText` varchar(300) CHARACTER SET utf8mb4 NULL,
            `ImagePath` varchar(700) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `TargetUrl` varchar(700) CHARACTER SET utf8mb4 NULL,
            `Width` int NOT NULL DEFAULT 728,
            `Height` int NOT NULL DEFAULT 90,
            `DisplaySeconds` int NOT NULL DEFAULT 10,
            `SortOrder` int NOT NULL DEFAULT 0,
            `Priority` int NOT NULL DEFAULT 0,
            `StartDate` datetime(6) NULL,
            `EndDate` datetime(6) NULL,
            `IsActive` tinyint(1) NOT NULL DEFAULT 1,
            `IsDeleted` tinyint(1) NOT NULL DEFAULT 0,
            `TrackClicks` tinyint(1) NOT NULL DEFAULT 1,
            `ImpressionCount` int NOT NULL DEFAULT 0,
            `ClickCount` int NOT NULL DEFAULT 0,
            `LastShownAt` datetime(6) NULL,
            `LastClickedAt` datetime(6) NULL,
            `CreatedAt` datetime(6) NOT NULL,
            `UpdatedAt` datetime(6) NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_Advertisements_ProductSlotActive` (`ProductCode`, `SlotCode`, `IsActive`),
            INDEX `IX_Advertisements_DateFilter` (`IsDeleted`, `StartDate`, `EndDate`),
            INDEX `IX_Advertisements_ProductId` (`ProductId`)
        ) CHARACTER SET=utf8mb4;");
    }
    catch
    {
    }
}

{
    using var productVideoScope = app.Services.CreateScope();

    try
    {
        var db = productVideoScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `ProductVideos` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `ProductId` int NOT NULL,
            `YouTubeUrl` varchar(700) CHARACTER SET utf8mb4 NOT NULL,
            `YouTubeVideoId` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
            `Title` varchar(220) CHARACTER SET utf8mb4 NULL,
            `VideoType` varchar(80) CHARACTER SET utf8mb4 NULL,
            `Slug` varchar(240) CHARACTER SET utf8mb4 NOT NULL,
            `SortOrder` int NOT NULL DEFAULT 0,
            `IsFeatured` tinyint(1) NOT NULL DEFAULT 0,
            `IsActive` tinyint(1) NOT NULL DEFAULT 1,
            `CreatedAt` datetime(6) NOT NULL,
            `UpdatedAt` datetime(6) NULL,
            PRIMARY KEY (`Id`),
            UNIQUE INDEX `IX_ProductVideos_Slug` (`Slug`),
            INDEX `IX_ProductVideos_ProductId` (`ProductId`),
            INDEX `IX_ProductVideos_IsActive_IsFeatured_SortOrder` (`IsActive`, `IsFeatured`, `SortOrder`),
            CONSTRAINT `FK_ProductVideos_Products_ProductId`
                FOREIGN KEY (`ProductId`) REFERENCES `Products` (`Id`) ON DELETE RESTRICT
        ) CHARACTER SET=utf8mb4;");
    }
    catch
    {
        // Video modülü tablosu oluşturulamazsa site açılışı etkilenmesin.
    }
}

{
    using var bankTransferScope = app.Services.CreateScope();

    try
    {
        var db = bankTransferScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `BankTransferNotifications` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `ProductId` int NOT NULL,
            `UserId` int NOT NULL,
            `OrderId` int NULL,
            `FullName` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `Email` varchar(180) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `Phone` varchar(50) CHARACTER SET utf8mb4 NULL,
            `LicenseType` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Yearly',
            `Quantity` int NOT NULL DEFAULT 1,
            `Amount` decimal(18,2) NOT NULL DEFAULT 0,
            `SenderName` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `SenderBank` varchar(120) CHARACTER SET utf8mb4 NULL,
            `ReceiptNumber` varchar(120) CHARACTER SET utf8mb4 NULL,
            `TransferDate` datetime(6) NOT NULL,
            `Note` varchar(1000) CHARACTER SET utf8mb4 NULL,
            `Status` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Pending',
            `AdminNote` varchar(1000) CHARACTER SET utf8mb4 NULL,
            `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `City` varchar(120) CHARACTER SET utf8mb4 NULL,
            `Region` varchar(120) CHARACTER SET utf8mb4 NULL,
            `Country` varchar(120) CHARACTER SET utf8mb4 NULL,
            `GeoLookupAt` datetime(6) NULL,
            `UserAgent` varchar(500) CHARACTER SET utf8mb4 NULL,
            `CreatedAt` datetime(6) NOT NULL,
            `ReviewedAt` datetime(6) NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_BankTransferNotifications_ProductId` (`ProductId`),
            INDEX `IX_BankTransferNotifications_UserId` (`UserId`),
            INDEX `IX_BankTransferNotifications_OrderId` (`OrderId`),
            INDEX `IX_BankTransferNotifications_StatusCreatedAt` (`Status`, `CreatedAt`)
        ) CHARACTER SET=utf8mb4;");
    }
    catch
    {
    }
}


{
    using var licenseV2Scope = app.Services.CreateScope();

    try
    {
        var db = licenseV2Scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var productAlterSql = new[]
        {
            "ALTER TABLE `Products` ADD COLUMN `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL;"
        };

        foreach (var sql in productAlterSql)
        {
            ExecuteMySqlAlterIfColumnMissing(db, sql);
        }

        try
        {
            db.Database.ExecuteSqlRaw(@"UPDATE `Products`
                SET `ProductCode` = UPPER(REPLACE(REPLACE(REPLACE(`Slug`, '-', ''), ' ', ''), '.', ''))
                WHERE (`ProductCode` IS NULL OR `ProductCode` = '') AND `Slug` IS NOT NULL AND `Slug` <> '';");
        }
        catch { }

        try
        {
            db.Database.ExecuteSqlRaw(@"UPDATE `Licenses` l
                INNER JOIN `Products` p ON p.`Id` = l.`ProductId`
                SET l.`ProductCode` = p.`ProductCode`
                WHERE (l.`ProductCode` IS NULL OR l.`ProductCode` = '') AND p.`ProductCode` IS NOT NULL AND p.`ProductCode` <> '';");
        }
        catch { }

        var licenseAlterSql = new[]
        {
            "ALTER TABLE `Licenses` ADD COLUMN `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `LicenseStatus` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Active';",
            "ALTER TABLE `Licenses` ADD COLUMN `MaxDeviceCount` int NOT NULL DEFAULT 1;",
            "ALTER TABLE `Licenses` ADD COLUMN `OfflineAllowed` tinyint(1) NOT NULL DEFAULT 1;",
            "ALTER TABLE `Licenses` ADD COLUMN `LastCheckedAt` datetime(6) NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `LastIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `LastCity` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `LastRegion` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `LastCountry` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `LastGeoLookupAt` datetime(6) NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `LastAppVersion` varchar(50) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `RevokedAt` datetime(6) NULL;",
            "ALTER TABLE `Licenses` ADD COLUMN `RevokedReason` varchar(500) CHARACTER SET utf8mb4 NULL;"
        };

        foreach (var sql in licenseAlterSql)
        {
            ExecuteMySqlAlterIfColumnMissing(db, sql);
        }

        try { db.Database.ExecuteSqlRaw("UPDATE `Licenses` SET `LicenseStatus` = 'Active' WHERE `LicenseStatus` IS NULL OR `LicenseStatus` = '';"); } catch { }
        try { db.Database.ExecuteSqlRaw("UPDATE `Licenses` SET `MaxDeviceCount` = 1 WHERE `MaxDeviceCount` IS NULL OR `MaxDeviceCount` <= 0;"); } catch { }
        try { db.Database.ExecuteSqlRaw("UPDATE `Licenses` SET `OfflineAllowed` = 1 WHERE `OfflineAllowed` IS NULL;"); } catch { }
        try
        {
            db.Database.ExecuteSqlRaw(@"UPDATE `Licenses` l
                INNER JOIN `Products` p ON p.`Id` = l.`ProductId`
                SET l.`ProductCode` = p.`ProductCode`
                WHERE (l.`ProductCode` IS NULL OR l.`ProductCode` = '') AND p.`ProductCode` IS NOT NULL AND p.`ProductCode` <> '';");
        }
        catch { }

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `LicenseDevices` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `LicenseId` int NOT NULL,
            `MachineId` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
            `DeviceName` varchar(120) CHARACTER SET utf8mb4 NULL,
            `OsVersion` varchar(120) CHARACTER SET utf8mb4 NULL,
            `AppVersion` varchar(50) CHARACTER SET utf8mb4 NULL,
            `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
            `AttemptEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
            `DeviceStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Active',
            `FirstIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `LastIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `LastCity` varchar(120) CHARACTER SET utf8mb4 NULL,
            `LastRegion` varchar(120) CHARACTER SET utf8mb4 NULL,
            `LastCountry` varchar(120) CHARACTER SET utf8mb4 NULL,
            `LastGeoLookupAt` datetime(6) NULL,
            `IsBlocked` tinyint(1) NOT NULL DEFAULT 0,
            `IsRejected` tinyint(1) NOT NULL DEFAULT 0,
            `BlockReason` varchar(500) CHARACTER SET utf8mb4 NULL,
            `FirstActivatedAt` datetime(6) NOT NULL,
            `LastSeenAt` datetime(6) NOT NULL,
            PRIMARY KEY (`Id`),
            UNIQUE INDEX `IX_LicenseDevices_LicenseId_MachineId` (`LicenseId`, `MachineId`),
            INDEX `IX_LicenseDevices_MachineId_IsBlocked` (`MachineId`, `IsBlocked`)
        ) CHARACTER SET=utf8mb4;");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `LicenseCheckLogs` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `LicenseId` int NULL,
            `LicenseKeyMasked` varchar(120) CHARACTER SET utf8mb4 NULL,
            `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
            `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
            `MachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
            `DeviceName` varchar(120) CHARACTER SET utf8mb4 NULL,
            `OsVersion` varchar(120) CHARACTER SET utf8mb4 NULL,
            `AppVersion` varchar(50) CHARACTER SET utf8mb4 NULL,
            `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `Success` tinyint(1) NOT NULL DEFAULT 0,
            `Status` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Unknown',
            `Message` varchar(700) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `CreatedAt` datetime(6) NOT NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_LicenseCheckLogs_LicenseId_CreatedAt` (`LicenseId`, `CreatedAt`),
            INDEX `IX_LicenseCheckLogs_ProductMachineCreated` (`ProductCode`, `MachineId`, `CreatedAt`)
        ) CHARACTER SET=utf8mb4;");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `LicenseSecurityLogs` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `LicenseId` int NULL,
            `LicenseKeyMasked` varchar(120) CHARACTER SET utf8mb4 NULL,
            `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
            `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
            `MachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
            `Severity` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Warning',
            `EventType` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Unknown',
            `Message` varchar(700) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
            `AppVersion` varchar(50) CHARACTER SET utf8mb4 NULL,
            `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `CreatedAt` datetime(6) NOT NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_LicenseSecurityLogs_LicenseId_CreatedAt` (`LicenseId`, `CreatedAt`),
            INDEX `IX_LicenseSecurityLogs_ProductMachineCreated` (`ProductCode`, `MachineId`, `CreatedAt`)
        ) CHARACTER SET=utf8mb4;");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `OfflineLicenseCertificates` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `LicenseId` int NOT NULL,
            `CertificateId` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
            `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
            `MachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
            `PayloadJson` longtext CHARACTER SET utf8mb4 NOT NULL,
            `Signature` longtext CHARACTER SET utf8mb4 NOT NULL,
            `OfflineCode` longtext CHARACTER SET utf8mb4 NOT NULL,
            `IssuedAt` datetime(6) NOT NULL,
            `ExpiresAt` datetime(6) NULL,
            `IsRevoked` tinyint(1) NOT NULL DEFAULT 0,
            `RevokedAt` datetime(6) NULL,
            `RevokedReason` varchar(500) CHARACTER SET utf8mb4 NULL,
            PRIMARY KEY (`Id`),
            UNIQUE INDEX `IX_OfflineLicenseCertificates_CertificateId` (`CertificateId`),
            INDEX `IX_OfflineLicenseCertificates_LicenseMachine` (`LicenseId`, `MachineId`)
        ) CHARACTER SET=utf8mb4;");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `BlockedDevices` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `MachineId` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
            `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
            `Reason` varchar(500) CHARACTER SET utf8mb4 NULL,
            `IsActive` tinyint(1) NOT NULL DEFAULT 1,
            `CreatedAt` datetime(6) NOT NULL,
            `DisabledAt` datetime(6) NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_BlockedDevices_MachineProductActive` (`MachineId`, `ProductCode`, `IsActive`)
        ) CHARACTER SET=utf8mb4;");

        var licenseV2HardeningSql = new[]
        {
            "ALTER TABLE `LicenseDevices` ADD COLUMN `AttemptEmail` varchar(180) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `LicenseDevices` ADD COLUMN `DeviceStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Active';",
            "ALTER TABLE `LicenseDevices` ADD COLUMN `IsRejected` tinyint(1) NOT NULL DEFAULT 0;",
            "ALTER TABLE `LicenseDevices` ADD COLUMN `LastCity` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `LicenseDevices` ADD COLUMN `LastRegion` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `LicenseDevices` ADD COLUMN `LastCountry` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `LicenseDevices` ADD COLUMN `LastGeoLookupAt` datetime(6) NULL;",
            "ALTER TABLE `LicenseCheckLogs` ADD COLUMN `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `LicenseSecurityLogs` ADD COLUMN `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL;"
        };

        foreach (var sql in licenseV2HardeningSql)
        {
            ExecuteMySqlAlterIfColumnMissing(db, sql);
        }

        try { db.Database.ExecuteSqlRaw("UPDATE `LicenseDevices` SET `DeviceStatus` = 'Active' WHERE `DeviceStatus` IS NULL OR `DeviceStatus` = '';"); } catch { }
    }
    catch
    {
    }
}


{
    using var visitorStatsScope = app.Services.CreateScope();
    try
    {
        var db = visitorStatsScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `SiteVisits` (
            `Id` bigint NOT NULL AUTO_INCREMENT,
            `VisitorId` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
            `Path` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '/',
            `PageTitle` varchar(250) CHARACTER SET utf8mb4 NULL,
            `Referrer` varchar(800) CHARACTER SET utf8mb4 NULL,
            `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `DeviceType` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Bilinmiyor',
            `Browser` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Bilinmiyor',
            `OperatingSystem` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Bilinmiyor',
            `TrafficType` varchar(24) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Human',
            `RobotName` varchar(120) CHARACTER SET utf8mb4 NULL,
            `Country` varchar(120) CHARACTER SET utf8mb4 NULL,
            `City` varchar(120) CHARACTER SET utf8mb4 NULL,
            `GeoLookupAtUtc` datetime(6) NULL,
            `GeoLookupAttemptCount` int NOT NULL DEFAULT 0,
            `IsAuthenticated` tinyint(1) NOT NULL DEFAULT 0,
            `UserId` int NULL,
            `VisitedAtUtc` datetime(6) NOT NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_SiteVisits_VisitedAtUtc` (`VisitedAtUtc`),
            INDEX `IX_SiteVisits_VisitorId_VisitedAtUtc` (`VisitorId`, `VisitedAtUtc`),
            INDEX `IX_SiteVisits_Path_VisitedAtUtc` (`Path`, `VisitedAtUtc`)
        ) CHARACTER SET=utf8mb4;");

        foreach (var sql in new[]
        {
            "ALTER TABLE `SiteVisits` ADD COLUMN `TrafficType` varchar(24) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Human';",
            "ALTER TABLE `SiteVisits` ADD COLUMN `RobotName` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `SiteVisits` ADD COLUMN `Country` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `SiteVisits` ADD COLUMN `City` varchar(120) CHARACTER SET utf8mb4 NULL;",
            "ALTER TABLE `SiteVisits` ADD COLUMN `GeoLookupAtUtc` datetime(6) NULL;",
            "ALTER TABLE `SiteVisits` ADD COLUMN `GeoLookupAttemptCount` int NOT NULL DEFAULT 0;"
        })
        {
            ExecuteMySqlAlterIfColumnMissing(db, sql);
        }

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `OnlineVisitorPresences` (
            `Id` bigint NOT NULL AUTO_INCREMENT,
            `VisitorId` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
            `FirstSeenAtUtc` datetime(6) NOT NULL,
            `LastSeenAtUtc` datetime(6) NOT NULL,
            `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
            `City` varchar(120) CHARACTER SET utf8mb4 NULL,
            `PagePath` varchar(500) CHARACTER SET utf8mb4 NOT NULL DEFAULT '/',
            `PageTitle` varchar(220) CHARACTER SET utf8mb4 NULL,
            `Referrer` varchar(500) CHARACTER SET utf8mb4 NULL,
            `Browser` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Bilinmiyor',
            `OperatingSystem` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Bilinmiyor',
            `DeviceType` varchar(80) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Bilinmiyor',
            `UserAgent` varchar(500) CHARACTER SET utf8mb4 NULL,
            `IsAuthenticated` tinyint(1) NOT NULL DEFAULT 0,
            `UserName` varchar(150) CHARACTER SET utf8mb4 NULL,
            `UserEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
            `UserRole` varchar(60) CHARACTER SET utf8mb4 NULL,
            PRIMARY KEY (`Id`),
            UNIQUE INDEX `IX_OnlineVisitorPresences_VisitorId` (`VisitorId`),
            INDEX `IX_OnlineVisitorPresences_LastSeenAtUtc` (`LastSeenAtUtc`)
        ) CHARACTER SET=utf8mb4;");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Ziyaretçi istatistik tablosu hazırlanamadı.");
    }
}

// Dealer sales channel uses isolated tables so existing order/license flows stay intact.
{
    using var dealerSchemaScope = app.Services.CreateScope();
    try
    {
        var db = dealerSchemaScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `Dealers` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `DealerCode` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
            `BusinessName` varchar(180) CHARACTER SET utf8mb4 NOT NULL,
            `ContactName` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
            `Email` varchar(180) CHARACTER SET utf8mb4 NOT NULL,
            `Phone` varchar(30) CHARACTER SET utf8mb4 NULL,
            `Province` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
            `District` varchar(120) CHARACTER SET utf8mb4 NULL,
            `Address` varchar(500) CHARACTER SET utf8mb4 NULL,
            `PasswordHash` longtext CHARACTER SET utf8mb4 NOT NULL,
            `CommissionMode` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Percent',
            `CommissionPercent` decimal(18,2) NOT NULL DEFAULT 0,
            `CommissionFixedAmount` decimal(18,2) NOT NULL DEFAULT 0,
            `IsActive` tinyint(1) NOT NULL DEFAULT 1,
            `CreatedAt` datetime(6) NOT NULL,
            `UpdatedAt` datetime(6) NULL,
            PRIMARY KEY (`Id`),
            UNIQUE INDEX `IX_Dealers_DealerCode` (`DealerCode`),
            UNIQUE INDEX `IX_Dealers_Email` (`Email`),
            INDEX `IX_Dealers_Province_IsActive` (`Province`, `IsActive`)
        ) CHARACTER SET=utf8mb4;");

        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS `DealerSales` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `DealerId` int NOT NULL,
            `CustomerUserId` int NOT NULL,
            `ProductId` int NOT NULL,
            `OrderId` int NOT NULL,
            `BankTransferNotificationId` int NULL,
            `LicenseType` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Yearly',
            `Quantity` int NOT NULL DEFAULT 1,
            `SaleAmount` decimal(18,2) NOT NULL DEFAULT 0,
            `CommissionModeSnapshot` varchar(20) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Percent',
            `CommissionPercentSnapshot` decimal(18,2) NOT NULL DEFAULT 0,
            `CommissionFixedAmountSnapshot` decimal(18,2) NOT NULL DEFAULT 0,
            `CommissionAmount` decimal(18,2) NOT NULL DEFAULT 0,
            `Status` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'WaitingBankTransfer',
            `CreatedAt` datetime(6) NOT NULL,
            `BankTransferSubmittedAt` datetime(6) NULL,
            PRIMARY KEY (`Id`),
            INDEX `IX_DealerSales_DealerId_CreatedAt` (`DealerId`, `CreatedAt`),
            INDEX `IX_DealerSales_CustomerUserId` (`CustomerUserId`),
            UNIQUE INDEX `IX_DealerSales_OrderId` (`OrderId`),
            UNIQUE INDEX `IX_DealerSales_BankTransferNotificationId` (`BankTransferNotificationId`)
        ) CHARACTER SET=utf8mb4;");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Dealer system tables could not be prepared.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/hata");
    app.UseHsts();
}

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/sayfa-bulunamadi"));

app.UseHttpsRedirection();
app.UseResponseCompression();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-NSX-Build"] = deploymentIdentity.BuildId;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Content-Security-Policy"] = string.Join(' ',
            "default-src 'self';",
            "base-uri 'self';",
            "object-src 'none';",
            "frame-ancestors 'self';",
            "form-action 'self';",
            "script-src 'self' 'unsafe-inline' https://www.clarity.ms https://scripts.clarity.ms https://www.googletagmanager.com;",
            "style-src 'self' 'unsafe-inline';",
            "img-src 'self' data: blob: https:;",
            "font-src 'self' data:;",
            "connect-src 'self' https://*.clarity.ms https://www.google-analytics.com https://www.googletagmanager.com wss:;",
            "frame-src 'self' https://www.youtube.com https://www.youtube-nocookie.com https://www.googletagmanager.com;",
            "worker-src 'self' blob:;");
        return Task.CompletedTask;
    });

    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var isRemoteSupportApi = (context.Request.Path.Value ?? string.Empty)
            .StartsWith("/api/uzaktan-yardim", StringComparison.OrdinalIgnoreCase);

        if (!isRemoteSupportApi
            && string.Equals(context.Request.Host.Host, "nsxyazilim.com", StringComparison.OrdinalIgnoreCase))
        {
            var targetUrl = $"https://www.nsxyazilim.com{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            context.Response.Redirect(targetUrl, permanent: true);
            return;
        }

        await next();
    });
}

var staticFileContentTypeProvider = new FileExtensionContentTypeProvider();
staticFileContentTypeProvider.Mappings[".webp"] = "image/webp";
staticFileContentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticFileContentTypeProvider,
    OnPrepareResponse = context =>
    {
        var request = context.Context.Request;
        var response = context.Context.Response;
        var path = request.Path.Value ?? string.Empty;
        var extension = Path.GetExtension(path);

        if (path.EndsWith("service-worker.js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/sw.js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Cache-Control"] = "no-cache,no-store,must-revalidate";
            return;
        }

        if (path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Cache-Control"] = "public,max-age=604800,stale-while-revalidate=86400";
            return;
        }

        if (path.StartsWith("/generated/product-thumbs/", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
            return;
        }

        var versioned = request.Query.ContainsKey("v");
        if (versioned && (extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
            return;
        }

        if (path.StartsWith("/nsx-assets/", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Cache-Control"] = "public,max-age=2592000,stale-while-revalidate=604800";
            return;
        }

        if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Cache-Control"] = "public,max-age=86400,stale-while-revalidate=604800";
        }
    }
});

// NSX Uzaktan Yardım canlı ekran / giriş / dosya relay bağlantısı için WebSocket desteği.
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

// /en, /fr, /de ... public URL'lerini routing'e girmeden önce gerçek uygulama
// yoluna indirger. PathBase dil önekini koruduğu için Razor URL üretimi de aynı
// dil alanında kalır. Varsayılan dilin /tr kopyası 308 ile kök URL'ye birleşir.
app.UseMiddleware<LocalizedRoutePrefixMiddleware>();

app.UseRouting();
app.UseRateLimiter();

app.UseSession();

app.UseAuthentication();
app.UseAuthorization();
// Kullanıcı yabancı bir dili seçmişken eski/kök bir public linke gelirse dili
// URL'ye taşır. Arama motoru cookie'siz kök URL'de varsayılan Türkçeyi görür.
app.UseMiddleware<LocalizedSeoRedirectMiddleware>();
app.UseMiddleware<SiteLocalizationHtmlMiddleware>();
app.UseMiddleware<SiteVisitTrackingMiddleware>();

// Public but non-sensitive deployment probe. /api oldugu icin localization ve ziyaretci
// istatistigi middleware'leri bu istegi islemez; kontrol ziyaretci sayisini sisirmez.
app.MapGet("/api/nsx-deploy", (HttpContext context) =>
{
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";

    return Results.Json(new
    {
        status = "ok",
        buildId = deploymentIdentity.BuildId,
        markerBuildId = deploymentIdentity.MarkerBuildId,
        publishedAtUtc = deploymentIdentity.PublishedAtUtc,
        assemblySha256 = deploymentIdentity.AssemblySha256,
        markerAssemblySha256 = deploymentIdentity.MarkerAssemblySha256,
        verified = deploymentIdentity.IsVerified,
        marker = deploymentIdentity.HasPublishMarker ? "publish" : "runtime-fallback"
    });
}).ExcludeFromDescription();

// NSX Uzaktan Yardım - public istemci/signaling API'si.
// Ana domain altında çalışır: /api/uzaktan-yardim/...
var nsxRemoteApi = app.MapGroup("/api/uzaktan-yardim");
var nsxRemoteOperators = nsxRemoteApi.MapGroup("/v1/operator")
    .RequireAuthorization(RemoteSupportAuthenticationHandler.PolicyName);
nsxRemoteOperators.AddEndpointFilter(async (invocation, next) => {
    var user = invocation.HttpContext.User;
    var operatorId = RemoteSupportAuthorization.OperatorId(user);
    if (string.IsNullOrWhiteSpace(operatorId)) return Results.Forbid();
    invocation.HttpContext.Items["NsxOperatorId"] = operatorId;
    var rawId = invocation.HttpContext.Request.RouteValues["requestId"]?.ToString();
    if (Guid.TryParse(rawId, out var requestId) && nsxRemoteIncomingRequests.TryGetValue(requestId, out var request)
        && !RemoteSupportAuthorization.OwnsRequest(request.OperatorId,user)) return Results.Forbid();
    // Cookie operators must use a same-origin request for state-changing calls.
    if (HttpMethods.IsPost(invocation.HttpContext.Request.Method) &&
        !user.Identities.Any(identity => identity.AuthenticationType == RemoteSupportAuthenticationHandler.SchemeName)) {
        if (!RemoteSupportAuthorization.IsSameOrigin(invocation.HttpContext.Request))
            return Results.StatusCode(403);
    }
    return await next(invocation);
});

nsxRemoteApi.MapGet("/health", () =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    return Results.Ok(new
    {
        service = "NSX Uzaktan Yardım Signaling + Remote Relay",
        version = "0.4.3-web",
        utc = DateTimeOffset.UtcNow,
        onlineClients = nsxRemoteClients.Values.Count(x => NsxRemoteIsRecent(x.LastSeenUtc)),
        remoteSessions = nsxRemoteSessions.Count
    });
});

nsxRemoteApi.MapPost("/v1/clients/register", (NsxRemoteClientRegistrationRequest request) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    var deviceId = NsxRemoteNormalizeDeviceId(request.DeviceId);
    if (deviceId.Length != 9 || string.IsNullOrWhiteSpace(request.SessionToken))
        return Results.BadRequest(new { message = "Geçersiz cihaz kimliği veya oturum anahtarı." });

    var state = new NsxRemoteClientState
    {
        DeviceId = deviceId,
        SessionToken = request.SessionToken.Trim(),
        QuickCode = request.QuickCode?.Trim() ?? string.Empty,
        ClientKind = request.ClientKind?.Trim() ?? string.Empty,
        DisplayName = request.DisplayName?.Trim() ?? "NSX Müşterisi",
        ProductCode = request.ProductCode,
        ProductName = request.ProductName,
        MachineName = request.MachineName?.Trim() ?? string.Empty,
        OsVersion = request.OsVersion?.Trim() ?? string.Empty,
        AppVersion = request.AppVersion?.Trim() ?? string.Empty,
        PermissionGranted = request.PermissionGranted,
        SupportRequested = false,
        LastSeenUtc = DateTimeOffset.UtcNow
    };

    nsxRemoteClients.AddOrUpdate(deviceId, state, (_, existing) =>
    {
        existing.SessionToken = state.SessionToken;
        existing.QuickCode = state.QuickCode;
        existing.ClientKind = state.ClientKind;
        existing.DisplayName = state.DisplayName;
        existing.ProductCode = state.ProductCode;
        existing.ProductName = state.ProductName;
        existing.MachineName = state.MachineName;
        existing.OsVersion = state.OsVersion;
        existing.AppVersion = state.AppVersion;
        existing.PermissionGranted = state.PermissionGranted;
        existing.LastSeenUtc = state.LastSeenUtc;
        return existing;
    });

    return Results.Ok(new { status = "registered", deviceId });
});

nsxRemoteApi.MapPost("/v1/clients/heartbeat", (NsxRemoteClientHeartbeatRequest request) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    var deviceId = NsxRemoteNormalizeDeviceId(request.DeviceId);
    if (!nsxRemoteClients.TryGetValue(deviceId, out var state)
        || !NsxRemoteTokenEquals(state.SessionToken, request.SessionToken))
        return Results.Unauthorized();

    state.PermissionGranted = request.PermissionGranted;
    var hasOpenSupportRequest = nsxRemoteSupportRequests.Values.Any(x =>
        x.DeviceId == deviceId && (x.Status == "waiting" || x.Status == "operator-contacted"));
    state.SupportRequested = request.SupportRequested && hasOpenSupportRequest;
    state.LastSeenUtc = DateTimeOffset.UtcNow;
    return Results.Ok(new { status = "ok" });
});

nsxRemoteApi.MapPost("/v1/clients/permission", (NsxRemotePermissionUpdateRequest request) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    var deviceId = NsxRemoteNormalizeDeviceId(request.DeviceId);
    if (!nsxRemoteClients.TryGetValue(deviceId, out var state)
        || !NsxRemoteTokenEquals(state.SessionToken, request.SessionToken))
        return Results.Unauthorized();

    state.PermissionGranted = request.PermissionGranted;
    state.LastSeenUtc = DateTimeOffset.UtcNow;
    return Results.Ok(new { status = request.PermissionGranted ? "granted" : "closed" });
});

nsxRemoteApi.MapPost("/v1/clients/unregister", (NsxRemoteClientDisconnectRequest request) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    var deviceId = NsxRemoteNormalizeDeviceId(request.DeviceId);
    if (!nsxRemoteClients.TryGetValue(deviceId, out var client)
        || !NsxRemoteTokenEquals(client.SessionToken, request.SessionToken))
        return Results.Unauthorized();

    var now = DateTimeOffset.UtcNow;
    foreach (var incoming in nsxRemoteIncomingRequests.Values.Where(x => x.DeviceId == deviceId && x.Status == "pending"))
    {
        incoming.Status = "client-offline";
        incoming.UpdatedUtc = now;
    }

    NsxRemoteFinalizeSupportRequestsForDevice(nsxRemoteSupportRequests, deviceId, "client-offline");
    client.PermissionGranted = false;
    client.SupportRequested = false;
    nsxRemoteClients.TryRemove(deviceId, out _);
    return Results.Ok(new { status = "offline" });
});

nsxRemoteApi.MapPost("/v1/support/requests", (NsxRemoteSupportRequestCreateRequest request) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    var deviceId = NsxRemoteNormalizeDeviceId(request.DeviceId);
    if (deviceId.Length != 9 || string.IsNullOrWhiteSpace(request.SessionToken))
        return Results.BadRequest(new { message = "Geçersiz destek talebi." });

    var client = nsxRemoteClients.GetOrAdd(deviceId, _ => new NsxRemoteClientState
    {
        DeviceId = deviceId,
        SessionToken = request.SessionToken.Trim(),
        QuickCode = request.QuickCode?.Trim() ?? string.Empty,
        ClientKind = request.ClientKind?.Trim() ?? string.Empty,
        DisplayName = request.DisplayName?.Trim() ?? "NSX Müşterisi",
        ProductCode = request.ProductCode,
        ProductName = request.ProductName,
        MachineName = request.MachineName?.Trim() ?? string.Empty,
        OsVersion = request.OsVersion?.Trim() ?? string.Empty,
        AppVersion = string.Empty,
        PermissionGranted = false,
        SupportRequested = true,
        LastSeenUtc = DateTimeOffset.UtcNow
    });

    if (!NsxRemoteTokenEquals(client.SessionToken, request.SessionToken))
        return Results.Unauthorized();

    client.QuickCode = request.QuickCode?.Trim() ?? string.Empty;
    client.ClientKind = request.ClientKind?.Trim() ?? string.Empty;
    client.DisplayName = request.DisplayName?.Trim() ?? "NSX Müşterisi";
    client.ProductCode = request.ProductCode;
    client.ProductName = request.ProductName;
    client.MachineName = request.MachineName?.Trim() ?? string.Empty;
    client.OsVersion = request.OsVersion?.Trim() ?? string.Empty;
    client.SupportRequested = true;
    client.LastSeenUtc = DateTimeOffset.UtcNow;

    var openExisting = nsxRemoteSupportRequests.Values
        .Where(x => x.DeviceId == deviceId && x.Status == "waiting")
        .OrderByDescending(x => x.CreatedUtc)
        .FirstOrDefault();

    if (openExisting is not null)
        return Results.Ok(new NsxRemoteSupportRequestCreateResponse(openExisting.RequestId, openExisting.Status, openExisting.CreatedUtc));

    var created = new NsxRemoteSupportRequestState
    {
        RequestId = Guid.NewGuid(),
        DeviceId = deviceId,
        QuickCode = request.QuickCode?.Trim() ?? string.Empty,
        ClientKind = request.ClientKind?.Trim() ?? string.Empty,
        DisplayName = request.DisplayName?.Trim() ?? "NSX Müşterisi",
        ProductCode = request.ProductCode,
        ProductName = request.ProductName,
        MachineName = request.MachineName?.Trim() ?? string.Empty,
        OsVersion = request.OsVersion?.Trim() ?? string.Empty,
        Status = "waiting",
        CreatedUtc = DateTimeOffset.UtcNow
    };
    nsxRemoteSupportRequests[created.RequestId] = created;

    return Results.Ok(new NsxRemoteSupportRequestCreateResponse(created.RequestId, created.Status, created.CreatedUtc));
});

nsxRemoteApi.MapGet("/v1/clients/{deviceId}/incoming", (string deviceId, string sessionToken) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    deviceId = NsxRemoteNormalizeDeviceId(deviceId);
    if (!nsxRemoteClients.TryGetValue(deviceId, out var client)
        || !NsxRemoteTokenEquals(client.SessionToken, sessionToken))
        return Results.Unauthorized();

    client.LastSeenUtc = DateTimeOffset.UtcNow;
    var pending = nsxRemoteIncomingRequests.Values
        .Where(x => x.DeviceId == deviceId && x.Status == "pending")
        .OrderByDescending(x => x.CreatedUtc)
        .FirstOrDefault();

    if (pending is null)
        return Results.NoContent();

    return Results.Ok(new NsxRemoteIncomingConnectionRequest(
        pending.RequestId,
        pending.DeviceId,
        pending.OperatorName,
        pending.OperatorNote,
        pending.CreatedUtc,
        pending.ExpiresUtc,
        pending.Status));
});

nsxRemoteApi.MapPost("/v1/clients/{deviceId}/incoming/{requestId:guid}/decision",
    (string deviceId, Guid requestId, NsxRemoteIncomingConnectionDecisionRequest request) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    deviceId = NsxRemoteNormalizeDeviceId(deviceId);
    if (NsxRemoteNormalizeDeviceId(request.DeviceId) != deviceId)
        return Results.BadRequest(new { message = "Cihaz kimliği uyuşmuyor." });

    if (!nsxRemoteClients.TryGetValue(deviceId, out var client)
        || !NsxRemoteTokenEquals(client.SessionToken, request.SessionToken))
        return Results.Unauthorized();

    if (!nsxRemoteIncomingRequests.TryGetValue(requestId, out var incoming) || incoming.DeviceId != deviceId)
        return Results.NotFound(new { message = "Bağlantı isteği bulunamadı." });

    if (!incoming.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { message = $"Bağlantı isteği artık aktif değil: {incoming.Status}." });

    if (incoming.ExpiresUtc <= DateTimeOffset.UtcNow)
    {
        incoming.Status = "expired";
        incoming.UpdatedUtc = DateTimeOffset.UtcNow;
        client.SupportRequested = false;
        NsxRemoteFinalizeSupportRequestsForDevice(nsxRemoteSupportRequests, deviceId, "expired");
        return Results.Conflict(new { message = "Bağlantı isteğinin 60 saniyelik onay süresi doldu." });
    }

    var decision = request.Decision.Equals("accept", StringComparison.OrdinalIgnoreCase) ? "accepted" : "rejected";
    incoming.Status = decision;
    incoming.UpdatedUtc = DateTimeOffset.UtcNow;
    client.PermissionGranted = decision == "accepted";
    client.SupportRequested = decision == "accepted";
    client.LastSeenUtc = DateTimeOffset.UtcNow;
    NsxRemoteFinalizeSupportRequestsForDevice(nsxRemoteSupportRequests, deviceId, decision);

    NsxRemoteSessionInfo? sessionInfo = null;
    if (decision == "accepted")
    {
        var session = nsxRemoteSessions.Values.FirstOrDefault(x => x.RequestId == requestId && x.ExpiresUtc > DateTimeOffset.UtcNow);
        if (session is null)
        {
            session = new NsxRemoteSessionState
            {
                SessionId = Guid.NewGuid(),
                RequestId = requestId,
                DeviceId = deviceId,
                ClientAccessKey = NsxRemoteNewAccessKey(),
                OperatorAccessKey = NsxRemoteNewAccessKey(),
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(4)
            };
            nsxRemoteSessions[session.SessionId] = session;
        }

        sessionInfo = session.ToInfo("client");
    }

    return Results.Ok(new NsxRemoteIncomingConnectionDecisionResponse(requestId, decision, incoming.UpdatedUtc, sessionInfo));
});

nsxRemoteOperators.MapGet("/clients", () =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);

    var items = nsxRemoteClients.Values
        .Where(x => NsxRemoteIsRecent(x.LastSeenUtc))
        .Where(x => x.ClientKind.Equals("nsx-customer", StringComparison.OrdinalIgnoreCase) || x.SupportRequested)
        .OrderByDescending(x => x.SupportRequested)
        .ThenByDescending(x => x.LastSeenUtc)
        .Select(x => new NsxRemoteOperatorClientView(
            x.DeviceId,
            x.ClientKind,
            x.DisplayName,
            x.ProductCode,
            x.ProductName,
            x.MachineName,
            x.OsVersion,
            x.AppVersion,
            x.PermissionGranted,
            x.SupportRequested,
            x.LastSeenUtc))
        .ToArray();

    return Results.Ok(items);
});

nsxRemoteOperators.MapGet("/resolve/{key}", (string key) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    var normalized = (key ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", string.Empty);
    var digits = new string(normalized.Where(char.IsDigit).ToArray());

    var client = nsxRemoteClients.Values
        .Where(x => NsxRemoteIsRecent(x.LastSeenUtc))
        .FirstOrDefault(x =>
            (digits.Length == 9 && x.DeviceId == digits) ||
            x.QuickCode.Replace(" ", string.Empty).Equals(normalized, StringComparison.OrdinalIgnoreCase));

    if (client is null)
        return Results.NotFound(new { message = "Çevrimiçi cihaz bulunamadı." });

    return Results.Ok(new NsxRemoteOperatorClientView(
        client.DeviceId,
        client.ClientKind,
        client.DisplayName,
        client.ProductCode,
        client.ProductName,
        client.MachineName,
        client.OsVersion,
        client.AppVersion,
        client.PermissionGranted,
        client.SupportRequested,
        client.LastSeenUtc));
});

nsxRemoteOperators.MapGet("/requests", () =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    var items = nsxRemoteSupportRequests.Values
        .Where(x => x.Status == "waiting")
        .Where(x => nsxRemoteClients.TryGetValue(x.DeviceId, out var client) && NsxRemoteIsRecent(client.LastSeenUtc) && client.SupportRequested)
        .OrderBy(x => x.CreatedUtc)
        .Select(x => new NsxRemoteOperatorSupportRequestView(
            x.RequestId,
            x.DeviceId,
            x.QuickCode,
            x.ClientKind,
            x.DisplayName,
            x.ProductCode,
            x.ProductName,
            x.MachineName,
            x.OsVersion,
            x.Status,
            x.CreatedUtc))
        .ToArray();

    return Results.Ok(items);
});

nsxRemoteOperators.MapPost("/connect/{deviceId}", (HttpContext context, string deviceId, NsxRemoteOperatorConnectRequest request) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    deviceId = NsxRemoteNormalizeDeviceId(deviceId);
    if (!nsxRemoteClients.TryGetValue(deviceId, out var client) || !NsxRemoteIsRecent(client.LastSeenUtc))
        return Results.NotFound(new { message = "Cihaz çevrimiçi değil." });

    var existing = nsxRemoteIncomingRequests.Values
        .FirstOrDefault(x => x.DeviceId == deviceId && x.Status == "pending");
    if (existing is not null && existing.OperatorId != context.Items["NsxOperatorId"]?.ToString())
        return Results.StatusCode(403);
    if (existing is not null)
        return Results.Ok(new NsxRemoteIncomingConnectionRequest(existing.RequestId, existing.DeviceId, existing.OperatorName, existing.OperatorNote, existing.CreatedUtc, existing.ExpiresUtc, existing.Status));

    var incoming = new NsxRemoteIncomingRequestState
    {
        RequestId = Guid.NewGuid(),
        DeviceId = deviceId,
        OperatorId = context.Items["NsxOperatorId"]!.ToString()!,
        OperatorName = context.User.Identity?.Name ?? "NSX Destek",
        OperatorNote = request.OperatorNote,
        CreatedUtc = DateTimeOffset.UtcNow,
        ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(60),
        UpdatedUtc = DateTimeOffset.UtcNow,
        Status = "pending"
    };
    nsxRemoteIncomingRequests[incoming.RequestId] = incoming;

    var waitingSupport = nsxRemoteSupportRequests.Values
        .Where(x => x.DeviceId == deviceId && x.Status == "waiting")
        .OrderBy(x => x.CreatedUtc)
        .FirstOrDefault();
    if (waitingSupport is not null)
        waitingSupport.Status = "operator-contacted";

    return Results.Ok(new NsxRemoteIncomingConnectionRequest(incoming.RequestId, incoming.DeviceId, incoming.OperatorName, incoming.OperatorNote, incoming.CreatedUtc, incoming.ExpiresUtc, incoming.Status));
});

nsxRemoteOperators.MapPost("/incoming/{requestId:guid}/cancel", (Guid requestId) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    if (!nsxRemoteIncomingRequests.TryGetValue(requestId, out var incoming))
        return Results.NotFound(new { message = "Bağlantı isteği bulunamadı." });

    if (incoming.Status == "pending")
    {
        incoming.Status = "cancelled";
        incoming.UpdatedUtc = DateTimeOffset.UtcNow;
        if (nsxRemoteClients.TryGetValue(incoming.DeviceId, out var client))
            client.SupportRequested = false;
        NsxRemoteFinalizeSupportRequestsForDevice(nsxRemoteSupportRequests, incoming.DeviceId, "cancelled");
    }

    return Results.Ok(new { status = incoming.Status });
});

nsxRemoteOperators.MapGet("/incoming/{requestId:guid}", (Guid requestId) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    if (!nsxRemoteIncomingRequests.TryGetValue(requestId, out var incoming))
        return Results.NotFound(new { message = "Bağlantı isteği bulunamadı." });

    return Results.Ok(new NsxRemoteIncomingConnectionRequest(
        incoming.RequestId,
        incoming.DeviceId,
        incoming.OperatorName,
        incoming.OperatorNote,
        incoming.CreatedUtc,
        incoming.ExpiresUtc,
        incoming.Status));
});

nsxRemoteOperators.MapGet("/incoming/{requestId:guid}/session", (Guid requestId) =>
{
    NsxRemotePruneExpired(nsxRemoteClients, nsxRemoteSupportRequests, nsxRemoteIncomingRequests, nsxRemoteSessions);
    if (!nsxRemoteIncomingRequests.TryGetValue(requestId, out var incoming))
        return Results.NotFound(new { message = "Bağlantı isteği bulunamadı." });

    if (!incoming.Status.Equals("accepted", StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { message = "Müşteri henüz bağlantıyı onaylamadı." });

    var session = nsxRemoteSessions.Values.FirstOrDefault(x => x.RequestId == requestId && x.ExpiresUtc > DateTimeOffset.UtcNow);
    if (session is null)
        return Results.NotFound(new { message = "Uzak masaüstü oturumu bulunamadı veya süresi doldu." });

    return Results.Ok(session.ToInfo("operator"));
});

nsxRemoteApi.MapGet("/ws/remote/{sessionId:guid}", async (HttpContext context, Guid sessionId) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { message = "WebSocket bağlantısı gerekli." });
        return;
    }

    if (!nsxRemoteSessions.TryGetValue(sessionId, out var session) || session.ExpiresUtc <= DateTimeOffset.UtcNow)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var role = context.Request.Query["role"].ToString().Trim().ToLowerInvariant();
    var accessKey = context.Request.Query["accessKey"].ToString();
    if (!session.IsAuthorized(role, accessKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    session.Attach(role, socket);

    await NsxRemoteRelayWebSocketAsync(session, role, socket, context.RequestAborted);
    nsxRemoteSessions.TryRemove(sessionId, out _);
});


app.MapHub<LiveChatHub>("/liveChatHub");

// NSX_NATIVE_ROTATE_HARD_ROUTE_BEGIN
// Native mobile refresh token rotation is intentionally mapped at Program level
// on a unique path to bypass the legacy GET-only route collision on Plesk/IIS.
app.MapPost("/api/nsx-native-token/rotate", async Task<IResult> (
    NSYazilim.Web.CaritakipCloud.Models.CariNativeRefreshRequest? request,
    HttpContext http,
    CaritakipNativeMobileStore nativeStore,
    CancellationToken cancellationToken) =>
{
    http.Response.Headers.CacheControl = "no-store";

    try
    {
        var refreshed = await nativeStore.RefreshAsync(
            request?.RefreshToken ?? string.Empty,
            cancellationToken);

        if (refreshed.Result is null)
        {
            var error = refreshed.Error ?? "REFRESH_REVOKED";
            var message = error switch
            {
                "REFRESH_EXPIRED" => "Mobil oturum yenileme anahtarÄ±nÄ±n sÃ¼resi dolmuÅŸ.",
                "DEVICE_UNAUTHORIZED" => "Bu mobil cihazÄ±n eriÅŸim yetkisi kaldÄ±rÄ±lmÄ±ÅŸ.",
                _ => "Mobil oturum yenileme anahtarÄ± geÃ§ersiz veya iptal edilmiÅŸ."
            };

            return Results.Json(
                new { error, message },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new
        {
            accessToken = refreshed.Result.AccessToken,
            refreshToken = refreshed.Result.RefreshToken,
            accessTokenExpiresAt = refreshed.Result.AccessTokenExpiresAtUtc,
            refreshTokenExpiresAt = refreshed.Result.RefreshTokenExpiresAtUtc
        });
    }
    catch (CariEntitlementUnavailableException ex)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Json(
            new { error = "SERVICE_UNAVAILABLE", message = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.RequireRateLimiting("CaritakipCloud");
// NSX_NATIVE_ROTATE_HARD_ROUTE_END
app.MapNsxTeknikServisCloud();
app.MapNsxSalonTakipCloud();
app.MapNsxVeresiyeFreeCloud();
app.MapNsxCaritakipCloud();

// Attribute route kullanan SEO endpointleri:
// /robots.txt, /sitemap.xml, /blog, /blog/{slug}, /ucretsiz-veresiye-programi vb.
app.MapControllers();

app.MapControllerRoute(
    name: "product-slug",
    pattern: "urun/{slug}",
    defaults: new { controller = "Store", action = "DetailBySlug" });

app.MapControllerRoute(
    name: "admin",
    pattern: "Admin/{action=Index}/{id?}",
    defaults: new { controller = "Admin" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();


static string NsxRemoteNormalizeDeviceId(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
static bool NsxRemoteIsRecent(DateTimeOffset lastSeenUtc) => DateTimeOffset.UtcNow - lastSeenUtc <= TimeSpan.FromSeconds(75);
static string NsxRemoteNewAccessKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

static bool NsxRemoteTokenEquals(string expected, string? actual)
{
    if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual) || expected.Length != actual.Length)
        return false;

    return CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(expected),
        System.Text.Encoding.UTF8.GetBytes(actual));
}

static void NsxRemoteFinalizeSupportRequestsForDevice(
    ConcurrentDictionary<Guid, NsxRemoteSupportRequestState> supportRequests,
    string deviceId,
    string status)
{
    foreach (var support in supportRequests.Values.Where(x =>
                 x.DeviceId == deviceId &&
                 (x.Status == "waiting" || x.Status == "operator-contacted")))
    {
        support.Status = status;
    }
}

static void NsxRemotePruneExpired(
    ConcurrentDictionary<string, NsxRemoteClientState> clients,
    ConcurrentDictionary<Guid, NsxRemoteSupportRequestState> supportRequests,
    ConcurrentDictionary<Guid, NsxRemoteIncomingRequestState> incomingRequests,
    ConcurrentDictionary<Guid, NsxRemoteSessionState> sessions)
{
    var now = DateTimeOffset.UtcNow;
    var terminalRetention = TimeSpan.FromMinutes(10);

    foreach (var pair in clients)
    {
        if (now - pair.Value.LastSeenUtc > TimeSpan.FromHours(2))
            clients.TryRemove(pair.Key, out _);
    }

    foreach (var pair in sessions)
    {
        if (pair.Value.ExpiresUtc <= now)
            sessions.TryRemove(pair.Key, out _);
    }

    foreach (var pair in incomingRequests.ToArray())
    {
        var incoming = pair.Value;
        if (incoming.Status == "pending")
        {
            var clientOnline = clients.TryGetValue(incoming.DeviceId, out var client) && NsxRemoteIsRecent(client.LastSeenUtc);
            if (!clientOnline)
            {
                incoming.Status = "client-offline";
                incoming.UpdatedUtc = now;
                if (client is not null)
                    client.SupportRequested = false;
                NsxRemoteFinalizeSupportRequestsForDevice(supportRequests, incoming.DeviceId, "client-offline");
            }
            else if (incoming.ExpiresUtc <= now)
            {
                incoming.Status = "expired";
                incoming.UpdatedUtc = now;
                client!.SupportRequested = false;
                NsxRemoteFinalizeSupportRequestsForDevice(supportRequests, incoming.DeviceId, "expired");
            }
        }

        if (incoming.Status != "pending" && now - incoming.UpdatedUtc > terminalRetention)
            incomingRequests.TryRemove(pair.Key, out _);
    }

    foreach (var pair in supportRequests.ToArray())
    {
        var support = pair.Value;
        if (support.Status == "waiting" || support.Status == "operator-contacted")
        {
            if (!clients.TryGetValue(support.DeviceId, out var client) || !NsxRemoteIsRecent(client.LastSeenUtc))
                support.Status = "client-offline";
            else if (!client.SupportRequested && support.Status == "waiting")
                support.Status = "cancelled";
        }

        if (support.Status != "waiting" && support.Status != "operator-contacted" && now - support.CreatedUtc > terminalRetention)
            supportRequests.TryRemove(pair.Key, out _);
    }
}

static async Task NsxRemoteRelayWebSocketAsync(
    NsxRemoteSessionState session,
    string role,
    WebSocket socket,
    CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];

    try
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var peer = session.GetPeer(role);
            if (peer is null || peer.State != WebSocketState.Open)
                continue;

            await session.SendToPeerAsync(
                peer,
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (WebSocketException)
    {
    }
    finally
    {
        session.Detach(role, socket);
        var peer = session.GetPeer(role);
        if (peer is { State: WebSocketState.Open })
        {
            try
            {
                await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "NSX uzak destek oturumu sona erdi.", CancellationToken.None);
            }
            catch
            {
            }
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "NSX uzak destek oturumu sona erdi.", CancellationToken.None);
            }
            catch
            {
            }
        }
    }
}

static bool IsMissingOrPlaceholderConnectionString(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return true;
    }

    return connectionString.Contains("NSX_DB_USER", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("NSX_DB_PASSWORD", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
}

static void ExecuteMySqlAlterIfColumnMissing(ApplicationDbContext db, string alterSql)
{
    var identifiers = Regex.Matches(alterSql, "`(?<name>[^`]+)`")
        .Select(match => match.Groups["name"].Value)
        .ToArray();

    if (identifiers.Length < 2)
        throw new InvalidOperationException("ALTER TABLE komutundan tablo ve kolon adı okunamadı.");

    var tableName = identifiers[0];
    var columnName = identifiers[1];
    var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
        connection.Open();

    using var columnCheck = connection.CreateCommand();
    columnCheck.CommandText = @"SELECT COUNT(*)
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE()
          AND TABLE_NAME = @tableName
          AND COLUMN_NAME = @columnName;";

    var tableParameter = columnCheck.CreateParameter();
    tableParameter.ParameterName = "@tableName";
    tableParameter.Value = tableName;
    columnCheck.Parameters.Add(tableParameter);

    var columnParameter = columnCheck.CreateParameter();
    columnParameter.ParameterName = "@columnName";
    columnParameter.Value = columnName;
    columnCheck.Parameters.Add(columnParameter);

    if (Convert.ToInt32(columnCheck.ExecuteScalar()) == 0)
        db.Database.ExecuteSqlRaw(alterSql);
}

sealed record NsxRemoteClientRegistrationRequest(
    string DeviceId,
    string SessionToken,
    string? QuickCode,
    string? ClientKind,
    string? DisplayName,
    string? ProductCode,
    string? ProductName,
    string? MachineName,
    string? OsVersion,
    string? AppVersion,
    bool PermissionGranted,
    DateTimeOffset TimestampUtc);

sealed record NsxRemoteClientHeartbeatRequest(
    string DeviceId,
    string SessionToken,
    bool PermissionGranted,
    bool SupportRequested,
    DateTimeOffset TimestampUtc);

sealed record NsxRemoteSupportRequestCreateRequest(
    string DeviceId,
    string SessionToken,
    string? QuickCode,
    string? ClientKind,
    string? DisplayName,
    string? ProductCode,
    string? ProductName,
    string? MachineName,
    string? OsVersion,
    DateTimeOffset TimestampUtc);

sealed record NsxRemoteSupportRequestCreateResponse(Guid RequestId, string Status, DateTimeOffset CreatedUtc);
sealed record NsxRemotePermissionUpdateRequest(string DeviceId, string SessionToken, bool PermissionGranted, DateTimeOffset TimestampUtc);
sealed record NsxRemoteClientDisconnectRequest(string DeviceId, string SessionToken, DateTimeOffset TimestampUtc);
sealed record NsxRemoteIncomingConnectionRequest(Guid RequestId, string DeviceId, string OperatorName, string? OperatorNote, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, string Status);
sealed record NsxRemoteIncomingConnectionDecisionRequest(string DeviceId, string SessionToken, string Decision, DateTimeOffset TimestampUtc);
sealed record NsxRemoteOperatorConnectRequest(string OperatorName, string? OperatorNote, DateTimeOffset TimestampUtc);
sealed record NsxRemoteOperatorClientView(string DeviceId, string ClientKind, string DisplayName, string? ProductCode, string? ProductName, string MachineName, string OsVersion, string AppVersion, bool PermissionGranted, bool SupportRequested, DateTimeOffset LastSeenUtc);
sealed record NsxRemoteOperatorSupportRequestView(Guid RequestId, string DeviceId, string QuickCode, string ClientKind, string DisplayName, string? ProductCode, string? ProductName, string MachineName, string OsVersion, string Status, DateTimeOffset CreatedUtc);
sealed record NsxRemoteSessionInfo(Guid SessionId, Guid RequestId, string DeviceId, string AccessKey, string Role, DateTimeOffset ExpiresUtc);
sealed record NsxRemoteIncomingConnectionDecisionResponse(Guid RequestId, string Status, DateTimeOffset UpdatedUtc, NsxRemoteSessionInfo? Session = null);

sealed class NsxRemoteClientState
{
    public required string DeviceId { get; init; }
    public required string SessionToken { get; set; }
    public required string QuickCode { get; set; }
    public required string ClientKind { get; set; }
    public required string DisplayName { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public required string MachineName { get; set; }
    public required string OsVersion { get; set; }
    public required string AppVersion { get; set; }
    public bool PermissionGranted { get; set; }
    public bool SupportRequested { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

sealed class NsxRemoteSupportRequestState
{
    public Guid RequestId { get; init; }
    public required string DeviceId { get; init; }
    public required string QuickCode { get; init; }
    public required string ClientKind { get; init; }
    public required string DisplayName { get; init; }
    public string? ProductCode { get; init; }
    public string? ProductName { get; init; }
    public required string MachineName { get; init; }
    public required string OsVersion { get; init; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
}

sealed class NsxRemoteIncomingRequestState
{
    public Guid RequestId { get; init; }
    public required string DeviceId { get; init; }
    public required string OperatorId { get; init; }
    public required string OperatorName { get; init; }
    public string? OperatorNote { get; init; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

sealed class NsxRemoteSessionState
{
    private readonly object _socketSync = new();
    private readonly SemaphoreSlim _peerSendLock = new(1, 1);
    private WebSocket? _clientSocket;
    private WebSocket? _operatorSocket;

    public Guid SessionId { get; init; }
    public Guid RequestId { get; init; }
    public required string DeviceId { get; init; }
    public required string ClientAccessKey { get; init; }
    public required string OperatorAccessKey { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }

    public bool IsAuthorized(string role, string accessKey)
    {
        var expected = role switch
        {
            "client" => ClientAccessKey,
            "operator" => OperatorAccessKey,
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(accessKey) || expected.Length != accessKey.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(accessKey));
    }

    public NsxRemoteSessionInfo ToInfo(string role) => new(
        SessionId,
        RequestId,
        DeviceId,
        role.Equals("client", StringComparison.OrdinalIgnoreCase) ? ClientAccessKey : OperatorAccessKey,
        role,
        ExpiresUtc);

    public void Attach(string role, WebSocket socket)
    {
        lock (_socketSync)
        {
            if (role == "client")
                _clientSocket = socket;
            else if (role == "operator")
                _operatorSocket = socket;
        }
    }

    public void Detach(string role, WebSocket socket)
    {
        lock (_socketSync)
        {
            if (role == "client" && ReferenceEquals(_clientSocket, socket))
                _clientSocket = null;
            else if (role == "operator" && ReferenceEquals(_operatorSocket, socket))
                _operatorSocket = null;
        }
    }

    public WebSocket? GetPeer(string role)
    {
        lock (_socketSync)
            return role == "client" ? _operatorSocket : _clientSocket;
    }

    public async Task SendToPeerAsync(WebSocket peer, ReadOnlyMemory<byte> payload, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        await _peerSendLock.WaitAsync(cancellationToken);
        try
        {
            if (peer.State == WebSocketState.Open)
                await peer.SendAsync(payload, messageType, endOfMessage, cancellationToken);
        }
        finally
        {
            _peerSendLock.Release();
        }
    }
}

sealed record NsxDeploymentIdentity(
    string BuildId,
    string? MarkerBuildId,
    DateTimeOffset? PublishedAtUtc,
    string? AssemblySha256,
    string? MarkerAssemblySha256,
    bool HasPublishMarker,
    bool IsVerified)
{
    public static NsxDeploymentIdentity Load(string contentRootPath)
    {
        var markerPath = Path.Combine(contentRootPath, "nsx-deploy.json");
        var actualAssemblyHash = TryGetRuntimeAssemblySha256();

        try
        {
            if (File.Exists(markerPath))
            {
                using var stream = File.OpenRead(markerPath);
                using var document = System.Text.Json.JsonDocument.Parse(stream);
                var root = document.RootElement;

                var markerBuildId = root.TryGetProperty("buildId", out var buildIdElement)
                    ? buildIdElement.GetString()?.Trim()
                    : null;
                var markerHash = root.TryGetProperty("assemblySha256", out var hashElement)
                    ? hashElement.GetString()?.Trim().ToLowerInvariant()
                    : null;

                DateTimeOffset? publishedAtUtc = null;
                if (root.TryGetProperty("publishedAtUtc", out var publishedElement)
                    && DateTimeOffset.TryParse(
                        publishedElement.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsedPublishedAtUtc))
                {
                    publishedAtUtc = parsedPublishedAtUtc;
                }

                if (!string.IsNullOrWhiteSpace(markerBuildId))
                {
                    var verified = !string.IsNullOrWhiteSpace(markerHash)
                        && !string.IsNullOrWhiteSpace(actualAssemblyHash)
                        && string.Equals(markerHash, actualAssemblyHash, StringComparison.OrdinalIgnoreCase);
                    var effectiveBuildId = verified
                        ? markerBuildId
                        : BuildRuntimeFallbackId(actualAssemblyHash);

                    return new NsxDeploymentIdentity(
                        effectiveBuildId,
                        markerBuildId,
                        publishedAtUtc,
                        actualAssemblyHash,
                        markerHash,
                        true,
                        verified);
                }
            }
        }
        catch
        {
            // Marker bozuk/eksik olsa bile siteyi kapatma. Asagidaki runtime kimligi kullanilir.
        }

        return new NsxDeploymentIdentity(
            BuildRuntimeFallbackId(actualAssemblyHash),
            null,
            null,
            actualAssemblyHash,
            null,
            false,
            false);
    }

    private static string BuildRuntimeFallbackId(string? actualAssemblyHash)
    {
        if (!string.IsNullOrWhiteSpace(actualAssemblyHash))
            return $"runtime-{actualAssemblyHash[..Math.Min(12, actualAssemblyHash.Length)]}";

        var moduleId = typeof(NsxDeploymentIdentity).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        return $"runtime-{moduleId[..Math.Min(12, moduleId.Length)]}";
    }

    private static string? TryGetRuntimeAssemblySha256()
    {
        try
        {
            var assemblyPath = typeof(NsxDeploymentIdentity).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                return null;

            using var stream = File.OpenRead(assemblyPath);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
