using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        private const string RememberedAdminEmailCookie = "NSYazilim.Admin.RememberedEmail";
        private const long MaxUploadSize = 1_073_741_824; // 1 GB
        private const int MaxProductImageCount = 10;
        private const long MaxAdvertisementImageSize = 5 * 1024 * 1024;
        private const long MaxSupportProgramUploadSize = 300L * 1024 * 1024;
        private const string SupportProgramFileName = "NSXUzaktanYardimLite.exe";
        private static readonly HashSet<string> AllowedProgramPackageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".zip", ".rar", ".7z", ".msix", ".appx", ".msixbundle", ".appxbundle"
        };
        private const string FreeVeresiyeProgramCode = "NSXVERESIYETAKIPPROFREE";
        private const string LegacyVeresiyeAdvertisementCode = "NSXVERESIYEDEFTERI";
        private const string LegacyVeresiyeUpdateCode = "NSXVERESIYE";
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _environment;
        private readonly IEmailSender _emailSender;
        private readonly IConfiguration _configuration;
        private readonly OfflineLicenseService _offlineLicenseService;
        private readonly ClientIpService _clientIpService;
        private readonly IpGeolocationService _ipGeolocationService;
        private readonly LocalizationTranslationQueueService _translationQueue;
        private readonly ShopierService _shopierService;

        public AdminController(ApplicationDbContext context, IWebHostEnvironment environment, IEmailSender emailSender, IConfiguration configuration, OfflineLicenseService offlineLicenseService, ClientIpService clientIpService, IpGeolocationService ipGeolocationService, LocalizationTranslationQueueService translationQueue, ShopierService shopierService)
        {
            _context = context;
            _environment = environment;
            _emailSender = emailSender;
            _configuration = configuration;
            _offlineLicenseService = offlineLicenseService;
            _clientIpService = clientIpService;
            _ipGeolocationService = ipGeolocationService;
            _translationQueue = translationQueue;
            _shopierService = shopierService;
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true && User.IsInRole("Admin"))
                return RedirectToAction(nameof(Index));

            var rememberedEmail = (Request.Cookies[RememberedAdminEmailCookie] ?? string.Empty).Trim();
            ViewBag.Email = rememberedEmail;
            ViewBag.RememberMe = !string.IsNullOrWhiteSpace(rememberedEmail);

            return View();
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string email, string password, bool rememberMe = false)
        {
            email = (email ?? string.Empty).Trim().ToLowerInvariant();
            password ??= string.Empty;

            var user = await _context.Users
                .FirstOrDefaultAsync(x => x.Email.ToLower() == email && !x.IsDeleted && x.IsActive);

            if (user == null || user.Role != "Admin" || !PasswordHasher.Verify(password, user.PasswordHash))
            {
                ViewBag.Error = "E-posta veya şifre hatalı.";
                ViewBag.Email = email;
                ViewBag.RememberMe = rememberMe;
                return View();
            }

            if (rememberMe)
            {
                Response.Cookies.Append(RememberedAdminEmailCookie, user.Email, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddDays(180),
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    IsEssential = true,
                    Path = "/Admin"
                });
            }
            else
            {
                Response.Cookies.Delete(RememberedAdminEmailCookie, new CookieOptions
                {
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/Admin"
                });
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.FullName),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await UpdateUserLocationAsync(user);
            await _context.SaveChangesAsync();

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var nextMonth = monthStart.AddMonths(1);

            var completedOrderQuery = _context.Orders.Where(x =>
                x.PaymentStatus == "Paid" ||
                x.PaymentStatus == "Completed" ||
                x.OrderStatus == "Paid" ||
                x.OrderStatus == "Completed" ||
                x.OrderStatus == "Delivered");

            var model = new AdminDashboardViewModel
            {
                TotalUsers = await _context.Users.CountAsync(x => !x.IsDeleted && x.Role == "User"),
                ActiveUsers = await _context.Users.CountAsync(x => !x.IsDeleted && x.IsActive && x.Role == "User"),
                TotalProducts = await _context.Products.CountAsync(x => !x.IsDeleted),
                InStockProducts = await _context.Products.CountAsync(x => !x.IsDeleted && x.StockQuantity > 0),
                OutOfStockProducts = await _context.Products.CountAsync(x => !x.IsDeleted && x.StockQuantity <= 0),
                TotalOrders = await _context.Orders.CountAsync(),
                TotalRevenue = await _context.Orders.SumAsync(x => (decimal?)x.TotalAmount) ?? 0,
                TodayRevenue = await _context.Orders.Where(x => x.CreatedAt >= today && x.CreatedAt < tomorrow).SumAsync(x => (decimal?)x.TotalAmount) ?? 0,
                ThisMonthRevenue = await _context.Orders.Where(x => x.CreatedAt >= monthStart && x.CreatedAt < nextMonth).SumAsync(x => (decimal?)x.TotalAmount) ?? 0,
                CompletedRevenue = await completedOrderQuery.SumAsync(x => (decimal?)x.TotalAmount) ?? 0,
                PaidOrders = await completedOrderQuery.CountAsync(),
                TodayOrders = await _context.Orders.CountAsync(x => x.CreatedAt >= today && x.CreatedAt < tomorrow),
                ThisMonthOrders = await _context.Orders.CountAsync(x => x.CreatedAt >= monthStart && x.CreatedAt < nextMonth),
                TotalLicenses = await _context.Licenses.CountAsync(),
                ActiveLicenses = await _context.Licenses.CountAsync(x => x.IsActive && (x.EndDate == null || x.EndDate >= DateTime.Now)),
                TotalDemoDownloads = await _context.DemoDownloads.CountAsync()
            };

            try
            {
                model.PendingComments = await _context.ProductComments.CountAsync(x => !x.IsDeleted && !x.IsApproved);
                model.ApprovedComments = await _context.ProductComments.CountAsync(x => !x.IsDeleted && x.IsApproved);
            }
            catch
            {
                model.PendingComments = 0;
                model.ApprovedComments = 0;
            }

            model.LatestOrders = await _context.Orders
                .Include(x => x.User)
                .OrderByDescending(x => x.Id)
                .Take(6)
                .Select(x => new AdminDashboardOrderItem
                {
                    Id = x.Id,
                    OrderNumber = x.OrderNumber,
                    CustomerName = x.User != null ? x.User.FullName : "Müşteri",
                    TotalAmount = x.TotalAmount,
                    OrderStatus = x.OrderStatus,
                    CreatedAt = x.CreatedAt
                })
                .ToListAsync();

            model.LowStockProducts = await _context.Products
                .Where(x => !x.IsDeleted && x.IsActive && x.StockQuantity <= 5)
                .OrderBy(x => x.StockQuantity)
                .Take(8)
                .Select(x => new AdminDashboardProductItem
                {
                    Id = x.Id,
                    Name = x.Name,
                    StockQuantity = x.StockQuantity,
                    YearlyPrice = x.YearlyPrice,
                    LifetimePrice = x.LifetimePrice
                })
                .ToListAsync();

            model.LatestLicenses = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .OrderByDescending(x => x.Id)
                .Take(6)
                .Select(x => new AdminDashboardLicenseItem
                {
                    Id = x.Id,
                    LicenseKey = x.LicenseKey,
                    ProductName = x.Product != null ? x.Product.Name : "Ürün",
                    CustomerName = x.User != null ? x.User.FullName : "Müşteri",
                    IsActive = x.IsActive,
                    EndDate = x.EndDate
                })
                .ToListAsync();

            return View(model);
        }

        [HttpGet]
        public IActionResult SupportProgram()
        {
            var filePath = GetSupportProgramPath();
            var fileInfo = new FileInfo(filePath);

            ViewBag.ProgramExists = fileInfo.Exists;
            ViewBag.ProgramSize = fileInfo.Exists ? FormatFileSize(fileInfo.Length) : "-";
            ViewBag.ProgramUpdatedAt = fileInfo.Exists ? fileInfo.LastWriteTime : (DateTime?)null;
            ViewBag.DownloadUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/uzaktan-yardim-indir";

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxSupportProgramUploadSize)]
        public async Task<IActionResult> UploadSupportProgram(IFormFile? supportProgram)
        {
            if (supportProgram == null || supportProgram.Length <= 0)
            {
                TempData["Error"] = "Yüklenecek destek programı seçilmedi.";
                return RedirectToAction(nameof(SupportProgram));
            }

            if (supportProgram.Length > MaxSupportProgramUploadSize)
            {
                TempData["Error"] = "Destek programı en fazla 300 MB olabilir.";
                return RedirectToAction(nameof(SupportProgram));
            }

            var extension = Path.GetExtension(supportProgram.FileName);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Destek programı yalnızca EXE dosyası olabilir.";
                return RedirectToAction(nameof(SupportProgram));
            }

            if (!HasExpectedProgramPackageSignature(supportProgram, extension))
            {
                TempData["Error"] = "Seçilen dosya geçerli bir Windows EXE dosyası değil.";
                return RedirectToAction(nameof(SupportProgram));
            }

            var targetPath = GetSupportProgramPath();
            var targetDirectory = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(targetDirectory);

            var tempPath = Path.Combine(targetDirectory, $".{SupportProgramFileName}.{Guid.NewGuid():N}.upload");

            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await supportProgram.CopyToAsync(stream, HttpContext.RequestAborted);
                    await stream.FlushAsync(HttpContext.RequestAborted);
                }

                var tempInfo = new FileInfo(tempPath);
                if (!tempInfo.Exists || tempInfo.Length != supportProgram.Length)
                    throw new IOException("Yüklenen dosyanın boyutu doğrulanamadı.");

                System.IO.File.Move(tempPath, targetPath, overwrite: true);

                TempData["Success"] = "Destek programı başarıyla yüklendi. İletişim sayfasındaki indirme alanı artık bu EXE'yi sunuyor.";
            }
            catch (OperationCanceledException)
            {
                TempData["Error"] = "Destek programı yüklemesi iptal edildi.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Destek programı yüklenemedi: {ex.Message}";
            }
            finally
            {
                SafeDeletePhysicalFile(tempPath);
            }

            return RedirectToAction(nameof(SupportProgram));
        }

        [HttpGet]
        public async Task<IActionResult> ActiveInstallations(string? q = null, string? productCode = null, string? activity = null)
        {
            var now = DateTime.Now;
            var today = now.Date;
            var last7Days = now.AddDays(-7);
            var last30Days = now.AddDays(-30);
            q = (q ?? string.Empty).Trim();
            productCode = NormalizeProductCode(productCode);
            activity = (activity ?? string.Empty).Trim().ToLowerInvariant();

            // Successful V2, legacy and free auto-activation calls update the installation heartbeat.
            // Only technical installation metadata is used; customer business data is not collected.
            var licenses = await _context.Licenses
                .AsNoTracking()
                .Include(x => x.User)
                .Include(x => x.Product)
                .Include(x => x.Devices)
                .Where(x => x.User != null && !x.User.IsDeleted && x.User.Role == "User")
                .OrderByDescending(x => x.LastCheckedAt ?? DateTime.MinValue)
                .ThenByDescending(x => x.Id)
                .ToListAsync();

            static string NormalizeValue(string? value) => (value ?? string.Empty).Trim();
            static string MaskLicense(string? key)
            {
                var value = NormalizeValue(key);
                if (value.Length <= 8) return value;
                return $"{value[..4]}••••{value[^4..]}";
            }

            string ResolveProductCode(License license)
            {
                var code = NormalizeProductCode(license.ProductCode);
                if (!string.IsNullOrWhiteSpace(code)) return code;
                code = NormalizeProductCode(license.Product?.ProductCode);
                if (!string.IsNullOrWhiteSpace(code)) return code;
                return NormalizeProductCode(license.Product?.Slug);
            }

            var allRows = licenses.Select(license =>
            {
                var devices = license.Devices ?? new List<LicenseDevice>();
                var lastSeen = license.LastCheckedAt;
                var deviceLastSeen = devices.Count == 0 ? (DateTime?)null : devices.Max(x => x.LastSeenAt);
                if (deviceLastSeen.HasValue && (!lastSeen.HasValue || deviceLastSeen.Value > lastSeen.Value))
                    lastSeen = deviceLastSeen;

                var versionCandidates = new List<string>();
                void AddVersion(string? value)
                {
                    var normalized = NormalizeValue(value);
                    if (string.IsNullOrWhiteSpace(normalized))
                        return;
                    if (!versionCandidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        versionCandidates.Add(normalized);
                }

                AddVersion(license.LastAppVersion);
                foreach (var device in devices.OrderByDescending(x => x.LastSeenAt))
                    AddVersion(device.AppVersion);

                var versionText = versionCandidates.Count <= 3
                    ? string.Join(" · ", versionCandidates)
                    : string.Join(" · ", versionCandidates.Take(3)) + $" +{versionCandidates.Count - 3}";

                var level = lastSeen == null ? 0
                    : lastSeen >= today ? 4
                    : lastSeen >= last7Days ? 3
                    : lastSeen >= last30Days ? 2
                    : 1;
                var status = level switch
                {
                    4 => "Bugün aktif",
                    3 => "Son 7 gün aktif",
                    2 => "Son 30 gün aktif",
                    1 => "30+ gündür pasif",
                    _ => "Henüz doğrulanmadı"
                };

                var locationParts = new[] { license.LastCity, license.LastRegion, license.LastCountry }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                var productName = NormalizeValue(license.Product?.Name);
                if (string.IsNullOrWhiteSpace(productName))
                    productName = "NSX Ürünü";

                return new AdminActiveInstallationRow
                {
                    LicenseId = license.Id,
                    UserId = license.UserId,
                    CustomerName = NormalizeValue(license.User?.FullName),
                    Email = NormalizeValue(license.User?.Email),
                    Phone = NormalizeValue(license.User?.Phone),
                    ProductName = productName,
                    ProductCode = ResolveProductCode(license),
                    LicenseKeyMasked = MaskLicense(license.LicenseKey),
                    AppVersion = versionText,
                    IpAddress = NormalizeValue(license.LastIpAddress),
                    Location = string.Join(" / ", locationParts),
                    LastSeenAt = lastSeen,
                    DeviceCount = devices.Count,
                    ActiveDeviceCount = devices.Count(x => !x.IsBlocked && x.LastSeenAt >= last30Days),
                    LicenseIsActive = license.IsActive && license.LicenseStatus == "Active" && (!license.EndDate.HasValue || license.EndDate.Value >= now),
                    LicenseEndDate = license.EndDate,
                    ActivityStatus = status,
                    ActivityLevel = level
                };
            }).ToList();

            var productOptions = allRows
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AdminActiveInstallationProductOption
                {
                    ProductCode = g.Key,
                    ProductName = g.Select(x => x.ProductName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? g.Key
                })
                .OrderBy(x => x.ProductName)
                .ToList();

            var model = new AdminActiveInstallationsViewModel
            {
                TotalMatchedCustomers = allRows.Select(x => x.UserId).Distinct().Count(),
                TotalMatchedLicenses = allRows.Count,
                ActiveToday = allRows.Where(x => x.LastSeenAt >= today).Select(x => x.UserId).Distinct().Count(),
                ActiveLast7Days = allRows.Where(x => x.LastSeenAt >= last7Days).Select(x => x.UserId).Distinct().Count(),
                ActiveLast30Days = allRows.Where(x => x.LastSeenAt >= last30Days).Select(x => x.UserId).Distinct().Count(),
                InactiveOrNever = allRows.Where(x => x.LastSeenAt == null || x.LastSeenAt < last30Days).Select(x => x.UserId).Distinct().Count(),
                ActiveDevices = allRows.Sum(x => x.ActiveDeviceCount),
                GeneratedAt = now,
                Search = q,
                ProductCode = productCode,
                Activity = activity,
                Products = productOptions,
                ProductStats = allRows
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                    .GroupBy(x => new { x.ProductCode, x.ProductName })
                    .Select(g => new AdminActiveInstallationProductStat
                    {
                        ProductCode = g.Key.ProductCode,
                        ProductName = g.Key.ProductName,
                        MatchedCustomers = g.Select(x => x.UserId).Distinct().Count(),
                        ActiveLast30Days = g.Where(x => x.LastSeenAt >= last30Days).Select(x => x.UserId).Distinct().Count()
                    })
                    .OrderByDescending(x => x.ActiveLast30Days)
                    .ThenByDescending(x => x.MatchedCustomers)
                    .Take(8)
                    .ToList()
            };

            IEnumerable<AdminActiveInstallationRow> filtered = allRows;
            if (!string.IsNullOrWhiteSpace(productCode))
                filtered = filtered.Where(x => string.Equals(x.ProductCode, productCode, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(q))
            {
                filtered = filtered.Where(x =>
                    x.CustomerName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.Email.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.Phone.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.ProductName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.ProductCode.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.AppVersion.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            filtered = activity switch
            {
                "today" => filtered.Where(x => x.LastSeenAt >= today),
                "7d" => filtered.Where(x => x.LastSeenAt >= last7Days),
                "30d" => filtered.Where(x => x.LastSeenAt >= last30Days),
                "inactive" => filtered.Where(x => x.LastSeenAt == null || x.LastSeenAt < last30Days),
                "verified" => filtered.Where(x => x.LastSeenAt != null),
                "never" => filtered.Where(x => x.LastSeenAt == null),
                _ => filtered
            };

            model.Rows = filtered
                .OrderByDescending(x => x.LastSeenAt ?? DateTime.MinValue)
                .ThenBy(x => x.CustomerName)
                .Take(1000)
                .ToList();

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Messages()
        {
            var messages = await _context.MailLogs
                .Where(x => x.MailType == "FooterContact" || x.MailType == "LiveSupport")
                .OrderByDescending(x => x.Id)
                .Take(100)
                .ToListAsync();

            return View(messages);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MessageDelete(int id)
        {
            var message = await _context.MailLogs.FirstOrDefaultAsync(x =>
                x.Id == id && (x.MailType == "FooterContact" || x.MailType == "LiveSupport"));

            if (message == null)
            {
                TempData["Error"] = "Mesaj kaydı bulunamadı.";
                return RedirectToAction(nameof(Messages));
            }

            _context.MailLogs.Remove(message);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Mesaj kaydı silindi.";
            return RedirectToAction(nameof(Messages));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MessagesClear()
        {
            var messages = await _context.MailLogs
                .Where(x => x.MailType == "FooterContact" || x.MailType == "LiveSupport")
                .ToListAsync();

            if (messages.Any())
            {
                _context.MailLogs.RemoveRange(messages);
                await _context.SaveChangesAsync();
            }

            TempData["Success"] = "Mesaj geçmişi temizlendi.";
            return RedirectToAction(nameof(Messages));
        }

        [HttpGet]
        public IActionResult LiveChat()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> ApiServices()
        {
            var now = DateTime.Now;
            var licenseApiKey = _configuration["LicenseApi:ApiKey"];

            var model = new AdminApiServicesViewModel
            {
                LicenseApiKeyConfigured = !string.IsNullOrWhiteSpace(licenseApiKey),
                TotalLicenses = await _context.Licenses.CountAsync(),
                ActiveLicenses = await _context.Licenses.CountAsync(x => x.IsActive && (x.EndDate == null || x.EndDate >= now)),
                TotalUpdates = await _context.ProductUpdates.CountAsync(),
                ActiveUpdates = await _context.ProductUpdates.CountAsync(x => x.IsActive),
                TotalAdvertisements = await _context.Advertisements.CountAsync(x => !x.IsDeleted),
                ActiveAdvertisements = await _context.Advertisements.CountAsync(x => !x.IsDeleted && x.IsActive),
                Services = BuildApiServiceCatalog()
            };

            return View(model);
        }

        private static List<AdminApiServiceItem> BuildApiServiceCatalog()
        {
            return new List<AdminApiServiceItem>
            {
                new()
                {
                    Name = "Lisans Aktivasyon",
                    Description = "Program lisans anahtarını makine kimliğiyle eşleştirir. Format/cihaz sonrası kayıtlı e-posta ve anahtar doğrulanırsa lisans yeni kuruluma güvenli biçimde aktarılır.",
                    Method = "GET",
                    Path = "/License/Activate",
                    Auth = "x-api-key header veya apiKey query",
                    Parameters = "key, machineId, email, apiKey",
                    SampleUrl = "/License/Activate?key=NSX-XXXX-XXXX&machineId=PC-001&email=kayitli@eposta.com&apiKey=***",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "Lisans Durum",
                    Description = "Mevcut lisansın aktiflik, bitiş tarihi ve kalan gün bilgisini kontrol eder.",
                    Method = "GET",
                    Path = "/License/Status",
                    Auth = "x-api-key header veya apiKey query",
                    Parameters = "key, machineId, apiKey",
                    SampleUrl = "/License/Status?key=NSX-XXXX-XXXX&machineId=PC-001&apiKey=***",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "Lisans V2 Aktivasyon",
                    Description = "Yeni program sürümleri için e-posta + lisans anahtarı + ürün kodu + cihaz kimliğini doğrular. İlk cihaz korunur, farklı cihaz loglanır ve reddedilir.",
                    Method = "POST",
                    Path = "/api/license/v2/activate",
                    Auth = "x-api-key header veya apiKey query",
                    Parameters = "email, licenseKey, productCode, machineId, deviceName, appVersion, osVersion",
                    SampleUrl = "/api/license/v2/activate",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "Lisans V2 Kontrol",
                    Description = "Program açıkken lisansı tekrar kontrol eder. Site onay verirse OnlineVerified döner; reddederse ikinci cihaz demo/kısıtlı moda alınabilir.",
                    Method = "POST",
                    Path = "/api/license/v2/check",
                    Auth = "x-api-key header veya apiKey query",
                    Parameters = "email, licenseKey, productCode, machineId, appVersion",
                    SampleUrl = "/api/license/v2/check",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "Program Güncelleme Kontrol",
                    Description = "Masaüstü programların ürün kodu ve mevcut sürüme göre yeni paket olup olmadığını kontrol eder.",
                    Method = "GET",
                    Path = "/api/update/check",
                    Auth = "Yok",
                    Parameters = "productCode, version",
                    SampleUrl = $"/api/update/check?productCode={FreeVeresiyeProgramCode}&version=1.0.0",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "Aktif Reklam",
                    Description = "Ürün kodu ve reklam slotuna göre program içi gösterilecek aktif reklamı döndürür.",
                    Method = "GET",
                    Path = "/api/ads/active",
                    Auth = "Yok",
                    Parameters = "productCode, slot",
                    SampleUrl = $"/api/ads/active?productCode={FreeVeresiyeProgramCode}&slot=FREE_BOTTOM_728X90",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "Reklam Görseli",
                    Description = "Program uyumluluğu için reklam görselini güvenli biçimde döndürür; WebP görseller PNG'ye çevrilebilir.",
                    Method = "GET",
                    Path = "/api/ads/image/{id}",
                    Auth = "Yok",
                    Parameters = "id",
                    SampleUrl = "/api/ads/image/1",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "API Servis Kataloğu",
                    Description = "Dış sistemlerin mevcut API endpoint listesini JSON olarak görmesini sağlar.",
                    Method = "GET",
                    Path = "/api/services",
                    Auth = "Yok",
                    Parameters = "-",
                    SampleUrl = "/api/services",
                    StatusLabel = "Aktif"
                },
                new()
                {
                    Name = "API Sağlık Kontrol",
                    Description = "API ve veritabanı bağlantısının çalışıp çalışmadığını kontrol eder.",
                    Method = "GET",
                    Path = "/api/status",
                    Auth = "Yok",
                    Parameters = "-",
                    SampleUrl = "/api/status",
                    StatusLabel = "Aktif"
                }
            };
        }

        [HttpGet]
        public async Task<IActionResult> Users()
        {
            var users = await _context.Users
                .Where(x => !x.IsDeleted)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            return View(users);
        }

        [HttpGet]
        public async Task<IActionResult> PhoneLocation(string? phone, CancellationToken cancellationToken)
        {
            var model = new AdminPhoneLocationLookupViewModel
            {
                Phone = (phone ?? string.Empty).Trim()
            };

            if (string.IsNullOrWhiteSpace(phone))
                return View(model);

            model.IsSearched = true;

            var normalizedPhone = NormalizeTurkishMobilePhone(phone);
            if (normalizedPhone == null)
            {
                model.ValidationMessage = "Geçerli bir Türkiye cep telefonu numarası girin. Örnek: 05XXXXXXXXX";
                return View(model);
            }

            model.Phone = normalizedPhone;
            model.MaskedPhone = MaskPhone(normalizedPhone);

            int? currentUserId = null;
            var currentUserIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(currentUserIdValue, out var parsedCurrentUserId))
                currentUserId = parsedCurrentUserId;

            User? user = null;

            // Kendi admin numarası sorgulanıyorsa aynı numaraya sahip başka bir kayıt yerine
            // oturumdaki admin hesabını önceliklendir.
            if (currentUserId.HasValue)
            {
                var currentUser = await _context.Users
                    .FirstOrDefaultAsync(x => x.Id == currentUserId.Value && !x.IsDeleted, cancellationToken);

                if (currentUser != null &&
                    string.Equals(NormalizeTurkishMobilePhone(currentUser.Phone), normalizedPhone, StringComparison.Ordinal))
                {
                    user = currentUser;
                }
            }

            if (user == null)
            {
                var phoneTail = normalizedPhone[^7..];
                var possibleUsers = await _context.Users
                    .Where(x => !x.IsDeleted && x.Phone != null && x.Phone.Contains(phoneTail))
                    .OrderByDescending(x => x.Id)
                    .Take(100)
                    .ToListAsync(cancellationToken);

                user = possibleUsers.FirstOrDefault(x =>
                    string.Equals(NormalizeTurkishMobilePhone(x.Phone), normalizedPhone, StringComparison.Ordinal));
            }

            var candidates = new List<PhoneLocationCandidate>();
            var userRecordChanged = false;
            var licenseIds = new List<int>();

            if (user != null)
            {
                model.HasMatch = true;
                model.MatchedFullName = user.FullName;

                AddPhoneLocationCandidate(candidates,
                    user.LastCity,
                    user.LastRegion,
                    user.LastCountry,
                    user.LastIpAddress,
                    user.LastGeoLookupAt ?? user.LastLoginAt,
                    "Üye hesabı son giriş kaydı");

                // Sorgulanan numara oturumdaki admin hesabına aitse mevcut bağlantı IP'si
                // bu hesaba kesin olarak aittir. Böylece kendi numarası ilk sorguda da çalışır.
                if (currentUserId == user.Id)
                {
                    var currentRequestIp = _clientIpService.GetClientIp(HttpContext);
                    AddPhoneLocationCandidate(candidates,
                        null,
                        null,
                        null,
                        currentRequestIp,
                        DateTime.Now,
                        "Bu admin oturumunun güncel bağlantısı");

                    if (!string.IsNullOrWhiteSpace(currentRequestIp) &&
                        !string.Equals(user.LastIpAddress, currentRequestIp, StringComparison.OrdinalIgnoreCase))
                    {
                        user.LastIpAddress = currentRequestIp;
                        user.LastLoginAt = DateTime.Now;
                        userRecordChanged = true;
                    }
                }

                var licenses = await _context.Licenses
                    .AsNoTracking()
                    .Where(x => x.UserId == user.Id)
                    .Select(x => new
                    {
                        x.Id,
                        x.LastCity,
                        x.LastRegion,
                        x.LastCountry,
                        x.LastIpAddress,
                        ObservedAt = x.LastGeoLookupAt ?? x.LastCheckedAt ?? x.CreatedAt
                    })
                    .ToListAsync(cancellationToken);

                licenseIds = licenses.Select(x => x.Id).ToList();

                foreach (var license in licenses)
                {
                    AddPhoneLocationCandidate(candidates,
                        license.LastCity,
                        license.LastRegion,
                        license.LastCountry,
                        license.LastIpAddress,
                        license.ObservedAt,
                        "Lisans programı son kontrol kaydı");
                }

                var devices = await _context.LicenseDevices
                    .AsNoTracking()
                    .Where(x => x.License != null && x.License.UserId == user.Id)
                    .Select(x => new
                    {
                        x.LastCity,
                        x.LastRegion,
                        x.LastCountry,
                        x.LastIpAddress,
                        FirstIpAddress = x.FirstIpAddress,
                        ObservedAt = x.LastGeoLookupAt ?? x.LastSeenAt
                    })
                    .ToListAsync(cancellationToken);

                foreach (var device in devices)
                {
                    AddPhoneLocationCandidate(candidates,
                        device.LastCity,
                        device.LastRegion,
                        device.LastCountry,
                        device.LastIpAddress ?? device.FirstIpAddress,
                        device.ObservedAt,
                        "Lisans cihazı son bağlantı kaydı");
                }

                // Site ziyaret kayıtlarında şehir önceden çözülmemiş olsa bile kullanıcıya bağlı
                // gerçek IP bulunabilir. İlk sürümde bu kaynak taranmıyordu.
                try
                {
                    var visits = await _context.SiteVisits
                        .AsNoTracking()
                        .Where(x => x.UserId == user.Id)
                        .OrderByDescending(x => x.VisitedAtUtc)
                        .Take(100)
                        .Select(x => new
                        {
                            x.City,
                            x.Country,
                            x.IpAddress,
                            x.VisitedAtUtc
                        })
                        .ToListAsync(cancellationToken);

                    foreach (var visit in visits)
                    {
                        AddPhoneLocationCandidate(candidates,
                            visit.City,
                            null,
                            visit.Country,
                            visit.IpAddress,
                            visit.VisitedAtUtc,
                            "Üyeye bağlı site ziyaret kaydı");
                    }
                }
                catch
                {
                    // Eski canlı veritabanında tablo/kolon henüz yoksa sorgu ekranı çalışmaya devam etsin.
                }

                try
                {
                    var freeActivations = await _context.FreeLicenseActivationTokens
                        .AsNoTracking()
                        .Where(x => x.UserId == user.Id)
                        .OrderByDescending(x => x.UsedAt ?? x.CreatedAt)
                        .Take(100)
                        .Select(x => new
                        {
                            x.CreatedIpAddress,
                            x.UsedIpAddress,
                            ObservedAt = x.UsedAt ?? x.CreatedAt
                        })
                        .ToListAsync(cancellationToken);

                    foreach (var activation in freeActivations)
                    {
                        AddPhoneLocationCandidate(candidates,
                            null,
                            null,
                            null,
                            activation.UsedIpAddress ?? activation.CreatedIpAddress,
                            activation.ObservedAt,
                            "Ücretsiz lisans aktivasyon kaydı");
                    }
                }
                catch
                {
                }

                try
                {
                    var checkQuery = _context.LicenseCheckLogs.AsNoTracking();
                    if (licenseIds.Count > 0)
                    {
                        checkQuery = checkQuery.Where(x =>
                            (x.LicenseId.HasValue && licenseIds.Contains(x.LicenseId.Value)) ||
                            x.RequestEmail == user.Email);
                    }
                    else
                    {
                        checkQuery = checkQuery.Where(x => x.RequestEmail == user.Email);
                    }

                    var checkLogs = await checkQuery
                        .OrderByDescending(x => x.CreatedAt)
                        .Take(100)
                        .Select(x => new { x.IpAddress, x.CreatedAt })
                        .ToListAsync(cancellationToken);

                    foreach (var log in checkLogs)
                    {
                        AddPhoneLocationCandidate(candidates,
                            null,
                            null,
                            null,
                            log.IpAddress,
                            log.CreatedAt,
                            "Lisans kontrol geçmişi");
                    }
                }
                catch
                {
                }

                try
                {
                    var securityQuery = _context.LicenseSecurityLogs.AsNoTracking();
                    if (licenseIds.Count > 0)
                    {
                        securityQuery = securityQuery.Where(x =>
                            (x.LicenseId.HasValue && licenseIds.Contains(x.LicenseId.Value)) ||
                            x.RequestEmail == user.Email);
                    }
                    else
                    {
                        securityQuery = securityQuery.Where(x => x.RequestEmail == user.Email);
                    }

                    var securityLogs = await securityQuery
                        .OrderByDescending(x => x.CreatedAt)
                        .Take(100)
                        .Select(x => new { x.IpAddress, x.CreatedAt })
                        .ToListAsync(cancellationToken);

                    foreach (var log in securityLogs)
                    {
                        AddPhoneLocationCandidate(candidates,
                            null,
                            null,
                            null,
                            log.IpAddress,
                            log.CreatedAt,
                            "Lisans güvenlik geçmişi");
                    }
                }
                catch
                {
                }

                try
                {
                    var phoneTail = normalizedPhone[^7..];
                    var transfers = await _context.BankTransferNotifications
                        .AsNoTracking()
                        .Where(x => x.UserId == user.Id || (x.Phone != null && x.Phone.Contains(phoneTail)))
                        .OrderByDescending(x => x.CreatedAt)
                        .Take(100)
                        .ToListAsync(cancellationToken);

                    foreach (var transfer in transfers.Where(x =>
                                 x.UserId == user.Id ||
                                 string.Equals(NormalizeTurkishMobilePhone(x.Phone), normalizedPhone, StringComparison.Ordinal)))
                    {
                        AddPhoneLocationCandidate(candidates,
                            null,
                            null,
                            null,
                            transfer.IpAddress,
                            transfer.CreatedAt,
                            "Havale bildirim kaydı");
                    }
                }
                catch
                {
                }
            }

            var phoneTailForDownloads = normalizedPhone[^7..];
            var possibleDownloads = await _context.DemoDownloads
                .AsNoTracking()
                .Where(x =>
                    (user != null && x.UserId == user.Id) ||
                    (x.Phone != null && x.Phone.Contains(phoneTailForDownloads)))
                .OrderByDescending(x => x.DownloadedAt)
                .Take(100)
                .ToListAsync(cancellationToken);

            var matchingDownloads = possibleDownloads
                .Where(x =>
                    (user != null && x.UserId == user.Id) ||
                    string.Equals(NormalizeTurkishMobilePhone(x.Phone), normalizedPhone, StringComparison.Ordinal))
                .ToList();

            if (matchingDownloads.Count > 0)
            {
                model.HasMatch = true;
                if (string.IsNullOrWhiteSpace(model.MatchedFullName))
                    model.MatchedFullName = matchingDownloads.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.FullName))?.FullName;

                foreach (var download in matchingDownloads)
                {
                    AddPhoneLocationCandidate(candidates,
                        download.City,
                        download.Region,
                        download.Country,
                        download.IpAddress,
                        download.GeoLookupAt ?? download.DownloadedAt,
                        "Demo indirme kaydı");
                }
            }

            var storedLocation = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x.City) || !string.IsNullOrWhiteSpace(x.Region))
                .OrderByDescending(x => x.ObservedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            var latestPublicIp = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x.IpAddress) && _clientIpService.IsPublicIp(x.IpAddress))
                .OrderByDescending(x => x.ObservedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            model.HasPublicIp = latestPublicIp != null;
            PhoneLocationCandidate? selected = storedLocation;

            if (latestPublicIp != null &&
                (storedLocation == null ||
                 (latestPublicIp.ObservedAt ?? DateTime.MinValue) >= (storedLocation.ObservedAt ?? DateTime.MinValue)))
            {
                var resolved = await _ipGeolocationService.ResolveAsync(latestPublicIp.IpAddress, cancellationToken);
                var resolvedCity = resolved?.City ?? resolved?.Region;

                if (!string.IsNullOrWhiteSpace(resolvedCity))
                {
                    selected = new PhoneLocationCandidate
                    {
                        City = resolvedCity,
                        Region = string.Equals(resolvedCity, resolved?.Region, StringComparison.OrdinalIgnoreCase)
                            ? null
                            : resolved?.Region,
                        Country = resolved?.Country,
                        IpAddress = latestPublicIp.IpAddress,
                        ObservedAt = latestPublicIp.ObservedAt,
                        Source = $"{latestPublicIp.Source} / IP tahmini"
                    };

                    if (user != null)
                    {
                        user.LastIpAddress = latestPublicIp.IpAddress;
                        user.LastCity = selected.City;
                        user.LastRegion = selected.Region;
                        user.LastCountry = selected.Country;
                        user.LastGeoLookupAt = DateTime.Now;
                        userRecordChanged = true;
                    }
                }
            }

            if (userRecordChanged)
                await _context.SaveChangesAsync(cancellationToken);

            if (selected != null &&
                (!string.IsNullOrWhiteSpace(selected.City) || !string.IsNullOrWhiteSpace(selected.Region)))
            {
                model.HasLocation = true;
                model.City = selected.City ?? selected.Region;
                model.Region = string.Equals(model.City, selected.Region, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : selected.Region;
                model.Country = selected.Country;
                model.Source = selected.Source;
                model.ObservedAt = selected.ObservedAt;
            }
            else if (model.HasMatch)
            {
                model.StatusMessage = model.HasPublicIp
                    ? "Kayıt ve public IP bulundu; ancak GeoIP servislerinden şehir sonucu alınamadı. Birkaç saniye sonra yeniden sorgulayın."
                    : "Kayıt bulundu; ancak bu hesaba bağlı gerçek ziyaretçi IP'si henüz oluşmamış. Kullanıcı siteye giriş yaptığında veya lisans programı bağlandığında şehir kaydı oluşur.";
            }

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UserDelete(int id)
        {
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.Equals(currentUserId, id.ToString(), StringComparison.Ordinal))
            {
                TempData["Error"] = "Kendi admin hesabınızı silemezsiniz.";
                return RedirectToAction(nameof(Users));
            }

            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (user == null)
            {
                TempData["Error"] = "Üye bulunamadı.";
                return RedirectToAction(nameof(Users));
            }

            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Admin hesabı bu ekrandan silinemez.";
                return RedirectToAction(nameof(Users));
            }

            user.IsDeleted = true;
            user.IsActive = false;
            await _context.SaveChangesAsync();

            TempData["Success"] = "Üye pasife alındı ve listeden kaldırıldı.";
            return RedirectToAction(nameof(Users));
        }

        [HttpGet]
        public async Task<IActionResult> Orders()
        {
            var orders = await _context.Orders
                .Include(x => x.User)
                .Include(x => x.OrderItems)
                    .ThenInclude(x => x.Product)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            return View(orders);
        }

        [HttpGet]
        public async Task<IActionResult> Payments(string? q = null, string? paymentMethod = null)
        {
            q = (q ?? string.Empty).Trim();
            paymentMethod = (paymentMethod ?? string.Empty).Trim().ToLowerInvariant();

            var bankTransfers = await _context.BankTransferNotifications
                .AsNoTracking()
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.Order)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            var shopierOrders = await _context.ShopierOrders
                .AsNoTracking()
                .Where(x => !x.IsAdminHidden)
                .OrderByDescending(x => x.ShopierCreatedAt)
                .Take(500)
                .ToListAsync();

            var manualOrders = await _context.Orders
                .AsNoTracking()
                .Include(x => x.User)
                .Include(x => x.OrderItems)
                    .ThenInclude(x => x.Product)
                .Where(x => x.OrderNumber.StartsWith("NSX-MAN-"))
                .OrderByDescending(x => x.CreatedAt)
                .Take(500)
                .ToListAsync();

            var rows = new List<AdminPaymentRowViewModel>();

            foreach (var item in bankTransfers)
            {
                var isApproved = string.Equals(item.Status, "Approved", StringComparison.OrdinalIgnoreCase);
                var isRejected = string.Equals(item.Status, "Rejected", StringComparison.OrdinalIgnoreCase);
                rows.Add(new AdminPaymentRowViewModel
                {
                    Reference = item.Order?.OrderNumber ?? $"HVL-{item.Id}",
                    CustomerName = item.FullName,
                    CustomerEmail = item.Email,
                    CustomerPhone = item.Phone ?? string.Empty,
                    ProductName = item.Product?.Name ?? "Ürün",
                    LicenseLabel = string.Equals(item.LicenseType, "Lifetime", StringComparison.OrdinalIgnoreCase) ? "Sınırsız" : "Yıllık",
                    PaymentType = "Havale / EFT",
                    Provider = "Banka",
                    ProviderDetail = string.IsNullOrWhiteSpace(item.SenderBank) ? "Havale bildirimi" : item.SenderBank,
                    Amount = item.Amount,
                    Currency = "TRY",
                    Status = StatusTextHelper.BankTransferStatus(item.Status),
                    StatusCss = isApproved ? "ok" : isRejected ? "bad" : "warn",
                    CreatedAt = item.CreatedAt,
                    IsPaid = isApproved,
                    LocalOrderId = item.OrderId,
                    BankTransferId = item.Id,
                    SourceType = "bank",
                    SourceId = item.Id
                });
            }

            foreach (var item in shopierOrders)
            {
                var fulfillmentStatus = (item.LocalFulfillmentStatus ?? string.Empty).Trim();
                var isFulfilled = string.Equals(fulfillmentStatus, "Completed", StringComparison.OrdinalIgnoreCase);
                var isPaid = isFulfilled
                    || string.Equals(item.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.PaymentStatus, "completed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.PaymentStatus, "success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.PaymentStatus, "successful", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.PaymentStatus, "captured", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.PaymentStatus, "approved", StringComparison.OrdinalIgnoreCase);
                var customerName = string.Join(" ", new[] { item.CustomerFirstName, item.CustomerLastName }
                    .Where(x => !string.IsNullOrWhiteSpace(x)))
                    .Trim();
                var providerDetail = item.PaymentMethod switch
                {
                    "creditCard" => item.IsInstallments ? "Kredi kartı · Taksitli" : "Kredi kartı",
                    "debitCard" => "Banka kartı",
                    _ => "Kart ödemesi"
                };

                var (statusText, statusCss) = fulfillmentStatus.ToLowerInvariant() switch
                {
                    "completed" => ("Lisans teslim edildi", "ok"),
                    "waitinguser" => ("Üye eşleşmesi bekliyor", "warn"),
                    "waitingproduct" => ("Ürün eşleşmesi bekliyor", "warn"),
                    "waitingvariant" => ("Lisans türü kontrolü", "warn"),
                    "amountmismatch" => ("Tutar kontrolü gerekli", "bad"),
                    "currencymismatch" => ("Para birimi kontrolü", "bad"),
                    "stockproblem" => ("Stok sorunu", "bad"),
                    "failed" => ("Lisans teslimat hatası", "bad"),
                    "paymentpending" => ("Ödeme doğrulanıyor", "warn"),
                    _ => isPaid ? ("Ödendi", "ok") : (StatusTextHelper.PaymentStatus(item.PaymentStatus), "warn")
                };

                rows.Add(new AdminPaymentRowViewModel
                {
                    Reference = $"SHP-{item.ShopierOrderId}",
                    CustomerName = string.IsNullOrWhiteSpace(customerName) ? "Shopier müşterisi" : customerName,
                    CustomerEmail = item.CustomerEmail ?? string.Empty,
                    CustomerPhone = item.CustomerPhone ?? string.Empty,
                    ProductName = item.ProductTitle ?? "Shopier ürünü",
                    LicenseLabel = NormalizeShopierLicenseLabel(item.LicenseSelection),
                    PaymentType = "Banka / Kredi Kartı",
                    Provider = "Shopier",
                    ProviderDetail = providerDetail,
                    Amount = item.TotalAmount,
                    Currency = string.IsNullOrWhiteSpace(item.Currency) ? "TRY" : item.Currency,
                    Status = statusText,
                    StatusCss = statusCss,
                    StatusDetail = item.LocalFulfillmentMessage ?? string.Empty,
                    CreatedAt = item.ShopierCreatedAt,
                    IsPaid = isPaid,
                    LocalOrderId = item.LocalOrderId,
                    SourceType = "shopier",
                    SourceId = item.Id
                });
            }

            foreach (var order in manualOrders)
            {
                var firstItem = order.OrderItems.FirstOrDefault();
                var isPaid = string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(order.PaymentStatus, "Completed", StringComparison.OrdinalIgnoreCase);
                var isFailed = string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(order.PaymentStatus, "Rejected", StringComparison.OrdinalIgnoreCase);

                rows.Add(new AdminPaymentRowViewModel
                {
                    Reference = order.OrderNumber,
                    CustomerName = string.IsNullOrWhiteSpace(order.User?.FullName) ? "NSX müşterisi" : order.User.FullName,
                    CustomerEmail = order.User?.Email ?? string.Empty,
                    CustomerPhone = order.User?.Phone ?? string.Empty,
                    ProductName = firstItem?.Product?.Name ?? "Ürün",
                    LicenseLabel = string.Equals(firstItem?.LicenseType, "Lifetime", StringComparison.OrdinalIgnoreCase) ? "Sınırsız" : "Yıllık",
                    PaymentType = "Manuel Satış",
                    Provider = "NSX Admin",
                    ProviderDetail = "Admin manuel satış",
                    Amount = order.TotalAmount,
                    Currency = "TRY",
                    Status = StatusTextHelper.PaymentStatus(order.PaymentStatus),
                    StatusCss = isPaid ? "ok" : isFailed ? "bad" : "warn",
                    CreatedAt = order.CreatedAt,
                    IsPaid = isPaid,
                    LocalOrderId = order.Id,
                    SourceType = "manual",
                    SourceId = order.Id
                });
            }

            var allRows = rows.OrderByDescending(x => x.CreatedAt).ToList();
            var paidRows = allRows.Where(x => x.IsPaid).ToList();

            var filteredRows = allRows.AsEnumerable();
            if (paymentMethod == "card")
                filteredRows = filteredRows.Where(x => x.PaymentType == "Banka / Kredi Kartı");
            else if (paymentMethod == "transfer")
                filteredRows = filteredRows.Where(x => x.PaymentType == "Havale / EFT");
            else if (paymentMethod == "manual")
                filteredRows = filteredRows.Where(x => x.PaymentType == "Manuel Satış");

            if (!string.IsNullOrWhiteSpace(q))
            {
                filteredRows = filteredRows.Where(x =>
                    ContainsInvariant(x.Reference, q) ||
                    ContainsInvariant(x.CustomerName, q) ||
                    ContainsInvariant(x.CustomerEmail, q) ||
                    ContainsInvariant(x.CustomerPhone, q) ||
                    ContainsInvariant(x.ProductName, q) ||
                    ContainsInvariant(x.LicenseLabel, q) ||
                    ContainsInvariant(x.Provider, q));
            }

            var model = new AdminPaymentsViewModel
            {
                Rows = filteredRows.ToList(),
                Query = q,
                PaymentMethod = paymentMethod,
                TotalCount = allRows.Count,
                CardCount = allRows.Count(x => x.PaymentType == "Banka / Kredi Kartı"),
                BankTransferCount = allRows.Count(x => x.PaymentType == "Havale / EFT"),
                ManualSaleCount = allRows.Count(x => x.PaymentType == "Manuel Satış"),
                PendingBankTransferCount = bankTransfers.Count(x => string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
                PaidTotal = paidRows.Sum(x => x.Amount),
                CardPaidTotal = paidRows.Where(x => x.PaymentType == "Banka / Kredi Kartı").Sum(x => x.Amount),
                BankTransferPaidTotal = paidRows.Where(x => x.PaymentType == "Havale / EFT").Sum(x => x.Amount),
                ManualSalePaidTotal = paidRows.Where(x => x.PaymentType == "Manuel Satış").Sum(x => x.Amount),
                ShopierConfigured = _shopierService.IsConfigured,
                ShopierWebhookConfigured = _shopierService.IsWebhookConfigured,
                ShopierStatusMessage = _shopierService.IsConfigured
                    ? (_shopierService.IsWebhookConfigured ? "Shopier API + anlık webhook aktif." : "Shopier API hazır. Webhook henüz etkin değil.")
                    : "Shopier PAT sunucu ayarlarında tanımlı değil."
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PaymentsSyncShopier()
        {
            var result = await _shopierService.SyncRecentOrdersAsync(HttpContext.RequestAborted);
            TempData[result.Success ? "Success" : "Error"] = result.Message;
            return RedirectToAction(nameof(Payments));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PaymentsDelete(string sourceType, int id)
        {
            sourceType = (sourceType ?? string.Empty).Trim().ToLowerInvariant();

            if (sourceType == "bank")
            {
                var notification = await _context.BankTransferNotifications.FirstOrDefaultAsync(x => x.Id == id);
                if (notification == null)
                {
                    TempData["Error"] = "Havale / EFT ödeme kaydı bulunamadı.";
                    return RedirectToAction(nameof(Payments));
                }

                _context.BankTransferNotifications.Remove(notification);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Havale / EFT ödeme kaydı silindi. Bağlı sipariş ve lisans kayıtları korundu.";
                return RedirectToAction(nameof(Payments));
            }

            if (sourceType == "manual")
            {
                var order = await _context.Orders
                    .Include(x => x.OrderItems)
                    .FirstOrDefaultAsync(x => x.Id == id && x.OrderNumber.StartsWith("NSX-MAN-"));

                if (order == null)
                {
                    TempData["Error"] = "Manuel satış kaydı bulunamadı.";
                    return RedirectToAction(nameof(Payments));
                }

                var licenses = await _context.Licenses.Where(x => x.OrderId == order.Id).ToListAsync();
                foreach (var license in licenses)
                    license.OrderId = null;

                _context.Orders.Remove(order);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Manuel satış kaydı silindi. Oluşturulmuş lisanslar korunarak sipariş bağlantısı kaldırıldı.";
                return RedirectToAction(nameof(Payments));
            }

            if (sourceType == "shopier")
            {
                var shopierOrder = await _context.ShopierOrders.FirstOrDefaultAsync(x => x.Id == id);
                if (shopierOrder == null)
                {
                    TempData["Error"] = "Shopier satış kaydı bulunamadı.";
                    return RedirectToAction(nameof(Payments));
                }

                // Shopier siparişini fiziksel olarak silmek, sonraki API senkronunda aynı siparişin
                // yeniden işlenip ikinci kez lisans üretmesine yol açabilir. Bu nedenle yalnızca
                // admin ödeme listesinden kalıcı olarak gizlenir; sipariş/lisans bütünlüğü korunur.
                shopierOrder.IsAdminHidden = true;
                await _context.SaveChangesAsync();
                TempData["Success"] = "Shopier satış kaydı ödeme listesinden kaldırıldı. Sipariş ve lisans verileri korundu.";
                return RedirectToAction(nameof(Payments));
            }

            TempData["Error"] = "Silinecek ödeme kaynağı tanınmadı.";
            return RedirectToAction(nameof(Payments));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ShopierWebhookRegister()
        {
            var webhookUrl = _shopierService.ResolveWebhookUrl(Request);
            var result = await _shopierService.RegisterOrderCreatedWebhookAsync(webhookUrl, HttpContext.RequestAborted);
            TempData[result.Success ? "Success" : "Error"] = result.Message;

            if (result.Success)
            {
                var sync = await _shopierService.SyncRecentOrdersAsync(HttpContext.RequestAborted);
                if (!sync.Success)
                    TempData["Warning"] = sync.Message;
            }

            return RedirectToAction(nameof(Payments));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OrderAmountUpdate(int id, string amount)
        {
            var rawAmount = (amount ?? string.Empty).Trim();
            var parsed = decimal.TryParse(rawAmount, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var newAmount)
                || decimal.TryParse(rawAmount, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out newAmount);

            if (!parsed || newAmount <= 0)
            {
                TempData["Error"] = "Sipariş tutarı 0'dan büyük geçerli bir değer olmalıdır.";
                return RedirectToAction(nameof(Orders));
            }

            newAmount = decimal.Round(newAmount, 2, MidpointRounding.AwayFromZero);

            var order = await _context.Orders
                .Include(x => x.OrderItems)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (order == null)
            {
                TempData["Error"] = "Sipariş bulunamadı.";
                return RedirectToAction(nameof(Orders));
            }

            order.TotalAmount = newAmount;

            // Tek ürün / tek adet siparişlerde müşteri sipariş detayındaki ürün fiyatını da aynı tutar.
            if (order.OrderItems.Count == 1)
            {
                var item = order.OrderItems.First();
                if (item.Quantity == 1)
                    item.UnitPrice = newAmount;
            }

            // Siparişe bağlı havale bildirimi varsa ekranda eski otomatik tutarın kalmasını önle.
            var bankTransfers = await _context.BankTransferNotifications
                .Where(x => x.OrderId == id)
                .ToListAsync();
            foreach (var notification in bankTransfers)
                notification.Amount = newAmount;

            // Bayi satışıysa satış ve bayi kârını gerçek tahsil edilen tutarla senkron tut.
            var dealerSale = await _context.DealerSales.FirstOrDefaultAsync(x => x.OrderId == id);
            if (dealerSale != null)
            {
                dealerSale.SaleAmount = newAmount;
                var commission = string.Equals(dealerSale.CommissionModeSnapshot, "Fixed", StringComparison.OrdinalIgnoreCase)
                    ? dealerSale.CommissionFixedAmountSnapshot
                    : newAmount * dealerSale.CommissionPercentSnapshot / 100m;
                dealerSale.CommissionAmount = Math.Min(newAmount, Math.Max(0m, decimal.Round(commission, 2, MidpointRounding.AwayFromZero)));
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = $"{order.OrderNumber} sipariş tutarı {newAmount:N2} ₺ olarak güncellendi.";
            return RedirectToAction(nameof(Orders));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OrderDelete(int id)
        {
            var order = await _context.Orders
                .Include(x => x.OrderItems)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (order == null)
            {
                TempData["Error"] = "Sipariş bulunamadı.";
                return RedirectToAction(nameof(Orders));
            }

            var licenses = await _context.Licenses.Where(x => x.OrderId == id).ToListAsync();
            foreach (var license in licenses)
                license.OrderId = null;

            var bankTransfers = await _context.BankTransferNotifications.Where(x => x.OrderId == id).ToListAsync();
            foreach (var notification in bankTransfers)
                notification.OrderId = null;

            _context.Orders.Remove(order);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Sipariş kaydı silindi.";
            return RedirectToAction(nameof(Orders));
        }

        [HttpGet]
        public async Task<IActionResult> Licenses()
        {
            await RepairFreeVeresiyeLicenseMachineLinksAsync();

            var licenses = await _context.Licenses
                .Include(x => x.User)
                .Include(x => x.Order)
                .Include(x => x.Product)
                .Include(x => x.Devices)
                .Include(x => x.OfflineCertificates)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            return View(licenses);
        }

        [HttpGet]
        public async Task<IActionResult> LicenseCheckLogs(string? productCode = null, bool? success = null)
        {
            productCode = NormalizeProductCode(productCode);

            var query = _context.LicenseCheckLogs
                .Include(x => x.License)!
                    .ThenInclude(x => x!.Product)
                .Include(x => x.License)!
                    .ThenInclude(x => x!.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(productCode))
                query = query.Where(x => x.ProductCode == productCode);

            if (success.HasValue)
                query = query.Where(x => x.Success == success.Value);

            ViewBag.ProductCode = productCode;
            ViewBag.Success = success;

            var logs = await query
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Take(500)
                .ToListAsync();

            return View(logs);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseCheckLogsClear(string? productCode = null, bool? success = null)
        {
            productCode = NormalizeProductCode(productCode);

            var query = _context.LicenseCheckLogs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(productCode))
                query = query.Where(x => x.ProductCode == productCode);

            if (success.HasValue)
                query = query.Where(x => x.Success == success.Value);

            var logs = await query.ToListAsync();
            if (logs.Count > 0)
            {
                _context.LicenseCheckLogs.RemoveRange(logs);
                await _context.SaveChangesAsync();
            }

            TempData["Success"] = logs.Count > 0
                ? $"{logs.Count} lisans kontrol logu temizlendi."
                : "Temizlenecek lisans kontrol logu yok.";

            return RedirectToAction(nameof(LicenseCheckLogs), new { productCode, success });
        }

        [HttpGet]
        public async Task<IActionResult> LicenseSecurityLogs(string? productCode = null, string? severity = null)
        {
            productCode = NormalizeProductCode(productCode);
            severity = (severity ?? string.Empty).Trim();

            var query = _context.LicenseSecurityLogs
                .Include(x => x.License)!
                    .ThenInclude(x => x!.Product)
                .Include(x => x.License)!
                    .ThenInclude(x => x!.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(productCode))
                query = query.Where(x => x.ProductCode == productCode);

            if (!string.IsNullOrWhiteSpace(severity))
                query = query.Where(x => x.Severity == severity);

            ViewBag.ProductCode = productCode;
            ViewBag.Severity = severity;

            var logs = await query
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Take(500)
                .ToListAsync();

            return View(logs);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseSecurityLogsClear()
        {
            var logs = await _context.LicenseSecurityLogs.ToListAsync();
            var deletedCount = logs.Count;

            if (deletedCount > 0)
            {
                _context.LicenseSecurityLogs.RemoveRange(logs);
                await _context.SaveChangesAsync();
            }

            TempData["Success"] = deletedCount > 0
                ? $"{deletedCount} lisans güvenlik kaydı kalıcı olarak silindi."
                : "Silinecek lisans güvenlik kaydı bulunamadı.";

            return RedirectToAction(nameof(LicenseSecurityLogs));
        }

        [HttpGet]
        public async Task<IActionResult> LicenseDevices(string? productCode = null, bool? blocked = null)
        {
            productCode = NormalizeProductCode(productCode);

            var query = _context.LicenseDevices
                .Include(x => x.License)!
                    .ThenInclude(x => x!.Product)
                .Include(x => x.License)!
                    .ThenInclude(x => x!.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(productCode))
                query = query.Where(x => x.ProductCode == productCode);

            if (blocked.HasValue)
                query = query.Where(x => x.IsBlocked == blocked.Value);

            ViewBag.ProductCode = productCode;
            ViewBag.Blocked = blocked;

            var devices = await query
                .OrderByDescending(x => x.LastSeenAt)
                .ThenByDescending(x => x.Id)
                .Take(500)
                .ToListAsync();

            return View(devices);
        }

        [HttpGet]
        public async Task<IActionResult> BlockedDevices()
        {
            var devices = await _context.BlockedDevices
                .OrderByDescending(x => x.IsActive)
                .ThenByDescending(x => x.CreatedAt)
                .Take(500)
                .ToListAsync();

            return View(devices);
        }

        [HttpGet]
        public async Task<IActionResult> OfflineLicenseCertificates(bool? revoked = null)
        {
            var query = _context.OfflineLicenseCertificates
                .Include(x => x.License)!
                    .ThenInclude(x => x!.Product)
                .Include(x => x.License)!
                    .ThenInclude(x => x!.User)
                .AsQueryable();

            if (revoked.HasValue)
                query = query.Where(x => x.IsRevoked == revoked.Value);

            ViewBag.Revoked = revoked;

            var certificates = await query
                .OrderByDescending(x => x.IssuedAt)
                .ThenByDescending(x => x.Id)
                .Take(500)
                .ToListAsync();

            return View(certificates);
        }

        [HttpGet]
        public IActionResult LicenseSecuritySettings()
        {
            var configuredApiKey = (_configuration["LicenseApi:ApiKey"] ?? string.Empty).Trim();
            var allowQueryApiKey = string.Equals(_configuration["LicenseApi:AllowQueryApiKey"], "true", StringComparison.OrdinalIgnoreCase);
            var privateKeyPath = _configuration["LicenseV2:PrivateKeyPath"];
            if (string.IsNullOrWhiteSpace(privateKeyPath))
                privateKeyPath = Path.Combine("App_Data", "license_v2_private_key.pem");

            var fullPrivateKeyPath = Path.IsPathRooted(privateKeyPath)
                ? privateKeyPath
                : Path.Combine(_environment.ContentRootPath, privateKeyPath);

            ViewBag.ApiKeyConfigured = !string.IsNullOrWhiteSpace(configuredApiKey) && !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configuredApiKey))), "527BC91276A939D9121819124D79FC6CD0CDB78BF985D16777CBE917BFF958B9", StringComparison.Ordinal);
            ViewBag.AllowQueryApiKey = allowQueryApiKey;
            ViewBag.PrivateKeyPath = privateKeyPath;
            ViewBag.PrivateKeyExists = System.IO.File.Exists(fullPrivateKeyPath);
            ViewBag.PublicKey = _offlineLicenseService.GetPublicKeyPem();
            ViewBag.KeyId = _configuration["LicenseV2:KeyId"] ?? "";

            return View();
        }


        [HttpGet]
        public async Task<IActionResult> DemoDownloads()
        {
            var downloads = await _context.DemoDownloads
                .Include(x => x.Product)
                .Include(x => x.User)
                .OrderByDescending(x => x.DownloadedAt)
                .ThenByDescending(x => x.Id)
                .ToListAsync();

            return View(downloads);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DemoDownloadRefreshLocations()
        {
            var downloads = await _context.DemoDownloads
                .Where(x => x.IpAddress != null && x.IpAddress != "")
                .Where(x => x.City == null || x.City == "" || x.GeoLookupAt == null)
                .OrderByDescending(x => x.DownloadedAt)
                .Take(100)
                .ToListAsync();

            var updatedCount = 0;
            foreach (var item in downloads)
            {
                if (!_clientIpService.IsPublicIp(item.IpAddress))
                    continue;

                var location = await _ipGeolocationService.ResolveAsync(item.IpAddress);
                if (location == null)
                    continue;

                item.City = location.City;
                item.Region = location.Region;
                item.Country = location.Country;
                item.GeoLookupAt = DateTime.Now;
                updatedCount++;
            }

            if (updatedCount > 0)
            {
                await _context.SaveChangesAsync();
                TempData["Success"] = $"{updatedCount} demo indirme kaydının şehir bilgisi güncellendi.";
            }
            else
            {
                TempData["Info"] = "Güncellenecek demo şehir bilgisi bulunamadı veya IP lokasyon servisi cevap vermedi.";
            }

            return RedirectToAction(nameof(DemoDownloads));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DemoDownloadRefreshLocation(int id)
        {
            var item = await _context.DemoDownloads.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                TempData["Error"] = "Demo indirme kaydı bulunamadı.";
                return RedirectToAction(nameof(DemoDownloads));
            }

            if (string.IsNullOrWhiteSpace(item.IpAddress) || !_clientIpService.IsPublicIp(item.IpAddress))
            {
                TempData["Warning"] = "Bu kayıtta şehir bulunabilecek public IP yok.";
                return RedirectToAction(nameof(DemoDownloads));
            }

            var location = await _ipGeolocationService.ResolveAsync(item.IpAddress);
            if (location == null)
            {
                TempData["Warning"] = "IP şehir bilgisi alınamadı.";
                return RedirectToAction(nameof(DemoDownloads));
            }

            item.City = location.City;
            item.Region = location.Region;
            item.Country = location.Country;
            item.GeoLookupAt = DateTime.Now;
            await _context.SaveChangesAsync();

            TempData["Success"] = "Demo indirme şehir bilgisi güncellendi.";
            return RedirectToAction(nameof(DemoDownloads));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DemoDownloadDelete(int id)
        {
            var item = await _context.DemoDownloads.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                TempData["Error"] = "Demo indirme kaydı bulunamadı.";
                return RedirectToAction(nameof(DemoDownloads));
            }

            _context.DemoDownloads.Remove(item);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Demo indirme kaydı silindi.";
            return RedirectToAction(nameof(DemoDownloads));
        }

        [HttpGet]
        public async Task<IActionResult> BankTransfers()
        {
            var notifications = await _context.BankTransferNotifications
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.Order)
                .OrderBy(x => x.Status == "Pending" ? 0 : 1)
                .ThenByDescending(x => x.Id)
                .ToListAsync();

            return View(notifications);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BankTransferDelete(int id)
        {
            var notification = await _context.BankTransferNotifications.FirstOrDefaultAsync(x => x.Id == id);
            if (notification == null)
            {
                TempData["Error"] = "Havale bildirimi bulunamadı.";
                return RedirectToAction(nameof(BankTransfers));
            }

            _context.BankTransferNotifications.Remove(notification);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Havale bildirimi silindi.";
            return RedirectToAction(nameof(BankTransfers));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BankTransferStatus(int id, string status, string? adminNote)
        {
            var notification = await _context.BankTransferNotifications
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.Order)
                    .ThenInclude(x => x!.OrderItems)
                        .ThenInclude(x => x.Product)
                .Include(x => x.Order)
                    .ThenInclude(x => x!.Licenses)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (notification == null)
            {
                TempData["Error"] = "Havale bildirimi bulunamadı.";
                return RedirectToAction(nameof(BankTransfers));
            }

            notification.AdminNote = CleanOptional(adminNote);
            notification.ReviewedAt = DateTime.Now;

            if (string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                var approved = await CompleteBankTransferOrderAsync(notification);
                if (!approved)
                    return RedirectToAction(nameof(BankTransfers));

                TempData["Success"] = "Havale bildirimi onaylandı, lisans oluşturuldu.";
            }
            else if (string.Equals(status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                notification.Status = "Rejected";
                if (notification.Order != null)
                {
                    notification.Order.PaymentStatus = "Rejected";
                    notification.Order.OrderStatus = "BankTransferRejected";
                }

                await _context.SaveChangesAsync();
                TempData["Success"] = "Havale bildirimi reddedildi.";
            }
            else
            {
                TempData["Warning"] = "Geçerli bir işlem seçin.";
            }

            return RedirectToAction(nameof(BankTransfers));
        }

        [HttpGet]
        public async Task<IActionResult> Products()
        {
            var products = await _context.Products
                .Include(x => x.Images)
                .Include(x => x.Files)
                .Where(x => !x.IsDeleted)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            var shopierMappings = new List<ShopierProductMapping>();
            try
            {
                shopierMappings = await _context.ShopierProductMappings
                    .AsNoTracking()
                    .Where(x => x.IsActive)
                    .ToListAsync();
            }
            catch (Exception)
            {
                // İlk deploy anında eşleştirme tablosu henüz oluşmadıysa Ürün Yönetimi yine de açılabilsin.
            }

            ViewBag.ShopierMappings = shopierMappings.ToDictionary(x => x.ProductId);
            ViewBag.ShopierConfigured = _shopierService.IsConfigured;

            return View(products);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ShopierProductsSync()
        {
            var result = await _shopierService.SyncProductMappingsAsync(force: true, HttpContext.RequestAborted);
            TempData[result.Success ? "Success" : "Error"] = result.Message;
            return RedirectToAction(nameof(Products));
        }

        [HttpGet]
        public async Task<IActionResult> ProductCategories()
        {
            var categories = await _context.ProductCategories
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Label)
                .ToListAsync();

            return View(categories);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProductCategorySave(int? id, string label, string? key, int sortOrder, bool isActive)
        {
            label = (label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                TempData["Error"] = "Kategori adı zorunludur.";
                return RedirectToAction(nameof(ProductCategories));
            }

            var normalizedKey = ProductCategoryHelper.CreateKey(string.IsNullOrWhiteSpace(key) ? label : key);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                TempData["Error"] = "Kategori için geçerli bir anahtar oluşturulamadı.";
                return RedirectToAction(nameof(ProductCategories));
            }

            var duplicate = await _context.ProductCategories
                .AnyAsync(x => x.Key == normalizedKey && (!id.HasValue || x.Id != id.Value));
            if (duplicate)
            {
                TempData["Error"] = "Bu kategori anahtarı zaten kullanılıyor.";
                return RedirectToAction(nameof(ProductCategories));
            }

            ProductCategory category;
            if (id.HasValue)
            {
                category = await _context.ProductCategories.FirstOrDefaultAsync(x => x.Id == id.Value)
                    ?? throw new InvalidOperationException("Kategori bulunamadı.");

                var oldKey = category.Key;
                if (!string.Equals(oldKey, normalizedKey, StringComparison.OrdinalIgnoreCase))
                {
                    var products = await _context.Products.Where(x => x.Category == oldKey).ToListAsync();
                    foreach (var product in products)
                        product.Category = normalizedKey;
                }

                category.Key = normalizedKey;
                category.Label = label[..Math.Min(label.Length, 120)];
                category.SortOrder = sortOrder;
                category.IsActive = isActive;
                category.UpdatedAt = DateTime.Now;
            }
            else
            {
                category = new ProductCategory
                {
                    Key = normalizedKey,
                    Label = label[..Math.Min(label.Length, 120)],
                    SortOrder = sortOrder,
                    IsActive = isActive,
                    CreatedAt = DateTime.Now
                };
                _context.ProductCategories.Add(category);
            }

            await _context.SaveChangesAsync();
            var categoryQueued = await _translationQueue.QueueCategoryAsync(category, HttpContext.RequestAborted);
            await ReloadProductCategoryOptionsAsync();
            TempData["Success"] = id.HasValue
                ? $"Kategori güncellendi. {categoryQueued} dil çevirisi kuyruğa alındı."
                : $"Yeni kategori eklendi. {categoryQueued} dil çevirisi kuyruğa alındı.";
            return RedirectToAction(nameof(ProductCategories));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProductCategoryDelete(int id)
        {
            var category = await _context.ProductCategories.FirstOrDefaultAsync(x => x.Id == id);
            if (category == null)
                return NotFound();

            if (await _context.Products.AnyAsync(x => !x.IsDeleted && x.Category == category.Key))
            {
                TempData["Error"] = "Bu kategori ürünlerde kullanıldığı için silinemez. Önce ürünlerin kategorisini değiştirin.";
                return RedirectToAction(nameof(ProductCategories));
            }

            _context.ProductCategories.Remove(category);
            await _context.SaveChangesAsync();
            await ReloadProductCategoryOptionsAsync();
            TempData["Success"] = "Kategori silindi.";
            return RedirectToAction(nameof(ProductCategories));
        }

        [HttpGet]
        public IActionResult ProductAdd()
        {
            return View(new Product
            {
                StockQuantity = 999,
                IsActive = true
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxUploadSize)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadSize)]
        public async Task<IActionResult> ProductAdd(ProductFormViewModel model)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    ViewBag.Error = "Ürün adı zorunludur.";
                    return View(new ProductFromForm(model));
                }

                var slug = await CreateUniqueSlugAsync(model.Name);
                var productCode = NormalizeProductCode(string.IsNullOrWhiteSpace(model.ProductCode) ? model.Name : model.ProductCode);
                if (string.IsNullOrWhiteSpace(productCode))
                    productCode = NormalizeProductCode(slug);

                var productCodeExists = await _context.Products.AnyAsync(x => !x.IsDeleted && x.ProductCode == productCode);
                if (productCodeExists)
                {
                    ViewBag.Error = "Bu ürün kodu başka bir üründe kullanılıyor.";
                    return View(new ProductFromForm(model));
                }

                var formStockValue = Request.Form["StockQuantity"].FirstOrDefault();
                var stockQuantity = int.TryParse(formStockValue, out var postedStockQuantity)
                    ? Math.Max(0, postedStockQuantity)
                    : Math.Max(0, model.StockQuantity);

                var postedIsActiveValues = Request.Form["IsActive"];
                var isActive = postedIsActiveValues.Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase));

                var product = new Product
                {
                    Name = model.Name.Trim(),
                    Slug = slug,
                    ProductCode = productCode,
                    Category = ProductCategoryHelper.Normalize(model.Category, model.Name, productCode),
                    Description = ProductContentSanitizer.Sanitize(model.Description),
                    MetaTitle = model.MetaTitle,
                    MetaDescription = model.MetaDescription,
                    YearlyPrice = model.YearlyPrice,
                    LifetimePrice = model.LifetimePrice,
                    StockQuantity = stockQuantity,
                    IsActive = isActive,
                    IsDeleted = false,
                    CreatedAt = DateTime.Now
                };

                _context.Products.Add(product);
                await _context.SaveChangesAsync();

                var uploadedImages = GetUploadedFiles(model.Images, "Images");
                await SaveImagesAsync(product, uploadedImages);
                await SaveProductFileAsync(product, GetUploadedFile(model.DemoFile, "DemoFile"), "Demo", replaceExisting: false);

                await _context.SaveChangesAsync();
                var queuedTranslations = await _translationQueue.QueueProductAsync(product, HttpContext.RequestAborted);
                TempData["Success"] = $"Ürün başarıyla eklendi. {queuedTranslations} aktif dil çevirisi otomatik kuyruğa alındı.";
                return RedirectToAction(nameof(Products));
            }
            catch (Exception ex)
            {
                ViewBag.Error = "Ürün eklenirken hata oluştu: " + ex.Message;
                ViewBag.DebugKeys = string.Join(", ", Request.Form.Keys);
                return View(new ProductFromForm(model));
            }
        }

        [HttpGet]
        public async Task<IActionResult> ProductEdit(int id)
        {
            var product = await _context.Products
                .Include(x => x.Images)
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

            if (product == null)
                return NotFound();

            await CleanMissingProductImagesAsync(product);
            await PrepareProductEditUpdateViewBagAsync(product);

            return View(product);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxUploadSize)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadSize)]
        public async Task<IActionResult> ProductEdit(int id, ProductFormViewModel model)
        {
            var product = await _context.Products
                .Include(x => x.Images)
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);

            if (product == null)
                return NotFound();

            await CleanMissingProductImagesAsync(product);

            try
            {
                var uploadedImages = GetUploadedFiles(model.Images, "Images");

                // Sadece görsel/demo dosyası değiştirildiğinde de ürün güncellensin.
                // Formdaki metin alanları boş gelirse mevcut değerleri koruyoruz.
                if (!string.IsNullOrWhiteSpace(model.Name))
                    product.Name = model.Name.Trim();

                var postedProductCode = NormalizeProductCode(string.IsNullOrWhiteSpace(model.ProductCode) ? product.ProductCode ?? product.Name : model.ProductCode);
                if (string.IsNullOrWhiteSpace(postedProductCode))
                    postedProductCode = NormalizeProductCode(product.Slug ?? product.Name);

                var productCodeExists = await _context.Products.AnyAsync(x => x.Id != product.Id && !x.IsDeleted && x.ProductCode == postedProductCode);
                if (productCodeExists)
                    throw new InvalidOperationException("Bu ürün kodu başka bir üründe kullanılıyor.");

                product.ProductCode = postedProductCode;

                product.Category = ProductCategoryHelper.Normalize(model.Category, product.Name, product.Slug, postedProductCode);
                product.Description = ProductContentSanitizer.Sanitize(model.Description ?? product.Description);
                product.MetaTitle = model.MetaTitle ?? product.MetaTitle;
                product.MetaDescription = model.MetaDescription ?? product.MetaDescription;
                product.YearlyPrice = model.YearlyPrice > 0 ? model.YearlyPrice : product.YearlyPrice;
                product.LifetimePrice = model.LifetimePrice > 0 ? model.LifetimePrice : product.LifetimePrice;

                // Checkbox pasife alınınca tarayıcı bazen sadece dosya alanlarını gönderip bool değeri boş bırakabiliyor.
                // Bu yüzden aktif/pasif ve stok değerini doğrudan Request.Form üzerinden okuyup EF'e özellikle değişti diye işaretliyoruz.
                var formStockValue = Request.Form["StockQuantity"].FirstOrDefault();
                if (int.TryParse(formStockValue, out var postedStockQuantity))
                    product.StockQuantity = postedStockQuantity < 0 ? 0 : postedStockQuantity;
                else
                    product.StockQuantity = model.StockQuantity < 0 ? 0 : model.StockQuantity;

                var postedIsActiveValues = Request.Form["IsActive"];
                product.IsActive = postedIsActiveValues.Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase));

                _context.Entry(product).Property(x => x.StockQuantity).IsModified = true;
                _context.Entry(product).Property(x => x.IsActive).IsModified = true;

                if (string.IsNullOrWhiteSpace(product.Slug))
                    product.Slug = await CreateUniqueSlugAsync(product.Name, product.Id);

                await SaveImagesAsync(product, uploadedImages, makeFirstNewImageMain: uploadedImages.Any(), replaceOldestWhenFull: false);
                await SaveProductFileAsync(product, GetUploadedFile(model.DemoFile, "DemoFile"), "Demo", replaceExisting: true);

                var updateSaved = await SaveProductUpdateFromProductEditAsync(product);
                await _context.SaveChangesAsync();
                var queuedTranslations = await _translationQueue.QueueProductAsync(product, HttpContext.RequestAborted);
                TempData["Success"] = updateSaved
                    ? "Ürün güncellendi ve güncelleme dosyası kaydedildi."
                    : (uploadedImages.Any()
                        ? "Ürün güncellendi ve yeni görsel eklendi."
                        : "Ürün başarıyla güncellendi.");

                return RedirectToAction(nameof(ProductEdit), new { id = product.Id });
            }
            catch (Exception ex)
            {
                ViewBag.Error = "Ürün güncellenirken hata oluştu: " + ex.Message;
                product = await _context.Products
                    .Include(x => x.Images)
                    .Include(x => x.Files)
                    .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted) ?? product;
                await PrepareProductEditUpdateViewBagAsync(product);
                return View(product);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProductImageDelete(int id)
        {
            var image = await _context.ProductImages
                .Include(x => x.Product)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (image == null)
                return NotFound();

            var productId = image.ProductId;
            DeletePhysicalFile(image.ImagePath);
            _context.ProductImages.Remove(image);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Ürün görseli silindi.";
            return RedirectToAction(nameof(ProductEdit), new { id = productId });
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProductFileDelete(int id)
        {
            var file = await _context.ProductFiles.FirstOrDefaultAsync(x => x.Id == id);
            if (file == null || !string.Equals(file.FileType, "Demo", StringComparison.OrdinalIgnoreCase))
                return NotFound();

            var productId = file.ProductId;
            DeletePhysicalFile(file.FilePath);
            _context.ProductFiles.Remove(file);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Demo program dosyası silindi.";
            return RedirectToAction(nameof(ProductEdit), new { id = productId });
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProductImageSetMain(int id)
        {
            var image = await _context.ProductImages
                .Include(x => x.Product)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (image == null)
                return NotFound();

            var productId = image.ProductId;
            var productImages = await _context.ProductImages
                .Where(x => x.ProductId == productId)
                .ToListAsync();

            foreach (var productImage in productImages)
                productImage.IsMain = productImage.Id == image.Id;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Kapak görseli güncellendi.";
            return RedirectToAction(nameof(ProductEdit), new { id = productId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProductDelete(int id)
        {
            var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (product == null)
                return NotFound();

            product.IsDeleted = true;
            product.IsActive = false;
            await _context.SaveChangesAsync();

            TempData["Success"] = "Ürün pasife alındı.";
            return RedirectToAction(nameof(Products));
        }


        [HttpGet]
        public async Task<IActionResult> ManualSale(int? productId = null, int? userId = null)
        {
            await PrepareManualSaleListsAsync(productId, userId);

            return View(new ManualSaleViewModel
            {
                ProductId = productId ?? 0,
                UserId = userId ?? 0,
                LicenseType = "Lifetime",
                CreateLicense = true,
                SaleDate = DateTime.Today
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ManualSale(ManualSaleViewModel model)
        {
            model.LicenseType = NormalizeLicenseType(model.LicenseType);
            model.SaleDate = model.SaleDate == default ? DateTime.Today : model.SaleDate.Date;

            var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == model.ProductId && !x.IsDeleted && x.IsActive);
            if (product == null)
                ModelState.AddModelError(nameof(model.ProductId), "Geçerli ve aktif bir ürün seçin.");

            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == model.UserId && !x.IsDeleted && x.IsActive);
            if (user == null)
                ModelState.AddModelError(nameof(model.UserId), "Geçerli ve aktif bir üye seçin.");

            if (model.Amount <= 0)
                ModelState.AddModelError(nameof(model.Amount), "Alınan ücret 0'dan büyük olmalıdır.");

            if (!ModelState.IsValid || product == null || user == null)
            {
                await PrepareManualSaleListsAsync(model.ProductId, model.UserId);
                return View(model);
            }

            var saleTime = model.SaleDate.Date.Add(DateTime.Now.TimeOfDay);
            var order = new Order
            {
                UserId = user.Id,
                OrderNumber = await GenerateUniqueManualOrderNumberAsync(),
                TotalAmount = model.Amount,
                PaymentStatus = "Paid",
                OrderStatus = "Completed",
                CreatedAt = saleTime
            };

            order.OrderItems.Add(new OrderItem
            {
                ProductId = product.Id,
                Quantity = 1,
                UnitPrice = model.Amount,
                LicenseType = model.LicenseType
            });

            if (product.StockQuantity > 0)
                product.StockQuantity -= 1;

            _context.Orders.Add(order);

            License? license = null;
            if (model.CreateLicense)
            {
                license = new License
                {
                    ProductId = product.Id,
                    UserId = user.Id,
                    Order = order,
                    LicenseKey = await GenerateUniqueLicenseKeyAsync(),
                    LicenseType = model.LicenseType,
                    ProductCode = GetProductCodeForLicense(product),
                    LicenseStatus = "Active",
                    MaxDeviceCount = 1,
                    OfflineAllowed = true,
                    StartDate = model.SaleDate,
                    EndDate = model.LicenseType == "Lifetime" ? null : model.SaleDate.AddYears(1),
                    MachineId = null,
                    IsActive = true,
                    CreatedAt = saleTime
                };

                _context.Licenses.Add(license);
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = model.CreateLicense && license != null
                ? $"Manuel satış kaydedildi. Sipariş: {order.OrderNumber} - Lisans: {license.LicenseKey}"
                : $"Manuel satış kaydedildi. Sipariş: {order.OrderNumber}";

            return RedirectToAction(nameof(Orders));
        }

        [HttpGet]
        public async Task<IActionResult> ManualLicense(int? productId = null, int? userId = null)
        {
            await PrepareManualLicenseListsAsync(productId, userId);

            return View(new ManualLicenseViewModel
            {
                ProductId = productId ?? 0,
                UserId = userId ?? 0,
                LicenseType = "Yearly",
                StartDate = DateTime.Today,
                IsActive = true
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ManualLicense(ManualLicenseViewModel model)
        {
            model.LicenseType = NormalizeLicenseType(model.LicenseType);
            model.StartDate = model.StartDate == default ? DateTime.Today : model.StartDate.Date;
            model.IsActive = true;

            var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == model.ProductId && !x.IsDeleted);
            if (product == null)
                ModelState.AddModelError(nameof(model.ProductId), "Geçerli bir ürün seçin.");

            var userExists = await _context.Users.AnyAsync(x => x.Id == model.UserId && !x.IsDeleted && x.IsActive);
            if (!userExists)
                ModelState.AddModelError(nameof(model.UserId), "Geçerli ve aktif bir üye seçin.");

            if (!ModelState.IsValid || product == null)
            {
                await PrepareManualLicenseListsAsync(model.ProductId, model.UserId);
                return View(model);
            }

            var license = new License
            {
                ProductId = model.ProductId,
                UserId = model.UserId,
                OrderId = null,
                LicenseKey = await GenerateUniqueLicenseKeyAsync(),
                LicenseType = model.LicenseType,
                ProductCode = GetProductCodeForLicense(product),
                LicenseStatus = "Active",
                MaxDeviceCount = 1,
                OfflineAllowed = true,
                StartDate = model.StartDate,
                EndDate = model.LicenseType == "Lifetime" ? null : model.StartDate.AddYears(1),
                MachineId = null,
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _context.Licenses.Add(license);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Manuel lisans oluşturuldu: {license.LicenseKey}";
            return RedirectToAction(nameof(Licenses));
        }

        [HttpGet]
        public IActionResult LicenseAdd(int? productId = null, int? userId = null)
        {
            return RedirectToAction(nameof(ManualLicense), new { productId, userId });
        }

        [HttpGet]
        public async Task<IActionResult> LicenseEdit(int id)
        {
            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.Devices)
                .Include(x => x.CheckLogs)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (license == null)
                return NotFound();

            await PrepareLicenseFormListsAsync();
            return View(license);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseEdit(int id, License model)
        {
            var license = await _context.Licenses
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id);
            if (license == null)
                return NotFound();

            var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == model.ProductId && !x.IsDeleted);
            var userExists = await _context.Users.AnyAsync(x => x.Id == model.UserId && !x.IsDeleted && x.IsActive);

            if (product == null || !userExists || string.IsNullOrWhiteSpace(model.LicenseKey))
            {
                ViewBag.Error = "Ürün, üye ve lisans anahtarı zorunludur.";
                await PrepareLicenseFormListsAsync();
                return View(model);
            }

            var duplicateKey = await _context.Licenses.AnyAsync(x => x.Id != id && x.LicenseKey == model.LicenseKey.Trim());
            if (duplicateKey)
            {
                ViewBag.Error = "Bu lisans anahtarı başka bir kayıtta kullanılıyor.";
                await PrepareLicenseFormListsAsync();
                return View(model);
            }

            license.ProductId = model.ProductId;
            license.UserId = model.UserId;
            license.OrderId = model.OrderId;
            license.LicenseKey = model.LicenseKey.Trim().ToUpperInvariant();
            license.LicenseType = NormalizeLicenseType(model.LicenseType);
            license.StartDate = model.StartDate == default ? DateTime.Today : model.StartDate.Date;
            license.EndDate = license.LicenseType == "Lifetime"
                ? null
                : model.EndDate?.Date;
            license.MachineId = string.IsNullOrWhiteSpace(model.MachineId) ? null : model.MachineId.Trim();
            license.ProductCode = string.IsNullOrWhiteSpace(model.ProductCode)
                ? GetProductCodeForLicense(product)
                : NormalizeProductCode(model.ProductCode);
            LicenseTermService.NormalizeTerm(license, DateTime.Now);
            license.LicenseStatus = string.IsNullOrWhiteSpace(model.LicenseStatus) ? (model.IsActive ? "Active" : "Suspended") : model.LicenseStatus.Trim();
            license.MaxDeviceCount = model.MaxDeviceCount <= 0 ? 1 : model.MaxDeviceCount;
            license.OfflineAllowed = model.OfflineAllowed;
            license.RevokedReason = string.IsNullOrWhiteSpace(model.RevokedReason) ? null : model.RevokedReason.Trim();
            license.IsActive = model.IsActive;
            if (!license.IsActive && license.RevokedAt == null)
                license.RevokedAt = DateTime.Now;
            if (license.IsActive && string.Equals(license.LicenseStatus, "Active", StringComparison.OrdinalIgnoreCase))
                license.RevokedAt = null;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Lisans güncellendi.";
            return RedirectToAction(nameof(LicenseEdit), new { id = license.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseClearLogs(int id)
        {
            var licenseExists = await _context.Licenses.AnyAsync(x => x.Id == id);
            if (!licenseExists)
                return NotFound();

            var logs = await _context.LicenseCheckLogs
                .Where(x => x.LicenseId == id)
                .ToListAsync();

            if (logs.Count > 0)
            {
                _context.LicenseCheckLogs.RemoveRange(logs);
                await _context.SaveChangesAsync();
            }

            TempData["Success"] = logs.Count > 0
                ? $"{logs.Count} lisans kontrol logu temizlendi."
                : "Temizlenecek lisans kontrol logu yok.";

            return RedirectToAction(nameof(LicenseEdit), new { id });
        }


        [HttpGet]
        public async Task<IActionResult> LicenseOfflineFile(int id, string? machineId = null)
        {
            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (license == null)
                return NotFound();

            if (!license.IsActive || (license.EndDate.HasValue && license.EndDate.Value.Date < DateTime.Today))
            {
                TempData["Error"] = "Bu lisans aktif değil.";
                return RedirectToAction(nameof(Licenses));
            }

            if (!license.OfflineAllowed)
            {
                TempData["Error"] = "Bu lisans için offline etkinleştirme kapalı.";
                return RedirectToAction(nameof(Licenses));
            }

            var requestedMachineId = (machineId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedMachineId))
                requestedMachineId = (license.MachineId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(requestedMachineId))
            {
                TempData["Error"] = "Offline lisans dosyası için müşteriden gelen Makine Kodu girilmeli veya lisans önce online bir cihaza eşleşmeli.";
                return RedirectToAction(nameof(LicenseEdit), new { id = license.Id });
            }

            // Hiç internetsiz aktivasyon senaryosunda müşteri online gelemediği için MachineId boş olabilir.
            // Admin müşterinin gönderdiği makine koduyla offline dosya üretince lisansı bu cihaza bağla.
            if (string.IsNullOrWhiteSpace(license.MachineId))
                license.MachineId = requestedMachineId;

            var productCode = string.IsNullOrWhiteSpace(license.ProductCode)
                ? NormalizeProductCode(license.Product?.Slug ?? license.Product?.Name ?? "NSX")
                : NormalizeProductCode(license.ProductCode);

            var certificate = license.OfflineCertificates
                .Where(x => !x.IsRevoked && x.MachineId == requestedMachineId && x.ProductCode == productCode)
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (certificate == null)
            {
                var created = _offlineLicenseService.CreateCertificate(license, productCode, requestedMachineId);
                certificate = new OfflineLicenseCertificate
                {
                    LicenseId = license.Id,
                    CertificateId = created.CertificateId,
                    ProductCode = productCode,
                    MachineId = requestedMachineId,
                    PayloadJson = created.PayloadJson,
                    Signature = created.Signature,
                    OfflineCode = created.OfflineCode,
                    IssuedAt = DateTime.Now,
                    ExpiresAt = license.EndDate
                };

                _context.OfflineLicenseCertificates.Add(certificate);
                await _context.SaveChangesAsync();
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(certificate.OfflineCode);
            var fileName = $"{productCode}-{certificate.CertificateId}.nsxlic";
            return File(bytes, "application/octet-stream", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseRecoveryCodeGenerate(int id, string? machineId = null)
        {
            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (license == null)
                return NotFound();

            var requestedMachineId = (machineId ?? string.Empty).Trim();
            var currentMachineId = (license.MachineId ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(currentMachineId) &&
                !string.IsNullOrWhiteSpace(requestedMachineId) &&
                !string.Equals(currentMachineId, requestedMachineId, StringComparison.Ordinal))
            {
                TempData["Error"] = "Bu lisans zaten farklı bir makine koduyla eşleşmiş. Önce cihazı sıfırlayın veya doğru makine kodunu kullanın.";
                return RedirectToAction(nameof(LicenseEdit), new { id });
            }

            var certificate = EnsureRecoveryCertificate(license, string.IsNullOrWhiteSpace(requestedMachineId) ? null : requestedMachineId);
            if (certificate == null)
            {
                TempData["Error"] = "Kurtarma kodu üretilemedi. Lisans aktif olmalı, offline kullanım açık olmalı ve makine kodu bulunmalı.";
                return RedirectToAction(nameof(LicenseEdit), new { id });
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = certificate.Id == 0
                ? "Kurtarma lisans kodu üretildi."
                : "Kurtarma lisans kodu hazır.";

            return RedirectToAction(nameof(LicenseEdit), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseRecoveryCodeEmail(int id)
        {
            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (license == null)
                return NotFound();

            if (license.User == null || string.IsNullOrWhiteSpace(license.User.Email))
            {
                TempData["Error"] = "Bu lisansın bağlı olduğu üyenin e-posta adresi bulunamadı.";
                return RedirectToAction(nameof(LicenseEdit), new { id });
            }

            var certificate = EnsureRecoveryCertificate(license);
            if (certificate == null)
            {
                TempData["Error"] = "Kurtarma kodu maili gönderilemedi. Önce lisansın makine kodu oluşmalı veya admin panelden makine kodu girilerek kurtarma kodu üretilmeli.";
                return RedirectToAction(nameof(LicenseEdit), new { id });
            }

            if (certificate.Id == 0)
                await _context.SaveChangesAsync();

            try
            {
                await _emailSender.SendEmailAsync(
                    license.User.Email,
                    "NSX Lisans Kurtarma Kodunuz",
                    EmailTemplates.LicenseRecoveryCode(license, certificate.OfflineCode, GetPublicSiteUrl()));

                TempData["Success"] = "Kurtarma lisans kodu müşteriye e-posta olarak gönderildi.";
            }
            catch
            {
                TempData["Error"] = "Kurtarma kodu e-postası gönderilemedi. SMTP ayarlarını kontrol edin.";
            }

            return RedirectToAction(nameof(LicenseEdit), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseMakePrimaryDevice(int id)
        {
            var device = await _context.LicenseDevices
                .Include(x => x.License)!
                    .ThenInclude(x => x!.Devices)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (device == null || device.License == null)
                return NotFound();

            var now = DateTime.Now;
            var license = device.License;
            device.IsBlocked = false;
            device.IsRejected = false;
            device.DeviceStatus = "Active";
            device.BlockReason = null;
            device.LastSeenAt = now;

            license.MachineId = device.MachineId;
            license.LastCheckedAt = now;
            license.LicenseStatus = "Active";
            license.IsActive = true;

            if ((license.MaxDeviceCount <= 0 ? 1 : license.MaxDeviceCount) <= 1)
            {
                foreach (var other in license.Devices.Where(x => x.Id != device.Id))
                {
                    other.IsBlocked = true;
                    other.IsRejected = true;
                    other.DeviceStatus = "ReplacedByAdmin";
                    other.BlockReason = "Admin yeni ana cihaz seçti. Tek cihaz lisansında eski cihaz pasife alındı.";
                }
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Seçilen cihaz ana cihaz yapıldı. Tek cihaz lisansında eski cihazlar pasife alındı.";
            return RedirectToAction(nameof(LicenseEdit), new { id = device.LicenseId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseResetDevice(int id)
        {
            var license = await _context.Licenses
                .Include(x => x.Devices)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (license == null)
                return NotFound();

            license.MachineId = null;
            foreach (var device in license.Devices)
            {
                device.IsBlocked = true;
                device.IsRejected = false;
                device.DeviceStatus = "ResetByAdmin";
                device.BlockReason = "Admin panelden cihaz eşleşmesi sıfırlandı.";
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Lisans cihaz eşleşmesi sıfırlandı. Eski cihazlar pasife alındı.";
            return RedirectToAction(nameof(LicenseEdit), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseDeviceBlock(int id, string? reason = null)
        {
            var device = await _context.LicenseDevices
                .Include(x => x.License)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (device == null)
                return NotFound();

            device.IsBlocked = true;
            device.DeviceStatus = "Blocked";
            device.BlockReason = string.IsNullOrWhiteSpace(reason) ? "Admin panelden engellendi." : reason.Trim();

            var productCode = NormalizeProductCode(device.ProductCode ?? device.License?.ProductCode);
            var existingGlobalBlock = await _context.BlockedDevices.FirstOrDefaultAsync(x =>
                x.MachineId == device.MachineId &&
                x.ProductCode == productCode &&
                x.IsActive);

            if (existingGlobalBlock == null)
            {
                _context.BlockedDevices.Add(new BlockedDevice
                {
                    MachineId = device.MachineId,
                    ProductCode = productCode,
                    Reason = device.BlockReason,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                });
            }
            else
            {
                existingGlobalBlock.Reason = device.BlockReason;
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Cihaz engellendi. Bu ürün koduyla V2 lisans kontrolünde reddedilecek.";
            return RedirectToAction(nameof(LicenseEdit), new { id = device.LicenseId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseDeviceUnblock(int id)
        {
            var device = await _context.LicenseDevices
                .Include(x => x.License)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (device == null)
                return NotFound();

            device.IsBlocked = false;
            device.IsRejected = false;
            device.DeviceStatus = "Active";
            device.BlockReason = null;

            var productCode = NormalizeProductCode(device.ProductCode ?? device.License?.ProductCode);
            var globalBlocks = await _context.BlockedDevices
                .Where(x => x.IsActive && x.MachineId == device.MachineId && x.ProductCode == productCode)
                .ToListAsync();

            foreach (var block in globalBlocks)
            {
                block.IsActive = false;
                block.DisabledAt = DateTime.Now;
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Cihaz engeli kaldırıldı.";
            return RedirectToAction(nameof(LicenseEdit), new { id = device.LicenseId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BlockedDeviceDisable(int id)
        {
            var blocked = await _context.BlockedDevices.FirstOrDefaultAsync(x => x.Id == id);
            if (blocked == null)
                return NotFound();

            blocked.IsActive = false;
            blocked.DisabledAt = DateTime.Now;

            var relatedDevices = await _context.LicenseDevices
                .Where(x => x.MachineId == blocked.MachineId && x.ProductCode == blocked.ProductCode)
                .ToListAsync();

            foreach (var device in relatedDevices)
            {
                device.IsBlocked = false;
                device.BlockReason = null;
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Global cihaz engeli kaldırıldı.";
            return RedirectToAction(nameof(BlockedDevices));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OfflineCertificateRevoke(int id, string? reason = null)
        {
            var certificate = await _context.OfflineLicenseCertificates
                .Include(x => x.License)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (certificate == null)
                return NotFound();

            certificate.IsRevoked = true;
            certificate.RevokedAt = DateTime.Now;
            certificate.RevokedReason = string.IsNullOrWhiteSpace(reason) ? "Admin panelden iptal edildi." : reason.Trim();

            await _context.SaveChangesAsync();
            TempData["Success"] = "Offline lisans sertifikası iptal edildi.";
            return certificate.LicenseId > 0
                ? RedirectToAction(nameof(LicenseEdit), new { id = certificate.LicenseId })
                : RedirectToAction(nameof(OfflineLicenseCertificates));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OfflineCertificateRestore(int id)
        {
            var certificate = await _context.OfflineLicenseCertificates.FirstOrDefaultAsync(x => x.Id == id);
            if (certificate == null)
                return NotFound();

            certificate.IsRevoked = false;
            certificate.RevokedAt = null;
            certificate.RevokedReason = null;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Offline lisans sertifikası tekrar aktif edildi.";
            return RedirectToAction(nameof(OfflineLicenseCertificates));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseRevokeAllOfflineCertificates(int id, string? reason = null)
        {
            var license = await _context.Licenses
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (license == null)
                return NotFound();

            license.OfflineAllowed = false;
            foreach (var certificate in license.OfflineCertificates.Where(x => !x.IsRevoked))
            {
                certificate.IsRevoked = true;
                certificate.RevokedAt = DateTime.Now;
                certificate.RevokedReason = string.IsNullOrWhiteSpace(reason) ? "Lisans için offline kullanım kapatıldı." : reason.Trim();
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = "Bu lisans için offline kullanım kapatıldı ve mevcut sertifikalar iptal edildi.";
            return RedirectToAction(nameof(LicenseEdit), new { id });
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LicenseDelete(int id)
        {
            var license = await _context.Licenses.FirstOrDefaultAsync(x => x.Id == id);
            if (license == null)
                return NotFound();

            _context.Licenses.Remove(license);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Lisans silindi.";
            return RedirectToAction(nameof(Licenses));
        }

        private async Task PrepareManualSaleListsAsync(int? selectedProductId = null, int? selectedUserId = null)
        {
            ViewBag.Products = await _context.Products
                .Where(x => !x.IsDeleted && x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync();

            ViewBag.Users = await _context.Users
                .Where(x => !x.IsDeleted && x.IsActive && x.Role == "User")
                .OrderBy(x => x.FullName)
                .ThenBy(x => x.Email)
                .ToListAsync();

            ViewBag.SelectedProductId = selectedProductId;
            ViewBag.SelectedUserId = selectedUserId;
        }

        private async Task PrepareManualLicenseListsAsync(int? selectedProductId = null, int? selectedUserId = null)
        {
            ViewBag.Products = await _context.Products
                .Where(x => !x.IsDeleted && x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync();

            ViewBag.Users = await _context.Users
                .Where(x => !x.IsDeleted && x.IsActive)
                .OrderBy(x => x.FullName)
                .ThenBy(x => x.Email)
                .ToListAsync();

            ViewBag.SelectedProductId = selectedProductId;
            ViewBag.SelectedUserId = selectedUserId;
        }

        private async Task PrepareLicenseFormListsAsync()
        {
            ViewBag.Products = await _context.Products
                .Where(x => !x.IsDeleted)
                .OrderBy(x => x.Name)
                .ToListAsync();

            ViewBag.Users = await _context.Users
                .Where(x => !x.IsDeleted)
                .OrderBy(x => x.FullName)
                .ThenBy(x => x.Email)
                .ToListAsync();
        }

        private static bool ContainsInvariant(string? source, string query)
        {
            return !string.IsNullOrWhiteSpace(source)
                && source.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeShopierLicenseLabel(string? selection)
        {
            var value = (selection ?? string.Empty).Trim();
            if (value.Contains("sınırsız", StringComparison.OrdinalIgnoreCase)
                || value.Contains("sinirsiz", StringComparison.OrdinalIgnoreCase)
                || value.Contains("lifetime", StringComparison.OrdinalIgnoreCase)
                || value.Contains("unlimited", StringComparison.OrdinalIgnoreCase))
                return "Sınırsız";

            if (value.Contains("yıllık", StringComparison.OrdinalIgnoreCase)
                || value.Contains("yillik", StringComparison.OrdinalIgnoreCase)
                || value.Contains("year", StringComparison.OrdinalIgnoreCase))
                return "Yıllık";

            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private async Task<string> GenerateUniqueManualOrderNumberAsync()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var suffix = RandomNumberGenerator.GetInt32(100, 999);
                var orderNumber = $"NSX-MAN-{DateTime.Now:yyyyMMddHHmmss}-{suffix}";
                var exists = await _context.Orders.AnyAsync(x => x.OrderNumber == orderNumber);
                if (!exists)
                    return orderNumber;
            }

            return $"NSX-MAN-{DateTime.Now:yyyyMMddHHmmssffff}";
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

        private async Task<bool> CompleteBankTransferOrderAsync(BankTransferNotification notification)
        {
            if (notification.Status == "Approved")
            {
                TempData["Warning"] = "Bu havale bildirimi daha önce onaylanmış.";
                return false;
            }

            var order = notification.Order;
            if (order == null)
            {
                TempData["Error"] = "Bu bildirime bağlı sipariş bulunamadı.";
                return false;
            }

            if (!order.OrderItems.Any())
            {
                TempData["Error"] = "Sipariş içinde ürün bulunamadı.";
                return false;
            }

            foreach (var item in order.OrderItems)
            {
                if (item.Product == null || item.Product.StockQuantity < item.Quantity)
                {
                    notification.Status = "StockProblem";
                    order.PaymentStatus = "StockProblem";
                    order.OrderStatus = "PaymentReceivedStockProblem";
                    await _context.SaveChangesAsync();
                    TempData["Error"] = "Ürün stoğu yetersiz olduğu için lisans oluşturulamadı.";
                    return false;
                }
            }

            order.PaymentStatus = "Paid";
            order.OrderStatus = "Completed";
            notification.Status = "Approved";

            foreach (var item in order.OrderItems)
            {
                item.Product!.StockQuantity -= item.Quantity;

                var normalizedLicenseType = NormalizeLicenseType(item.LicenseType);
                var existingCount = order.Licenses.Count(x => x.ProductId == item.ProductId && x.LicenseType == normalizedLicenseType);
                var missingCount = Math.Max(0, item.Quantity - existingCount);

                for (var i = 0; i < missingCount; i++)
                {
                    _context.Licenses.Add(new License
                    {
                        UserId = order.UserId,
                        ProductId = item.ProductId,
                        OrderId = order.Id,
                        LicenseKey = await GenerateUniqueLicenseKeyAsync(),
                        LicenseType = normalizedLicenseType,
                        ProductCode = GetProductCodeForLicense(item.Product),
                        LicenseStatus = "Active",
                        MaxDeviceCount = 1,
                        OfflineAllowed = true,
                        StartDate = DateTime.Now,
                        EndDate = normalizedLicenseType == "Lifetime" ? null : DateTime.Now.AddYears(1),
                        IsActive = true,
                        CreatedAt = DateTime.Now
                    });
                }
            }

            await _context.SaveChangesAsync();
            await SendBankTransferApprovedEmailSafe(order.Id);
            return true;
        }

        private async Task SendBankTransferApprovedEmailSafe(int orderId)
        {
            try
            {
                var order = await _context.Orders
                    .Include(x => x.User)
                    .Include(x => x.OrderItems)
                        .ThenInclude(x => x.Product)
                    .Include(x => x.Licenses)
                        .ThenInclude(x => x.Product)
                    .FirstOrDefaultAsync(x => x.Id == orderId);

                if (order?.User == null || string.IsNullOrWhiteSpace(order.User.Email))
                    return;

                var siteUrl = $"{Request.Scheme}://{Request.Host}";
                var html = EmailTemplates.OrderCompleted(order, siteUrl);
                var subject = $"NSX Yazılım | Havale onaylandı, lisansınız hazır ({order.OrderNumber})";

                await _emailSender.SendEmailAsync(order.User.Email, subject, html);
            }
            catch
            {
                TempData["Warning"] = "Lisans oluşturuldu ancak bilgilendirme e-postası gönderilemedi.";
            }
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

        private static string NormalizeLicenseType(string? licenseType)
        {
            return string.Equals(licenseType, "Lifetime", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(licenseType, "Unlimited", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(licenseType, "Sınırsız", StringComparison.OrdinalIgnoreCase)
                ? "Lifetime"
                : "Yearly";
        }

        private List<IFormFile> GetUploadedFiles(List<IFormFile>? modelFiles, string formKey)
        {
            var files = new List<IFormFile>();

            if (modelFiles != null)
                files.AddRange(modelFiles.Where(x => x != null && x.Length > 0));

            files.AddRange(Request.Form.Files
                .Where(x => string.Equals(x.Name, formKey, StringComparison.OrdinalIgnoreCase) && x.Length > 0));

            return files
                .GroupBy(x => x.FileName + "|" + x.Length)
                .Select(x => x.First())
                .ToList();
        }

        private IFormFile? GetUploadedFile(IFormFile? modelFile, string formKey)
        {
            if (modelFile != null && modelFile.Length > 0)
                return modelFile;

            return Request.Form.Files
                .FirstOrDefault(x => string.Equals(x.Name, formKey, StringComparison.OrdinalIgnoreCase) && x.Length > 0);
        }

        private async Task SaveImagesAsync(Product product, List<IFormFile>? images, bool makeFirstNewImageMain = false, bool replaceOldestWhenFull = false)
        {
            if (images == null || !images.Any())
                return;

            product.Images ??= new List<ProductImage>();
            await CleanMissingProductImagesAsync(product);

            var imageDir = Path.Combine(_environment.WebRootPath, "uploads", "products", "images");
            Directory.CreateDirectory(imageDir);

            product.Images = product.Images
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ImagePath))
                .OrderByDescending(x => x.IsMain)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .Take(MaxProductImageCount)
                .ToList();

            var currentCount = product.Images.Count;
            var remainingSlots = Math.Max(0, MaxProductImageCount - currentCount);

            var candidateFiles = images
                .Where(x => x != null && x.Length > 0)
                .Take(MaxProductImageCount)
                .ToList();

            if (!candidateFiles.Any())
                return;

            if (remainingSlots <= 0)
                throw new InvalidOperationException($"Bu ürün için zaten {MaxProductImageCount} görsel var. Yeni görsel eklemek için önce eski bir görsel silin.");

            if (candidateFiles.Count > remainingSlots)
                throw new InvalidOperationException($"Bu ürün için en fazla {MaxProductImageCount} görsel olabilir. Şu an {currentCount} görsel var; en fazla {remainingSlots} yeni görsel ekleyebilirsin.");

            var savedFiles = new List<(string FullPath, string FileName, IFormFile SourceFile)>();

            try
            {
                foreach (var file in candidateFiles)
                {
                    var extension = Path.GetExtension(file.FileName);
                    if (string.IsNullOrWhiteSpace(extension))
                        extension = ".jpg";

                    extension = extension.ToLowerInvariant();
                    var allowedImageExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".jfif" };
                    if (!allowedImageExtensions.Contains(extension))
                        throw new InvalidOperationException($"Görsel formatı desteklenmiyor: {file.FileName}. Lütfen JPG, PNG veya WEBP yükle.");

                    if (!IsBrowserSafeImage(file, extension))
                        throw new InvalidOperationException($"Görsel okunabilir web görseli değil veya bozuk: {file.FileName}. Lütfen JPG/PNG/WEBP olarak yeniden kaydedip yükle.");

                    var safeExtension = extension == ".jfif" ? ".jpg" : extension;
                    var fileName = $"{Guid.NewGuid():N}{safeExtension}";
                    var fullPath = Path.Combine(imageDir, fileName);

                    await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await file.CopyToAsync(stream);
                        await stream.FlushAsync();
                    }

                    EnsureUploadedFileCompleted(fullPath, file.Length, file.FileName);
                    try
                    {
                        await ProductThumbnailGenerator.GenerateAllAsync(fullPath, _environment.WebRootPath);
                    }
                    catch
                    {
                        // Thumbnail üretimi ürün yüklemesini engellemez; eksik boyut ilk istekte güvenli fallback ile üretilir.
                    }
                    savedFiles.Add((fullPath, fileName, file));
                }

                var firstNewImage = true;
                foreach (var saved in savedFiles)
                {
                    if (makeFirstNewImageMain && firstNewImage)
                    {
                        foreach (var existingImage in product.Images)
                            existingImage.IsMain = false;
                    }

                    var productImage = new ProductImage
                    {
                        ProductId = product.Id,
                        ImagePath = $"/uploads/products/images/{saved.FileName}",
                        SortOrder = product.Images.Count,
                        IsMain = (makeFirstNewImageMain && firstNewImage) || product.Images.Count == 0
                    };

                    _context.ProductImages.Add(productImage);
                    product.Images.Add(productImage);
                    firstNewImage = false;
                }
            }
            catch
            {
                foreach (var saved in savedFiles)
                    SafeDeletePhysicalFile(saved.FullPath);

                throw;
            }
        }

        private bool IsBrowserSafeImage(IFormFile file, string extension)
        {
            try
            {
                Span<byte> buffer = stackalloc byte[16];
                using var stream = file.OpenReadStream();
                var read = stream.Read(buffer);
                if (read < 4)
                    return false;

                var b = buffer.ToArray();

                if ((extension == ".jpg" || extension == ".jpeg" || extension == ".jfif") && b[0] == 0xFF && b[1] == 0xD8)
                    return true;

                if (extension == ".png" && read >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
                    return true;

                if (extension == ".gif" && read >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)
                    return true;

                if (extension == ".bmp" && b[0] == 0x42 && b[1] == 0x4D)
                    return true;

                if (extension == ".webp" && read >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task CleanMissingProductImagesAsync(Product product)
        {
            if (product.Images == null || !product.Images.Any())
                return;

            var missing = product.Images
                .Where(x => !string.IsNullOrWhiteSpace(x.ImagePath) && (!PhysicalFileExists(x.ImagePath) || !IsBrowserSafePhysicalImage(x.ImagePath)))
                .ToList();

            if (!missing.Any())
                return;

            foreach (var item in missing)
            {
                _context.ProductImages.Remove(item);
                product.Images.Remove(item);
            }

            await _context.SaveChangesAsync();
        }

        private bool IsBrowserSafePhysicalImage(string? webPath)
        {
            var fullPath = GetPhysicalPath(webPath);
            if (string.IsNullOrWhiteSpace(fullPath) || !System.IO.File.Exists(fullPath))
                return false;

            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            try
            {
                using var fileStream = System.IO.File.OpenRead(fullPath);
                Span<byte> buffer = stackalloc byte[16];
                var read = fileStream.Read(buffer);
                if (read < 4)
                    return false;

                var b = buffer.ToArray();
                if ((extension == ".jpg" || extension == ".jpeg" || extension == ".jfif") && b[0] == 0xFF && b[1] == 0xD8)
                    return true;
                if (extension == ".png" && read >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
                    return true;
                if (extension == ".gif" && read >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)
                    return true;
                if (extension == ".bmp" && b[0] == 0x42 && b[1] == 0x4D)
                    return true;
                if (extension == ".webp" && read >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool PhysicalFileExists(string? webPath)
        {
            var fullPath = GetPhysicalPath(webPath);
            return !string.IsNullOrWhiteSpace(fullPath) && System.IO.File.Exists(fullPath);
        }

        private string GetSupportProgramPath()
        {
            return Path.Combine(_environment.ContentRootPath, "App_Data", "SupportProgram", SupportProgramFileName);
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
            if (bytes >= 1024L * 1024)
                return $"{bytes / (1024d * 1024):0.00} MB";
            if (bytes >= 1024L)
                return $"{bytes / 1024d:0.00} KB";
            return $"{bytes} B";
        }

        private string? GetPhysicalPath(string? webPath)
        {
            if (string.IsNullOrWhiteSpace(webPath))
                return null;

            var cleanPath = webPath.Split('?', '#')[0].TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return Path.Combine(_environment.WebRootPath, cleanPath);
        }

        private static void SafeDeletePhysicalFile(string fullPath)
        {
            try
            {
                if (System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
            }
            catch
            {
                // Upload rollback temizliği başarısız olursa kullanıcı akışını bozma.
            }
        }

        private async Task SaveProductFileAsync(Product product, IFormFile? file, string fileType, bool replaceExisting)
        {
            if (file == null || file.Length <= 0)
                return;

            ValidateProgramPackage(file);

            var fileDir = Path.Combine(_environment.WebRootPath, "uploads", "products", "files");
            Directory.CreateDirectory(fileDir);

            if (replaceExisting)
            {
                var existingFiles = product.Files
                    .Where(x => string.Equals(x.FileType, fileType, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var existing in existingFiles)
                {
                    DeletePhysicalFile(existing.FilePath);
                    _context.ProductFiles.Remove(existing);
                }
            }

            var extension = Path.GetExtension(file.FileName);
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(fileDir, fileName);

            await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(stream);
                await stream.FlushAsync();
            }

            EnsureUploadedFileCompleted(fullPath, file.Length, file.FileName);

            _context.ProductFiles.Add(new ProductFile
            {
                ProductId = product.Id,
                FileType = fileType,
                FilePath = $"/uploads/products/files/{fileName}",
                OriginalFileName = file.FileName,
                UploadedAt = DateTime.Now
            });
        }

        private static void ValidateProgramPackage(IFormFile file)
        {
            var extension = Path.GetExtension(file.FileName);
            if (!AllowedProgramPackageExtensions.Contains(extension))
            {
                throw new InvalidOperationException(
                    $"Demo program dosyası geçersiz: {file.FileName}. Yalnızca EXE, MSI, ZIP, RAR, 7Z veya Windows uygulama paketi yükleyebilirsin.");
            }

            if (!HasExpectedProgramPackageSignature(file, extension))
            {
                throw new InvalidOperationException(
                    $"Demo program dosyası okunamadı veya uzantısıyla içeriği uyuşmuyor: {file.FileName}.");
            }
        }

        private static bool HasExpectedProgramPackageSignature(IFormFile file, string extension)
        {
            try
            {
                var header = new byte[8];
                using var stream = file.OpenReadStream();
                var read = stream.Read(header, 0, header.Length);

                if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    return read >= 2 && header[0] == 0x4D && header[1] == 0x5A;

                if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                    return read >= 8 && header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

                if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                    return read >= 7 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 && header[4] == 0x1A && header[5] == 0x07;

                if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                    return read >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C;

                // ZIP, MSIX ve APPX paketlerinin tamamı ZIP kapsayıcı imzası taşır.
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

        private static void EnsureUploadedFileCompleted(string fullPath, long expectedLength, string originalFileName)
        {
            if (!System.IO.File.Exists(fullPath))
                throw new InvalidOperationException($"Dosya yüklenemedi: {originalFileName}");

            var actualLength = new FileInfo(fullPath).Length;
            if (actualLength != expectedLength)
            {
                try { System.IO.File.Delete(fullPath); } catch { }
                throw new InvalidOperationException($"Dosya eksik yüklendi: {originalFileName}. Lütfen tekrar dene.");
            }
        }

        private void DeletePhysicalFile(string? relativePath)
        {
            var fullPath = GetPhysicalPath(relativePath);
            if (string.IsNullOrWhiteSpace(fullPath))
                return;

            if (!string.IsNullOrWhiteSpace(relativePath) && relativePath.StartsWith("/uploads/products/images/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ProductThumbnailGenerator.DeleteAll(_environment.WebRootPath, Path.GetFileName(relativePath.Split('?', '#')[0]));
                }
                catch
                {
                    // Thumbnail temizliği ürün silme akışını bozmasın.
                }
            }

            if (System.IO.File.Exists(fullPath))
                System.IO.File.Delete(fullPath);
        }

        private async Task<string> CreateUniqueSlugAsync(string name, int? currentProductId = null)
        {
            var baseSlug = CreateSlug(name);
            var slug = baseSlug;
            var counter = 2;

            while (await _context.Products.AnyAsync(x => x.Slug == slug && (!currentProductId.HasValue || x.Id != currentProductId.Value)))
            {
                slug = $"{baseSlug}-{counter}";
                counter++;
            }

            return slug;
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

        [HttpGet]
        public async Task<IActionResult> ProductUpdates()
        {
            var updates = await _context.ProductUpdates
                .OrderByDescending(x => x.ProductCode)
                .ThenByDescending(x => x.IsActive)
                .ThenByDescending(x => x.CreatedAt)
                .ToListAsync();

            return View("UpdatePackages", updates);
        }

        [HttpGet]
        public IActionResult UpdatePackages()
        {
            return RedirectToAction(nameof(ProductUpdates));
        }

        [HttpGet]
        public IActionResult UpdatePackageAdd()
        {
            return View();
        }

        [HttpGet]
        public IActionResult ProductUpdateAdd()
        {
            return RedirectToAction(nameof(UpdatePackageAdd));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePackageAdd(string productCode, string version, string? releaseNotes, bool isActive, bool isRequired, IFormFile updateZip)
        {
            productCode = NormalizeProductCode(productCode);
            version = NormalizeUpdateVersion(version);
            releaseNotes = (releaseNotes ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(productCode) || string.IsNullOrWhiteSpace(version) || updateZip == null || updateZip.Length == 0)
            {
                TempData["Error"] = "Ürün kodu, versiyon ve update.zip zorunludur.";
                return View();
            }

            if (!IsValidUpdateVersion(version))
            {
                TempData["Error"] = "Versiyon formatı hatalı. Örnek: 1.0.2, 1.02 veya v1.02";
                return View();
            }

            if (!Path.GetExtension(updateZip.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Güncelleme dosyası ZIP olmalıdır.";
                return View();
            }

            const long maxZipSize = 300L * 1024L * 1024L;
            if (updateZip.Length > maxZipSize)
            {
                TempData["Error"] = "Güncelleme ZIP dosyası 300 MB üzerinde olamaz.";
                return View();
            }

            var safeCode = ToSafePathSegment(productCode).ToLowerInvariant();
            var safeVersion = ToSafeVersionPath(version);

            if (string.IsNullOrWhiteSpace(safeCode) || string.IsNullOrWhiteSpace(safeVersion))
            {
                TempData["Error"] = "Ürün kodu veya versiyon geçersiz karakter içeriyor.";
                return View();
            }

            var duplicate = await _context.ProductUpdates
                .FirstOrDefaultAsync(x => x.ProductCode == productCode && x.Version == version);

            if (duplicate != null)
            {
                TempData["Error"] = "Bu ürün için aynı versiyon daha önce eklenmiş. Önce eski kaydı silin veya yeni versiyon girin.";
                return RedirectToAction(nameof(ProductUpdates));
            }

            if (isActive)
            {
                var oldActiveUpdates = await _context.ProductUpdates
                    .Where(x => x.ProductCode == productCode && x.IsActive)
                    .ToListAsync();

                foreach (var old in oldActiveUpdates)
                    old.IsActive = false;
            }

            var relativeFolder = Path.Combine("updates", safeCode, safeVersion);
            var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
            Directory.CreateDirectory(folder);

            var filePath = Path.Combine(folder, "update.zip");
            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await updateZip.CopyToAsync(stream);
            }

            var downloadUrl = $"/updates/{safeCode}/{safeVersion}/update.zip";
            var item = new ProductUpdate
            {
                ProductCode = productCode,
                Version = version,
                DownloadUrl = downloadUrl,
                ReleaseNotes = releaseNotes,
                IsActive = isActive,
                IsRequired = isRequired,
                CreatedAt = DateTime.Now
            };

            _context.ProductUpdates.Add(item);
            await _context.SaveChangesAsync();

            TempData["Success"] = isActive
                ? "Güncelleme paketi eklendi ve aynı ürünün eski aktif paketleri pasif yapıldı."
                : "Güncelleme paketi pasif olarak eklendi.";

            return RedirectToAction(nameof(ProductUpdates));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePackageToggle(int id, string? returnUrl)
        {
            var item = await _context.ProductUpdates.FindAsync(id);
            if (item != null)
            {
                var willBeActive = !item.IsActive;

                if (willBeActive)
                {
                    var sameProductUpdates = await _context.ProductUpdates
                        .Where(x => x.ProductCode == item.ProductCode && x.Id != item.Id && x.IsActive)
                        .ToListAsync();

                    foreach (var old in sameProductUpdates)
                        old.IsActive = false;
                }

                item.IsActive = willBeActive;
                await _context.SaveChangesAsync();

                TempData["Success"] = willBeActive
                    ? "Güncelleme aktif yapıldı. Aynı ürünün diğer aktif paketleri pasif edildi."
                    : "Güncelleme pasif yapıldı.";
            }

            return RedirectBackToProductUpdateSource(returnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePackageDelete(int id, string? returnUrl)
        {
            var item = await _context.ProductUpdates.FindAsync(id);
            if (item != null)
            {
                var webRoot = _environment.WebRootPath ?? string.Empty;
                var relativePath = (item.DownloadUrl ?? string.Empty).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var filePath = Path.Combine(webRoot, relativePath);
                var folderPath = Path.GetDirectoryName(filePath);

                _context.ProductUpdates.Remove(item);
                await _context.SaveChangesAsync();

                try
                {
                    if (System.IO.File.Exists(filePath))
                        System.IO.File.Delete(filePath);

                    if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath) && !Directory.EnumerateFileSystemEntries(folderPath).Any())
                        Directory.Delete(folderPath);
                }
                catch
                {
                    TempData["Error"] = "Kayıt silindi fakat fiziksel ZIP dosyası silinemedi. Host dosya izinlerini kontrol edin.";
                    return RedirectBackToProductUpdateSource(returnUrl);
                }

                TempData["Success"] = "Güncelleme paketi silindi.";
            }

            return RedirectBackToProductUpdateSource(returnUrl);
        }

        private IActionResult RedirectBackToProductUpdateSource(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction(nameof(ProductUpdates));
        }

        private static string NormalizeProductCode(string? value)
        {
            return new string((value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private OfflineLicenseCertificate? EnsureRecoveryCertificate(License license, string? machineIdOverride = null)
        {
            if (!license.IsActive || (license.EndDate.HasValue && license.EndDate.Value.Date < DateTime.Today))
                return null;

            if (!license.OfflineAllowed)
                return null;

            var machineId = (machineIdOverride ?? license.MachineId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(machineId))
                return null;

            if (string.IsNullOrWhiteSpace(license.MachineId))
                license.MachineId = machineId;

            var productCode = string.IsNullOrWhiteSpace(license.ProductCode)
                ? GetProductCodeForLicense(license.Product)
                : NormalizeProductCode(license.ProductCode);

            var certificate = license.OfflineCertificates
                .Where(x => !x.IsRevoked &&
                            string.Equals(x.MachineId, machineId, StringComparison.Ordinal) &&
                            string.Equals(x.ProductCode, productCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (certificate != null)
                return certificate;

            var created = _offlineLicenseService.CreateCertificate(license, productCode, machineId);
            certificate = new OfflineLicenseCertificate
            {
                LicenseId = license.Id,
                CertificateId = created.CertificateId,
                ProductCode = productCode,
                MachineId = machineId,
                PayloadJson = created.PayloadJson,
                Signature = created.Signature,
                OfflineCode = created.OfflineCode,
                IssuedAt = DateTime.Now,
                ExpiresAt = license.EndDate
            };

            _context.OfflineLicenseCertificates.Add(certificate);
            license.OfflineCertificates.Add(certificate);
            return certificate;
        }

        private string GetPublicSiteUrl()
        {
            var configuredSiteUrl = _configuration["Site:Url"];
            if (!string.IsNullOrWhiteSpace(configuredSiteUrl))
                return configuredSiteUrl.TrimEnd('/');

            var host = Request.Host.Value ?? string.Empty;
            if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                return "https://nsxyazilim.com";

            return $"{Request.Scheme}://{Request.Host}".TrimEnd('/');
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

        private async Task RepairFreeVeresiyeLicenseMachineLinksAsync()
        {
            var licenses = await _context.Licenses
                .Include(x => x.Devices)
                .Where(x =>
                    x.IsActive &&
                    (x.MachineId == null || x.MachineId == "") &&
                    x.ProductCode != null &&
                    x.ProductCode.ToUpper() == FreeVeresiyeProgramCode)
                .Take(100)
                .ToListAsync();

            var changed = false;
            foreach (var license in licenses)
            {
                var device = license.Devices
                    .Where(x => !x.IsBlocked && !x.IsRejected && !string.IsNullOrWhiteSpace(x.MachineId))
                    .OrderBy(x => x.FirstActivatedAt)
                    .ThenBy(x => x.Id)
                    .FirstOrDefault();

                if (device == null)
                    continue;

                license.MachineId = device.MachineId.Trim();
                license.LastCheckedAt ??= device.LastSeenAt;

                if (string.IsNullOrWhiteSpace(license.LastIpAddress))
                    license.LastIpAddress = device.LastIpAddress ?? device.FirstIpAddress;

                changed = true;
            }

            if (changed)
                await _context.SaveChangesAsync();
        }

        private static string ToSafePathSegment(string value)
        {
            return new string((value ?? string.Empty)
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private static string NormalizeUpdateVersion(string value)
        {
            value = (value ?? string.Empty).Trim();
            value = value.Replace(',', '.');
            value = value.Replace(" ", string.Empty);

            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);

            return value;
        }

        private static bool IsValidUpdateVersion(string value)
        {
            value = NormalizeUpdateVersion(value);

            if (string.IsNullOrWhiteSpace(value))
                return false;

            var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || parts.Length > 4)
                return false;

            return parts.All(x => x.All(char.IsDigit));
        }

        private static string ToSafeVersionPath(string value)
        {
            return new string((value ?? string.Empty)
                .Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
                .ToArray());
        }

        private async Task PrepareProductEditUpdateViewBagAsync(Product product)
        {
            var productCode = BuildProductUpdateCode(product);
            var productCodes = GetVeresiyeProgramCodeAliases(productCode);
            ViewBag.ProductUpdateCode = productCode;
            ViewBag.ProductUpdateList = await _context.ProductUpdates
                .AsNoTracking()
                .Where(x => productCodes.Contains(x.ProductCode))
                .OrderByDescending(x => x.IsActive)
                .ThenByDescending(x => x.CreatedAt)
                .Take(10)
                .ToListAsync();
        }

        private async Task<bool> SaveProductUpdateFromProductEditAsync(Product product)
        {
            var updateZip = GetUploadedFile(null, "UpdateZip");
            var version = NormalizeUpdateVersion(Request.Form["UpdateVersion"].FirstOrDefault() ?? string.Empty);
            var notes = (Request.Form["UpdateNotes"].FirstOrDefault() ?? string.Empty).Trim();

            if ((updateZip == null || updateZip.Length <= 0) && string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(notes))
                return false;

            if (updateZip == null || updateZip.Length <= 0)
                throw new InvalidOperationException("Güncelleme için update.zip dosyası seçmelisin.");

            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("Güncelleme versiyonu zorunludur. Örnek: 1.0.2");

            if (!IsValidUpdateVersion(version))
                throw new InvalidOperationException("Versiyon formatı hatalı. Örnek: 1.0.2, 1.02 veya v1.02");

            var extension = Path.GetExtension(updateZip.FileName);
            if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Güncelleme dosyası ZIP olmalıdır.");

            const long maxUpdateZipSize = MaxUploadSize; // 1 GB
            if (updateZip.Length > maxUpdateZipSize)
                throw new InvalidOperationException("Güncelleme ZIP dosyası en fazla 1 GB olabilir.");

            var productCode = BuildProductUpdateCode(product);
            var safeProductFolder = ToSafePathSegment(!string.IsNullOrWhiteSpace(product.Slug) ? product.Slug : CreateSlug(product.Name));
            var safeVersionFolder = ToSafeVersionPath(version);

            if (string.IsNullOrWhiteSpace(safeProductFolder) || string.IsNullOrWhiteSpace(safeVersionFolder))
                throw new InvalidOperationException("Ürün slug veya versiyon klasörü oluşturulamadı.");

            var updateDir = Path.Combine(_environment.WebRootPath, "updates", safeProductFolder, safeVersionFolder);
            Directory.CreateDirectory(updateDir);

            var fullPath = Path.Combine(updateDir, "update.zip");
            await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await updateZip.CopyToAsync(stream);
                await stream.FlushAsync();
            }

            EnsureUploadedFileCompleted(fullPath, updateZip.Length, updateZip.FileName);

            var downloadUrl = $"/updates/{safeProductFolder}/{safeVersionFolder}/update.zip";
            var isActive = Request.Form["UpdateIsActive"].Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase));
            var isRequired = Request.Form["UpdateIsRequired"].Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase));

            var existingSameVersion = await _context.ProductUpdates
                .FirstOrDefaultAsync(x => x.ProductCode == productCode && x.Version == version);

            if (isActive)
            {
                var oldActiveUpdates = await _context.ProductUpdates
                    .Where(x => x.ProductCode == productCode && x.IsActive && (existingSameVersion == null || x.Id != existingSameVersion.Id))
                    .ToListAsync();

                foreach (var old in oldActiveUpdates)
                    old.IsActive = false;
            }

            if (existingSameVersion != null)
            {
                existingSameVersion.DownloadUrl = downloadUrl;
                existingSameVersion.ReleaseNotes = notes;
                existingSameVersion.IsActive = isActive;
                existingSameVersion.IsRequired = isRequired;
                existingSameVersion.CreatedAt = DateTime.Now;
            }
            else
            {
                _context.ProductUpdates.Add(new ProductUpdate
                {
                    ProductCode = productCode,
                    Version = version,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = notes,
                    IsActive = isActive,
                    IsRequired = isRequired,
                    CreatedAt = DateTime.Now
                });
            }

            return true;
        }

        private static string BuildProductUpdateCode(Product product)
        {
            var source = NormalizeProgramCodeText($"{product.ProductCode} {product.Slug} {product.Name}");

            if (IsFreeVeresiyeProgramSource(source))
                return FreeVeresiyeProgramCode;

            // Urunde acikca tanimli program kodu varsa slug'dan yeni kod uretme.
            // Boylece masaustu program, admin panel ve sonraki tum guncellemeler ayni kodu kullanir.
            var explicitProductCode = NormalizeProgramCodeText(product.ProductCode);
            if (!string.IsNullOrWhiteSpace(explicitProductCode))
                return explicitProductCode;

            if (source.Contains("VERESIYE"))
                return LegacyVeresiyeUpdateCode;

            if (source.Contains("TURBO"))
                return "NSXTURBO";

            var slug = !string.IsNullOrWhiteSpace(product.Slug) ? product.Slug : CreateSlug(product.Name);
            var code = new string((slug ?? string.Empty)
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

            return string.IsNullOrWhiteSpace(code) ? $"NSXPRODUCT{product.Id}" : code;
        }

        private static bool IsFreeVeresiyeProgramSource(string source)
        {
            return source.Contains("VERESIYE") &&
                   (source.Contains("FREE") || source.Contains("UCRETSIZ"));
        }

        private static string NormalizeProgramCodeText(string? value)
        {
            var text = (value ?? string.Empty)
                .Replace('\u0130', 'I')
                .Replace('\u0131', 'i')
                .Replace('\u00C7', 'C')
                .Replace('\u00E7', 'c')
                .Replace('\u011E', 'G')
                .Replace('\u011F', 'g')
                .Replace('\u00D6', 'O')
                .Replace('\u00F6', 'o')
                .Replace('\u015E', 'S')
                .Replace('\u015F', 's')
                .Replace('\u00DC', 'U')
                .Replace('\u00FC', 'u')
                .ToUpperInvariant();

            return new string(text.Where(char.IsLetterOrDigit).ToArray());
        }

        private static string[] GetVeresiyeProgramCodeAliases(string productCode)
        {
            var normalized = NormalizeProgramCodeText(productCode);
            if (!normalized.Contains("VERESIYE"))
                return new[] { productCode };

            return new[]
            {
                FreeVeresiyeProgramCode,
                LegacyVeresiyeAdvertisementCode,
                LegacyVeresiyeUpdateCode,
                "VERESIYEDEFTERI",
                "VERESIYE"
            };
        }


        [HttpGet]
        public async Task<IActionResult> Advertisements(string? productCode = null, string? slotCode = null)
        {
            var normalizedProductCode = NormalizeAdvertisementProductCode(productCode);
            var normalizedSlotCode = NormalizeAdvertisementSlotCode(slotCode);

            var query = _context.Advertisements
                .Include(x => x.Product)
                .Where(x => !x.IsDeleted);

            if (!string.IsNullOrWhiteSpace(normalizedProductCode))
            {
                var productCodes = GetVeresiyeProgramCodeAliases(normalizedProductCode);
                query = query.Where(x => productCodes.Contains(x.ProductCode));
            }

            if (!string.IsNullOrWhiteSpace(normalizedSlotCode))
                query = query.Where(x => x.SlotCode == normalizedSlotCode);

            var ads = await query
                .OrderByDescending(x => x.IsActive)
                .ThenByDescending(x => x.Priority)
                .ThenBy(x => x.SortOrder)
                .ThenByDescending(x => x.Id)
                .ToListAsync();

            await PrepareAdvertisementListsAsync(null, normalizedProductCode, normalizedSlotCode);
            ViewBag.ActiveAdvertisementCount = await _context.Advertisements.CountAsync(x => !x.IsDeleted && x.IsActive);
            ViewBag.TotalAdvertisementViews = await _context.Advertisements.Where(x => !x.IsDeleted).SumAsync(x => (int?)x.ImpressionCount) ?? 0;
            ViewBag.TotalAdvertisementClicks = await _context.Advertisements.Where(x => !x.IsDeleted).SumAsync(x => (int?)x.ClickCount) ?? 0;

            return View(ads);
        }

        [HttpGet]
        public async Task<IActionResult> AdvertisementAdd(int? productId = null)
        {
            var model = new AdvertisementFormViewModel
            {
                ProductId = productId,
                ProductCode = FreeVeresiyeProgramCode,
                SlotCode = "FREE_BOTTOM_728X90",
                Title = "Ücretsiz Veresiye Defteri",
                AltText = "NSX Yazılım reklam görseli",
                TargetUrl = "https://www.nsxyazilim.com",
                Width = 728,
                Height = 90,
                DisplaySeconds = 10,
                SortOrder = 0,
                Priority = 0,
                IsActive = true,
                TrackClicks = true
            };

            if (productId.HasValue)
            {
                var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == productId.Value && !x.IsDeleted);
                if (product != null)
                {
                    model.ProductCode = BuildAdvertisementProductCode(product);
                    model.Title = product.Name;
                }
            }

            await PrepareAdvertisementListsAsync(model.ProductId, model.ProductCode, model.SlotCode);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxAdvertisementImageSize)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxAdvertisementImageSize)]
        public async Task<IActionResult> AdvertisementAdd(AdvertisementFormViewModel model)
        {
            await NormalizeAdvertisementModelAsync(model);
            var imageFile = GetUploadedFile(model.ImageFile, "ImageFile");

            if (imageFile == null || imageFile.Length == 0)
                ModelState.AddModelError(nameof(model.ImageFile), "Banner görseli zorunludur.");

            ValidateAdvertisementModel(model, imageFileRequired: true);

            if (!ModelState.IsValid)
            {
                await PrepareAdvertisementListsAsync(model.ProductId, model.ProductCode, model.SlotCode);
                return View(model);
            }

            string imagePath;
            try
            {
                imagePath = await SaveAdvertisementImageAsync(imageFile!);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                await PrepareAdvertisementListsAsync(model.ProductId, model.ProductCode, model.SlotCode);
                return View(model);
            }

            var advertisement = new Advertisement
            {
                ProductId = model.ProductId,
                ProductCode = model.ProductCode,
                SlotCode = model.SlotCode,
                Title = model.Title.Trim(),
                Description = CleanOptional(model.Description),
                AltText = CleanOptional(model.AltText),
                ImagePath = imagePath,
                TargetUrl = CleanOptional(model.TargetUrl),
                Width = Math.Clamp(model.Width, 1, 4000),
                Height = Math.Clamp(model.Height, 1, 2000),
                DisplaySeconds = Math.Clamp(model.DisplaySeconds, 3, 3600),
                SortOrder = Math.Max(0, model.SortOrder),
                Priority = Math.Max(0, model.Priority),
                StartDate = model.StartDate,
                EndDate = model.EndDate,
                IsActive = model.IsActive,
                TrackClicks = model.TrackClicks,
                CreatedAt = DateTime.Now
            };

            _context.Advertisements.Add(advertisement);
            await _context.SaveChangesAsync();
            var queued = await _translationQueue.QueueAdvertisementAsync(advertisement, HttpContext.RequestAborted);

            TempData["Success"] = $"Reklam eklendi. {queued} dil çevirisi kuyruğa alındı. Program API üzerinden otomatik çekebilir.";
            return RedirectToAction(nameof(Advertisements));
        }

        [HttpGet]
        public async Task<IActionResult> AdvertisementEdit(int id)
        {
            var advertisement = await _context.Advertisements.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (advertisement == null)
                return NotFound();

            var model = new AdvertisementFormViewModel
            {
                Id = advertisement.Id,
                ProductId = advertisement.ProductId,
                ProductCode = advertisement.ProductCode,
                SlotCode = advertisement.SlotCode,
                Title = advertisement.Title,
                Description = advertisement.Description,
                AltText = advertisement.AltText,
                ExistingImagePath = advertisement.ImagePath,
                TargetUrl = advertisement.TargetUrl,
                Width = advertisement.Width,
                Height = advertisement.Height,
                DisplaySeconds = advertisement.DisplaySeconds,
                SortOrder = advertisement.SortOrder,
                Priority = advertisement.Priority,
                StartDate = advertisement.StartDate,
                EndDate = advertisement.EndDate,
                IsActive = advertisement.IsActive,
                TrackClicks = advertisement.TrackClicks
            };

            await PrepareAdvertisementListsAsync(model.ProductId, model.ProductCode, model.SlotCode);
            ViewBag.ImpressionCount = advertisement.ImpressionCount;
            ViewBag.ClickCount = advertisement.ClickCount;
            ViewBag.LastShownAt = advertisement.LastShownAt;
            ViewBag.LastClickedAt = advertisement.LastClickedAt;

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxAdvertisementImageSize)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxAdvertisementImageSize)]
        public async Task<IActionResult> AdvertisementEdit(int id, AdvertisementFormViewModel model)
        {
            var advertisement = await _context.Advertisements.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (advertisement == null)
                return NotFound();

            model.Id = id;
            model.ExistingImagePath = advertisement.ImagePath;
            await NormalizeAdvertisementModelAsync(model);

            var imageFile = GetUploadedFile(model.ImageFile, "ImageFile");
            ValidateAdvertisementModel(model, imageFileRequired: false);

            if (!ModelState.IsValid)
            {
                await PrepareAdvertisementListsAsync(model.ProductId, model.ProductCode, model.SlotCode);
                ViewBag.ImpressionCount = advertisement.ImpressionCount;
                ViewBag.ClickCount = advertisement.ClickCount;
                ViewBag.LastShownAt = advertisement.LastShownAt;
                ViewBag.LastClickedAt = advertisement.LastClickedAt;
                return View(model);
            }

            var oldImagePath = advertisement.ImagePath;

            try
            {
                if (imageFile != null && imageFile.Length > 0)
                    advertisement.ImagePath = await SaveAdvertisementImageAsync(imageFile);
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                await PrepareAdvertisementListsAsync(model.ProductId, model.ProductCode, model.SlotCode);
                return View(model);
            }

            advertisement.ProductId = model.ProductId;
            advertisement.ProductCode = model.ProductCode;
            advertisement.SlotCode = model.SlotCode;
            advertisement.Title = model.Title.Trim();
            advertisement.Description = CleanOptional(model.Description);
            advertisement.AltText = CleanOptional(model.AltText);
            advertisement.TargetUrl = CleanOptional(model.TargetUrl);
            advertisement.Width = Math.Clamp(model.Width, 1, 4000);
            advertisement.Height = Math.Clamp(model.Height, 1, 2000);
            advertisement.DisplaySeconds = Math.Clamp(model.DisplaySeconds, 3, 3600);
            advertisement.SortOrder = Math.Max(0, model.SortOrder);
            advertisement.Priority = Math.Max(0, model.Priority);
            advertisement.StartDate = model.StartDate;
            advertisement.EndDate = model.EndDate;
            advertisement.IsActive = model.IsActive;
            advertisement.TrackClicks = model.TrackClicks;
            advertisement.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();
            var queued = await _translationQueue.QueueAdvertisementAsync(advertisement, HttpContext.RequestAborted);

            if (!string.Equals(oldImagePath, advertisement.ImagePath, StringComparison.OrdinalIgnoreCase))
                DeleteAdvertisementImage(oldImagePath);

            TempData["Success"] = $"Reklam güncellendi. {queued} dil çevirisi kuyruğa alındı.";
            return RedirectToAction(nameof(Advertisements));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AdvertisementToggle(int id)
        {
            var advertisement = await _context.Advertisements.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (advertisement == null)
                return NotFound();

            advertisement.IsActive = !advertisement.IsActive;
            advertisement.UpdatedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            TempData["Success"] = advertisement.IsActive ? "Reklam aktif edildi." : "Reklam pasif edildi.";
            return RedirectToAction(nameof(Advertisements));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AdvertisementDelete(int id)
        {
            var advertisement = await _context.Advertisements.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (advertisement == null)
                return NotFound();

            advertisement.IsDeleted = true;
            advertisement.IsActive = false;
            advertisement.UpdatedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            DeleteAdvertisementImage(advertisement.ImagePath);

            TempData["Success"] = "Reklam silindi.";
            return RedirectToAction(nameof(Advertisements));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AdvertisementStatsReset(int id)
        {
            var advertisement = await _context.Advertisements.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (advertisement == null)
                return NotFound();

            advertisement.ImpressionCount = 0;
            advertisement.ClickCount = 0;
            advertisement.LastShownAt = null;
            advertisement.LastClickedAt = null;
            advertisement.UpdatedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            TempData["Success"] = "Reklam istatistikleri sıfırlandı.";
            return RedirectToAction(nameof(AdvertisementEdit), new { id });
        }

        private async Task PrepareAdvertisementListsAsync(int? selectedProductId = null, string? selectedProductCode = null, string? selectedSlotCode = null)
        {
            ViewBag.AdvertisementProducts = await _context.Products
                .Where(x => !x.IsDeleted)
                .OrderByDescending(x => x.IsActive)
                .ThenBy(x => x.Name)
                .ToListAsync();

            ViewBag.SelectedAdvertisementProductId = selectedProductId;
            ViewBag.SelectedAdvertisementProductCode = NormalizeAdvertisementProductCode(selectedProductCode);
            ViewBag.SelectedAdvertisementSlotCode = NormalizeAdvertisementSlotCode(selectedSlotCode);
            ViewBag.AdvertisementSlots = GetAdvertisementSlots();
        }

        private async Task NormalizeAdvertisementModelAsync(AdvertisementFormViewModel model)
        {
            model.ProductCode = NormalizeAdvertisementProductCode(model.ProductCode);
            model.SlotCode = NormalizeAdvertisementSlotCode(model.SlotCode);
            model.Title = (model.Title ?? string.Empty).Trim();
            model.TargetUrl = CleanOptional(model.TargetUrl);
            model.Description = CleanOptional(model.Description);
            model.AltText = CleanOptional(model.AltText);

            if (model.ProductId.HasValue)
            {
                var product = await _context.Products.FirstOrDefaultAsync(x => x.Id == model.ProductId.Value && !x.IsDeleted);
                if (product == null)
                {
                    model.ProductId = null;
                }
                else if (string.IsNullOrWhiteSpace(model.ProductCode))
                {
                    model.ProductCode = BuildAdvertisementProductCode(product);
                }
            }

            if (string.IsNullOrWhiteSpace(model.ProductCode))
                model.ProductCode = FreeVeresiyeProgramCode;

            if (string.IsNullOrWhiteSpace(model.SlotCode))
                model.SlotCode = "FREE_BOTTOM_728X90";
        }

        private void ValidateAdvertisementModel(AdvertisementFormViewModel model, bool imageFileRequired)
        {
            if (string.IsNullOrWhiteSpace(model.Title))
                ModelState.AddModelError(nameof(model.Title), "Reklam başlığı zorunludur.");

            if (string.IsNullOrWhiteSpace(model.ProductCode))
                ModelState.AddModelError(nameof(model.ProductCode), "Program kodu zorunludur.");

            if (string.IsNullOrWhiteSpace(model.SlotCode))
                ModelState.AddModelError(nameof(model.SlotCode), "Reklam alanı zorunludur.");

            if (model.EndDate.HasValue && model.StartDate.HasValue && model.EndDate.Value < model.StartDate.Value)
                ModelState.AddModelError(nameof(model.EndDate), "Bitiş tarihi başlangıç tarihinden önce olamaz.");

            if (!string.IsNullOrWhiteSpace(model.TargetUrl) && !IsValidAdvertisementUrl(model.TargetUrl))
                ModelState.AddModelError(nameof(model.TargetUrl), "URL http://, https:// veya / ile başlayan site içi bir adres olmalıdır.");

            var imageFile = GetUploadedFile(model.ImageFile, "ImageFile");
            if (imageFile != null && imageFile.Length > 0)
                ValidateAdvertisementImage(imageFile);
            else if (imageFileRequired)
                ModelState.AddModelError(nameof(model.ImageFile), "Banner görseli zorunludur.");
        }

        private static IReadOnlyList<(string Code, string Name, string Detail)> GetAdvertisementSlots()
        {
            return new List<(string Code, string Name, string Detail)>
            {
                ("FREE_BOTTOM_728X90", "Program Alt Banner", "728 x 90 px - ücretsiz sürüm alt reklam"),
                ("APP_TOP_728X90", "Program Üst Banner", "728 x 90 px - program içi üst reklam"),
                ("SITE_HOME_728X90", "Site Ana Sayfa Banner", "728 x 90 px - site içi kullanım"),
                ("POPUP_600X400", "Popup Reklam", "600 x 400 px - kampanya/popup alanı")
            };
        }

        private static string NormalizeAdvertisementProductCode(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string NormalizeAdvertisementSlotCode(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string BuildAdvertisementProductCode(Product product)
        {
            var source = NormalizeProgramCodeText($"{product.ProductCode} {product.Slug} {product.Name}");
            if (IsFreeVeresiyeProgramSource(source))
                return FreeVeresiyeProgramCode;

            var existingProductCode = NormalizeProductCode(product.ProductCode);
            if (!string.IsNullOrWhiteSpace(existingProductCode))
                return existingProductCode;

            if (source.Contains("VERESIYE"))
                return LegacyVeresiyeAdvertisementCode;

            if (source.Contains("TURBO"))
                return "NSXTURBO";

            if (source.Contains("KLINIK"))
                return "NSXKLINIK";

            if (source.Contains("TEKNIK"))
                return "NSXTEKNIKSERVIS";

            var normalized = source
                .Replace('ı', 'i')
                .Replace('ğ', 'g')
                .Replace('ü', 'u')
                .Replace('ş', 's')
                .Replace('ö', 'o')
                .Replace('ç', 'c');

            var code = new string(normalized
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

            return string.IsNullOrWhiteSpace(code) ? $"NSXPRODUCT{product.Id}" : code;
        }

        private static bool IsValidAdvertisementUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return true;

            var trimmed = url.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
                return true;

            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string? CleanOptional(string? value)
        {
            var cleaned = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        private void ValidateAdvertisementImage(IFormFile file)
        {
            if (file.Length > MaxAdvertisementImageSize)
                ModelState.AddModelError(nameof(AdvertisementFormViewModel.ImageFile), "Reklam görseli en fazla 5 MB olabilir.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

            if (!allowedExtensions.Contains(extension))
                ModelState.AddModelError(nameof(AdvertisementFormViewModel.ImageFile), "Reklam görseli JPG, PNG, GIF veya WEBP olmalıdır.");
            else if (!IsBrowserSafeImage(file, extension))
                ModelState.AddModelError(nameof(AdvertisementFormViewModel.ImageFile), "Seçilen dosya geçerli bir görsel dosyası değil.");
        }

        private async Task<string> SaveAdvertisementImageAsync(IFormFile file)
        {
            ValidateAdvertisementImage(file);
            if (!ModelState.IsValid)
                throw new InvalidOperationException("Reklam görseli uygun değil. JPG, PNG, GIF veya WEBP seçin.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var uploadDir = Path.Combine(_environment.WebRootPath, "uploads", "advertisements");
            Directory.CreateDirectory(uploadDir);

            var fileName = $"ad_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(uploadDir, fileName);

            await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(stream);
                await stream.FlushAsync();
            }

            EnsureUploadedFileCompleted(fullPath, file.Length, file.FileName);
            return $"/uploads/advertisements/{fileName}";
        }

        private void DeleteAdvertisementImage(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return;

            if (!imagePath.StartsWith("/uploads/advertisements/", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                DeletePhysicalFile(imagePath);
            }
            catch
            {
                // Reklam kaydı silinmişse dosya temizliği başarısız olsa da admin akışı bozulmasın.
            }
        }

        private static string? NormalizeTurkishMobilePhone(string? value)
        {
            var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());

            if (digits.Length == 12 && digits.StartsWith("90", StringComparison.Ordinal))
                digits = "0" + digits[2..];
            else if (digits.Length == 10 && digits.StartsWith("5", StringComparison.Ordinal))
                digits = "0" + digits;

            if (digits.Length != 11 || !digits.StartsWith("05", StringComparison.Ordinal))
                return null;

            return digits;
        }

        private static string MaskPhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone) || phone.Length < 7)
                return phone;

            return $"{phone[..4]} *** ** {phone[^2..]}";
        }

        private static void AddPhoneLocationCandidate(
            ICollection<PhoneLocationCandidate> candidates,
            string? city,
            string? region,
            string? country,
            string? ipAddress,
            DateTime? observedAt,
            string source)
        {
            if (string.IsNullOrWhiteSpace(city) &&
                string.IsNullOrWhiteSpace(region) &&
                string.IsNullOrWhiteSpace(ipAddress))
                return;

            candidates.Add(new PhoneLocationCandidate
            {
                City = city,
                Region = region,
                Country = country,
                IpAddress = ipAddress,
                ObservedAt = observedAt,
                Source = source
            });
        }

        private sealed class PhoneLocationCandidate
        {
            public string? City { get; init; }
            public string? Region { get; init; }
            public string? Country { get; init; }
            public string? IpAddress { get; init; }
            public DateTime? ObservedAt { get; init; }
            public string Source { get; init; } = string.Empty;
        }

        private async Task UpdateUserLocationAsync(User user)
        {
            var ipAddress = _clientIpService.GetClientIp(HttpContext);
            var previousIpAddress = user.LastIpAddress;

            if (!string.IsNullOrWhiteSpace(ipAddress))
                user.LastIpAddress = ipAddress;

            user.LastLoginAt = DateTime.Now;

            var shouldLookupLocation =
                !string.IsNullOrWhiteSpace(ipAddress) &&
                _clientIpService.IsPublicIp(ipAddress) &&
                (!string.Equals(previousIpAddress, ipAddress, StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(user.LastCity) ||
                 user.LastGeoLookupAt == null ||
                 user.LastGeoLookupAt.Value < DateTime.Now.AddDays(-7));

            if (!shouldLookupLocation)
                return;

            var location = await _ipGeolocationService.ResolveAsync(ipAddress);
            if (location == null)
                return;

            user.LastCity = location.City;
            user.LastRegion = location.Region;
            user.LastCountry = location.Country;
            user.LastGeoLookupAt = DateTime.Now;
        }

        private async Task ReloadProductCategoryOptionsAsync()
        {
            var options = await _context.ProductCategories
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Label)
                .Select(x => new ProductCategoryHelper.Option(x.Key, x.Label, x.IsActive))
                .ToListAsync();
            ProductCategoryHelper.Configure(options);
        }

        private sealed class ProductFromForm : Product
        {
            public ProductFromForm(ProductFormViewModel model)
            {
                Name = model.Name;
                ProductCode = NormalizeProductCode(model.ProductCode);
                Category = ProductCategoryHelper.Normalize(model.Category, model.Name, model.ProductCode);
                Description = ProductContentSanitizer.Sanitize(model.Description);
                MetaTitle = model.MetaTitle;
                MetaDescription = model.MetaDescription;
                YearlyPrice = model.YearlyPrice;
                LifetimePrice = model.LifetimePrice;
                StockQuantity = model.StockQuantity < 0 ? 0 : model.StockQuantity;
                IsActive = model.IsActive;
            }
        }
    }
}
