using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;
using System.Security.Claims;

namespace NSYazilim.Web.Controllers
{
    public class AccountController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailSender _emailSender;
        private readonly IConfiguration _configuration;
        private readonly OfflineLicenseService _offlineLicenseService;
        private readonly ClientIpService _clientIpService;
        private readonly SiteLocalizationService _localization;
        private readonly UserLocationUpdateQueue _userLocationQueue;
        private readonly BackgroundMailQueue _backgroundMailQueue;
        private readonly ShopierOrderFulfillmentService _shopierFulfillment;

        public AccountController(
            ApplicationDbContext context,
            IEmailSender emailSender,
            IConfiguration configuration,
            OfflineLicenseService offlineLicenseService,
            ClientIpService clientIpService,
            SiteLocalizationService localization,
            UserLocationUpdateQueue userLocationQueue,
            BackgroundMailQueue backgroundMailQueue,
            ShopierOrderFulfillmentService shopierFulfillment)
        {
            _context = context;
            _emailSender = emailSender;
            _configuration = configuration;
            _offlineLicenseService = offlineLicenseService;
            _clientIpService = clientIpService;
            _localization = localization;
            _userLocationQueue = userLocationQueue;
            _backgroundMailQueue = backgroundMailQueue;
            _shopierFulfillment = shopierFulfillment;
        }

        [HttpGet]
        public async Task<IActionResult> Login(string? returnUrl = null, string? uiLang = null)
        {
            if (IsCustomerLoggedIn())
            {
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                return RedirectToAction(nameof(MyAccount));
            }

            var currentLanguage = await _localization.GetCurrentLanguageAsync(HttpContext.RequestAborted);
            return View(new AccountLoginViewModel
            {
                ReturnUrl = returnUrl,
                UiLang = currentLanguage.Code
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(AccountLoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var email = model.Email.Trim().ToLowerInvariant();

            var user = await _context.Users.FirstOrDefaultAsync(x =>
                x.Email.ToLower() == email &&
                !x.IsDeleted &&
                x.IsActive &&
                x.Role == "User");

            if (user == null || !PasswordHasher.Verify(model.Password, user.PasswordHash))
            {
                TempData["Error"] = "E-posta veya şifre hatalı.";
                return View(model);
            }

            var locationWork = UpdateUserActivity(user);
            await ApplyPreferredLanguageAsync(user, model.UiLang);
            await _context.SaveChangesAsync();
            QueueUserLocation(user, locationWork);

            await SignInCustomer(user, model.RememberMe);

            TempData["Success"] = "Giriş başarılı. Hesabına hoş geldin.";

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                return Redirect(model.ReturnUrl);

            return RedirectToAction(nameof(MyAccount));
        }

        [HttpGet]
        public async Task<IActionResult> Register(string? returnUrl = null, string? uiLang = null)
        {
            if (IsCustomerLoggedIn())
            {
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                return RedirectToAction(nameof(MyAccount));
            }

            var currentLanguage = await _localization.GetCurrentLanguageAsync(HttpContext.RequestAborted);
            return View(new AccountRegisterViewModel
            {
                ReturnUrl = returnUrl,
                UiLang = currentLanguage.Code
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(AccountRegisterViewModel model)
        {
            NormalizeRegisterModel(model);

            if (!InternationalPhoneNumber.TryNormalize(model.Phone, out var normalizedPhone))
            {
                ModelState.AddModelError(nameof(model.Phone),
                    "Telefon numarası geçersiz. Türkiye için 05XXXXXXXXX, diğer ülkeler için + ülke kodu kullanın.");
            }
            else
            {
                model.Phone = normalizedPhone;
            }

            if (!ModelState.IsValid)
                return View(model);

            var emailExists = await _context.Users.AnyAsync(x =>
                x.Email.ToLower() == model.Email.ToLower());

            if (emailExists)
            {
                ModelState.AddModelError(nameof(model.Email), "Bu e-posta adresi zaten kayıtlı.");
                return View(model);
            }

            if (!string.IsNullOrWhiteSpace(model.Phone))
            {
                var phoneCandidates = InternationalPhoneNumber.GetLookupCandidates(model.Phone);
                var phoneExists = await _context.Users.AnyAsync(x =>
                    x.Phone != null && phoneCandidates.Contains(x.Phone));

                if (phoneExists)
                {
                    ModelState.AddModelError(nameof(model.Phone), "Bu cep telefonu zaten kayıtlı.");
                    return View(model);
                }
            }

            var user = new User
            {
                FullName = model.FullName,
                Email = model.Email,
                Phone = model.Phone,
                PasswordHash = PasswordHasher.Hash(model.Password),
                Role = "User",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.Now
            };

            var locationWork = UpdateUserActivity(user);

            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            await ApplyPreferredLanguageAsync(user, model.UiLang);
            await _context.SaveChangesAsync();
            QueueUserLocation(user, locationWork);

            var siteUrl = GetPublicSiteUrl();
            var nonTurkishWelcome = !string.IsNullOrWhiteSpace(model.UiLang)
                && !model.UiLang.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
            _backgroundMailQueue.TryQueue(new BackgroundMailMessage(
                user.Email,
                user.FullName,
                nonTurkishWelcome ? "Welcome to NSX Software" : "NSX Yazılım’a hoş geldiniz",
                EmailTemplates.Welcome(user.FullName, siteUrl, model.UiLang),
                "Welcome",
                "Yeni NSX kullanıcı hesabı hoş geldin e-postası."));

            await SignInCustomer(user);

            TempData["Success"] = "Üyelik oluşturuldu. Hesabına hoş geldin.";

            if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            {
                if (model.ReturnUrl.StartsWith("/store/free-download-start", StringComparison.OrdinalIgnoreCase))
                    TempData["NSX.RegistrationCompleted"] = "1";

                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction(nameof(MyAccount));
        }

        [HttpGet]
        public async Task<IActionResult> ForgotPassword(string? uiLang = null)
        {
            var languageCode = await ResolveUiLanguageCodeAsync(uiLang);
            return View(new ForgotPasswordViewModel { UiLang = languageCode });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            model.UiLang = await ResolveUiLanguageCodeAsync(model.UiLang);

            if (!ModelState.IsValid)
                return View(model);

            var email = model.Email.Trim().ToLowerInvariant();

            var user = await _context.Users.FirstOrDefaultAsync(x =>
                x.Email.ToLower() == email &&
                !x.IsDeleted &&
                x.IsActive &&
                x.Role == "User");

            // Güvenlik için kullanıcı yoksa da aynı sonucu gösteriyoruz.
            if (user != null)
            {
                user.PasswordResetToken = Guid.NewGuid().ToString("N");
                user.PasswordResetTokenExpireDate = DateTime.Now.AddHours(1);

                await _context.SaveChangesAsync();

                var resetPath = Url.Action(
                    nameof(ResetPassword),
                    "Account",
                    new { email = user.Email, token = user.PasswordResetToken, uiLang = model.UiLang });
                var resetUrl = string.IsNullOrWhiteSpace(resetPath)
                    ? null
                    : $"{GetPublicSiteUrl()}{resetPath}";

                if (!string.IsNullOrWhiteSpace(resetUrl))
                {
                    try
                    {
                        var nonTurkish = !string.IsNullOrWhiteSpace(model.UiLang)
                            && !model.UiLang.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
                        await _emailSender.SendEmailAsync(
                            user.Email,
                            nonTurkish ? "NSX Software password reset" : "NSX Yazılım şifre sıfırlama",
                            EmailTemplates.ResetPassword(user.FullName, resetUrl, model.UiLang));
                    }
                    catch
                    {
                        TempData["Error"] = "Şifre sıfırlama e-postası gönderilemedi. E-posta ayarlarını kontrol edin.";
                        return View(model);
                    }
                }
            }

            TempData["Success"] = "Eğer bu e-posta ile kayıtlı hesabınız varsa şifre yenileme bağlantısı gönderildi.";
            return RedirectToAction(nameof(ForgotPasswordConfirmation), new { uiLang = model.UiLang });
        }

        [HttpGet]
        public async Task<IActionResult> ForgotPasswordConfirmation(string? uiLang = null)
        {
            ViewBag.UiLang = await ResolveUiLanguageCodeAsync(uiLang);
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> ResetPassword(string email, string token, string? uiLang = null)
        {
            var languageCode = await ResolveUiLanguageCodeAsync(uiLang);
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
                return RedirectToAction(nameof(Login), new { uiLang = languageCode });

            var user = await _context.Users.FirstOrDefaultAsync(x =>
                x.Email == email &&
                x.PasswordResetToken == token &&
                x.PasswordResetTokenExpireDate != null &&
                x.PasswordResetTokenExpireDate >= DateTime.Now);

            if (user == null)
            {
                TempData["Error"] = "Şifre yenileme bağlantısı geçersiz veya süresi dolmuş.";
                return RedirectToAction(nameof(ForgotPassword), new { uiLang = languageCode });
            }

            return View(new ResetPasswordViewModel
            {
                Email = email,
                Token = token,
                UiLang = languageCode
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            model.UiLang = await ResolveUiLanguageCodeAsync(model.UiLang);

            if (!ModelState.IsValid)
                return View(model);

            var user = await _context.Users.FirstOrDefaultAsync(x =>
                x.Email == model.Email &&
                x.PasswordResetToken == model.Token &&
                x.PasswordResetTokenExpireDate != null &&
                x.PasswordResetTokenExpireDate >= DateTime.Now);

            if (user == null)
            {
                TempData["Error"] = "Şifre yenileme bağlantısı geçersiz veya süresi dolmuş.";
                return RedirectToAction(nameof(ForgotPassword), new { uiLang = model.UiLang });
            }

            user.PasswordHash = PasswordHasher.Hash(model.NewPassword);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpireDate = null;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Şifreniz başarıyla yenilendi. Yeni şifrenizle giriş yapabilirsiniz.";
            return RedirectToAction(nameof(Login), new { uiLang = model.UiLang });
        }

        [HttpGet]
        public async Task<IActionResult> MyAccount()
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = "/Account/MyAccount" });

            // Eski Shopier teslimatlarında lisans oluşmuş fakat yerel sipariş kaydı eksik kalmışsa
            // hesabı açarken yalnızca geçmiş kaydı onar. Yeni lisans üretmez.
            await _shopierFulfillment.RepairCustomerOrderHistoryAsync(
                user.Id, user.Email, HttpContext.RequestAborted);

            var recentOrders = await _context.Orders
                .Include(x => x.OrderItems)
                .ThenInclude(x => x.Product)
                .Where(x => x.UserId == user.Id)
                .OrderByDescending(x => x.Id)
                .Take(5)
                .ToListAsync();

            var recentLicenses = await _context.Licenses
                .Include(x => x.Product)
                .Where(x => x.UserId == user.Id)
                .OrderByDescending(x => x.Id)
                .Take(5)
                .ToListAsync();

            var model = new CustomerDashboardViewModel
            {
                User = user,
                OrderCount = await _context.Orders.CountAsync(x => x.UserId == user.Id),
                LicenseCount = await _context.Licenses.CountAsync(x => x.UserId == user.Id),
                ActiveLicenseCount = await _context.Licenses.CountAsync(x =>
                    x.UserId == user.Id &&
                    x.IsActive &&
                    (x.EndDate == null || x.EndDate >= DateTime.Now)),
                DownloadCount = await _context.DemoDownloads
                    .Where(x => x.UserId == user.Id)
                    .Select(x => x.ProductId)
                    .Distinct()
                    .CountAsync(),
                RecentOrders = recentOrders,
                RecentLicenses = recentLicenses
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Orders()
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = "/Account/Orders" });

            // Shopier tarafında teslim edilmiş, lisansı mevcut fakat Orders satırı eksik kalan
            // eski işlemleri güvenli biçimde sipariş geçmişine bağla.
            await _shopierFulfillment.RepairCustomerOrderHistoryAsync(
                user.Id, user.Email, HttpContext.RequestAborted);

            var orders = await _context.Orders
                .Include(x => x.OrderItems)
                .ThenInclude(x => x.Product)
                .Include(x => x.Licenses)
                .Where(x => x.UserId == user.Id)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            return View(orders);
        }

        [HttpGet]
        public async Task<IActionResult> Licenses()
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = "/Account/Licenses" });

            var licenses = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.Order)
                .Include(x => x.OfflineCertificates)
                .Where(x => x.UserId == user.Id)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            var recoveryCreated = false;
            foreach (var license in licenses)
            {
                var certificate = EnsureRecoveryCertificate(license);
                if (certificate != null && certificate.Id == 0)
                    recoveryCreated = true;
            }

            if (recoveryCreated)
                await _context.SaveChangesAsync();

            return View(licenses);
        }


        [HttpGet]
        public async Task<IActionResult> OfflineLicenseFile(int id)
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = $"/Account/OfflineLicenseFile/{id}" });

            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);

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

            var machineId = (license.MachineId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(machineId))
            {
                TempData["Error"] = "Offline lisans dosyası için önce programdan en az bir kez online etkinleştirme yapılmalı.";
                return RedirectToAction(nameof(Licenses));
            }

            var productCode = string.IsNullOrWhiteSpace(license.ProductCode)
                ? BuildProductCode(license.Product?.Slug ?? license.Product?.Name ?? "NSX")
                : license.ProductCode.Trim().ToUpperInvariant();

            var certificate = license.OfflineCertificates
                .Where(x => !x.IsRevoked && x.MachineId == machineId && x.ProductCode == productCode)
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (certificate == null)
            {
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
                await _context.SaveChangesAsync();
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(certificate.OfflineCode);
            var fileName = $"{productCode}-{certificate.CertificateId}.nsxlic";
            return File(bytes, "application/octet-stream", fileName);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendRecoveryCodeEmail(int id)
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = "/Account/Licenses" });

            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);

            if (license == null)
                return NotFound();

            var certificate = EnsureRecoveryCertificate(license);
            if (certificate == null)
            {
                TempData["Error"] = "Kurtarma kodu üretilemedi. Önce programdan bir kez online lisans etkinleştirme yapılmalı.";
                return RedirectToAction(nameof(Licenses));
            }

            if (certificate.Id == 0)
                await _context.SaveChangesAsync();

            try
            {
                await _emailSender.SendEmailAsync(
                    user.Email,
                    "NSX Lisans Kurtarma Kodunuz",
                    EmailTemplates.LicenseRecoveryCode(license, certificate.OfflineCode, GetPublicSiteUrl()));

                TempData["Success"] = "Kurtarma lisans kodu e-posta adresinize gönderildi.";
            }
            catch
            {
                TempData["Error"] = "Kurtarma kodu e-postası gönderilemedi. Lütfen daha sonra tekrar deneyin.";
            }

            return RedirectToAction(nameof(Licenses));
        }

        [HttpGet]
        public async Task<IActionResult> Downloads()
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = "/Account/Downloads" });

            var licenses = await _context.Licenses
                .Include(x => x.Product)
                .ThenInclude(x => x!.Files)
                .Where(x =>
                    x.UserId == user.Id &&
                    x.IsActive &&
                    (x.EndDate == null || x.EndDate >= DateTime.Now))
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            var demoDownloads = await _context.DemoDownloads
                .Include(x => x.Product)
                .ThenInclude(x => x!.Files)
                .Where(x => x.UserId == user.Id)
                .OrderByDescending(x => x.DownloadedAt)
                .ToListAsync();

            var licensedProductIds = licenses
                .Select(x => x.ProductId)
                .ToHashSet();

            var model = new CustomerDownloadsViewModel
            {
                Items = licenses
                    .Where(x => x.Product != null)
                    .GroupBy(x => x.ProductId)
                    .Select(g =>
                    {
                        var license = g.OrderByDescending(x => x.Id).First();
                        return new CustomerDownloadItemViewModel
                        {
                            ProductId = license.ProductId,
                            ProductName = license.Product?.Name ?? "Ürün",
                            LicenseKey = license.LicenseKey,
                            LicenseType = license.LicenseType,
                            EndDate = license.EndDate,
                            LicenseActive = license.IsActive && (license.EndDate == null || license.EndDate >= DateTime.Now),
                            Files = license.Product?.Files
                                .Where(f => f.FileType == "Program"
                                    || ((license.Product.YearlyPrice <= 0 && license.Product.LifetimePrice <= 0) && f.FileType == "Demo"))
                                .OrderByDescending(f => f.UploadedAt)
                                .ToList() ?? new List<ProductFile>()
                        };
                    })
                    .ToList(),
                DemoItems = demoDownloads
                    .Where(x => x.Product != null && !licensedProductIds.Contains(x.ProductId))
                    .GroupBy(x => x.ProductId)
                    .Select(g =>
                    {
                        var last = g.OrderByDescending(x => x.DownloadedAt).First();
                        return new CustomerDemoDownloadItemViewModel
                        {
                            ProductId = last.ProductId,
                            ProductName = last.Product?.Name ?? "Demo Program",
                            LastDownloadedAt = last.DownloadedAt,
                            DownloadCount = g.Count(),
                            Files = last.Product?.Files
                                .Where(f => f.FileType == "Demo")
                                .OrderByDescending(f => f.UploadedAt)
                                .ToList() ?? new List<ProductFile>()
                        };
                    })
                    .ToList()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> DownloadFile(int productId, int fileId)
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = $"/Account/DownloadFile?productId={productId}&fileId={fileId}" });

            var license = await _context.Licenses
                .Include(x => x.Product)
                .Where(x =>
                x.UserId == user.Id &&
                x.ProductId == productId &&
                x.IsActive &&
                (x.EndDate == null || x.EndDate >= DateTime.Now))
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            if (license == null)
            {
                TempData["Error"] = "Bu ürüne ait aktif lisansınız bulunamadı.";
                return RedirectToAction(nameof(Downloads));
            }

            var isFreeProduct = license.Product != null
                && license.Product.YearlyPrice <= 0
                && license.Product.LifetimePrice <= 0;
            var productCode = BuildProductCode(license.ProductCode ?? license.Product?.ProductCode ?? string.Empty);

            // Ücretsiz Veresiye kurulumu hesaptan tekrar indirilirken de mutlaka kişisel
            // aktivasyon tokenı üretilsin; ham setup dosyası hiçbir müşteri yolundan çıkmasın.
            if (string.Equals(productCode, "NSXVERESIYETAKIPPROFREE", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction("DownloadFree", "Store", new { productId });

            var file = await _context.ProductFiles
                .FirstOrDefaultAsync(x =>
                    x.Id == fileId
                    && x.ProductId == productId
                    && (x.FileType == "Program" || (isFreeProduct && x.FileType == "Demo")));

            if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
            {
                TempData["Error"] = "İndirme dosyası bulunamadı.";
                return RedirectToAction(nameof(Downloads));
            }

            var relativePath = file.FilePath
                .TrimStart('/', '\\')
                .Replace("/", Path.DirectorySeparatorChar.ToString())
                .Replace("\\", Path.DirectorySeparatorChar.ToString());

            var path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relativePath);

            if (!System.IO.File.Exists(path))
            {
                TempData["Error"] = "Dosya sunucuda bulunamadı.";
                return RedirectToAction(nameof(Downloads));
            }

            var downloadName = string.IsNullOrWhiteSpace(file.OriginalFileName)
                ? Path.GetFileName(path)
                : file.OriginalFileName;

            return PhysicalFile(path, "application/octet-stream", downloadName);
        }

        [HttpGet]
        public async Task<IActionResult> ChangePassword()
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = "/Account/ChangePassword" });

            return View(new ChangePasswordViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            var user = await GetCurrentCustomer();

            if (user == null)
                return RedirectToAction(nameof(Login), new { returnUrl = "/Account/ChangePassword" });

            model.CurrentPassword = model.CurrentPassword ?? string.Empty;
            model.NewPassword = model.NewPassword ?? string.Empty;
            model.ConfirmPassword = model.ConfirmPassword ?? string.Empty;

            if (!ModelState.IsValid)
                return View(model);

            if (!PasswordHasher.Verify(model.CurrentPassword, user.PasswordHash))
            {
                ModelState.AddModelError(nameof(model.CurrentPassword), "Mevcut şifre hatalı.");
                return View(model);
            }

            if (PasswordHasher.Verify(model.NewPassword, user.PasswordHash))
            {
                ModelState.AddModelError(nameof(model.NewPassword), "Yeni şifre mevcut şifreyle aynı olmamalıdır.");
                return View(model);
            }

            user.PasswordHash = PasswordHasher.Hash(model.NewPassword);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpireDate = null;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Şifreniz başarıyla değiştirildi.";
            return RedirectToAction(nameof(ChangePassword));
        }

        [HttpGet]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            TempData["Success"] = "Çıkış yapıldı.";
            return RedirectToAction("Index", "Home");
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
                ? BuildProductCode(license.Product?.Slug ?? license.Product?.Name ?? "NSX")
                : BuildProductCode(license.ProductCode);

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

        private static string BuildProductCode(string value)
        {
            return new string((value ?? "NSX")
                .Trim()
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private string GetPublicSiteUrl()
        {
            var configuredSiteUrl = _configuration["Site:Url"];
            if (!string.IsNullOrWhiteSpace(configuredSiteUrl))
                return configuredSiteUrl.TrimEnd('/');

            var host = Request.Host.Value ?? string.Empty;
            if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                return "https://www.nsxyazilim.com";

            return $"{Request.Scheme}://{Request.Host}".TrimEnd('/');
        }

        private static void NormalizeRegisterModel(AccountRegisterViewModel model)
        {
            model.FullName = (model.FullName ?? string.Empty).Trim();
            model.Email = (model.Email ?? string.Empty).Trim().ToLowerInvariant();
            model.Phone = string.IsNullOrWhiteSpace(model.Phone) ? null : model.Phone.Trim();
            model.Password = model.Password ?? string.Empty;
            model.ConfirmPassword = model.ConfirmPassword ?? string.Empty;
        }

        private (string? IpAddress, bool ForceLookup) UpdateUserActivity(User user)
        {
            var ipAddress = _clientIpService.GetClientIp(HttpContext);
            var previousIpAddress = user.LastIpAddress;

            if (!string.IsNullOrWhiteSpace(ipAddress))
                user.LastIpAddress = ipAddress;

            user.LastLoginAt = DateTime.Now;

            return (
                ipAddress,
                !string.IsNullOrWhiteSpace(ipAddress)
                && !string.Equals(previousIpAddress, ipAddress, StringComparison.OrdinalIgnoreCase));
        }

        private void QueueUserLocation(User user, (string? IpAddress, bool ForceLookup) work)
        {
            if (user.Id <= 0 || string.IsNullOrWhiteSpace(work.IpAddress) || !_clientIpService.IsPublicIp(work.IpAddress))
                return;

            _userLocationQueue.TryQueue(new UserLocationUpdateItem(user.Id, work.IpAddress, work.ForceLookup));
        }

        private async Task<User?> GetCurrentCustomer()
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

        private bool IsCustomerLoggedIn()
        {
            return User?.Identity?.IsAuthenticated == true &&
                   User.IsInRole("User");
        }

        private async Task<string> ResolveUiLanguageCodeAsync(string? uiLang)
        {
            var language = await _localization.FindActiveLanguageAsync(uiLang, HttpContext.RequestAborted)
                ?? await _localization.GetCurrentLanguageAsync(HttpContext.RequestAborted);

            if (!Response.HasStarted)
                _localization.WriteLanguageCookie(Response, Request, language.Code);

            return language.Code;
        }

        private async Task ApplyPreferredLanguageAsync(User user, string? uiLang = null)
        {
            if (user.Id <= 0)
                return;

            try
            {
                var activeLanguages = await _localization.GetActiveLanguagesAsync(HttpContext.RequestAborted);
                var preference = await _context.UserLanguagePreferences
                    .FirstOrDefaultAsync(x => x.UserId == user.Id, HttpContext.RequestAborted);

                // Login/Register ekranında görülen dil, bu oturumdaki en güncel tercihtir.
                // Eski hesap tercihi yeni seçimi geri ezmesin; aynı dili hesaba da yaz.
                var flowLanguage = !string.IsNullOrWhiteSpace(uiLang)
                    ? activeLanguages.FirstOrDefault(x =>
                        string.Equals(x.Code, uiLang, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.UrlCode, uiLang, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (flowLanguage != null)
                {
                    _localization.WriteLanguageCookie(Response, Request, flowLanguage.Code);
                    if (preference == null)
                    {
                        _context.UserLanguagePreferences.Add(new UserLanguagePreference
                        {
                            UserId = user.Id,
                            LanguageCode = flowLanguage.Code,
                            UpdatedAt = DateTime.Now
                        });
                    }
                    else
                    {
                        preference.LanguageCode = flowLanguage.Code;
                        preference.UpdatedAt = DateTime.Now;
                    }

                    return;
                }

                var cookieCode = Request.Cookies[SiteLocalizationService.LanguageCookieName];
                var cookieLanguage = !string.IsNullOrWhiteSpace(cookieCode)
                    ? activeLanguages.FirstOrDefault(x => string.Equals(x.Code, cookieCode, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (cookieLanguage != null)
                {
                    _localization.WriteLanguageCookie(Response, Request, cookieLanguage.Code);
                    if (preference == null)
                    {
                        _context.UserLanguagePreferences.Add(new UserLanguagePreference
                        {
                            UserId = user.Id,
                            LanguageCode = cookieLanguage.Code,
                            UpdatedAt = DateTime.Now
                        });
                    }
                    else
                    {
                        preference.LanguageCode = cookieLanguage.Code;
                        preference.UpdatedAt = DateTime.Now;
                    }

                    return;
                }

                var savedLanguage = preference == null
                    ? null
                    : activeLanguages.FirstOrDefault(x => string.Equals(x.Code, preference.LanguageCode, StringComparison.OrdinalIgnoreCase));

                if (savedLanguage != null)
                    _localization.WriteLanguageCookie(Response, Request, savedLanguage.Code);
            }
            catch
            {
                // Dil tercihi tablosu geçici olarak erişilemezse giriş akışını bozma.
                // Mevcut cookie tercihi bu cihazda kullanılmaya devam eder.
            }
        }

        private async Task SignInCustomer(User user, bool rememberMe = true)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.FullName ?? string.Empty),
                new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
                new Claim(ClaimTypes.Role, user.Role ?? "User")
            };

            if (!string.IsNullOrWhiteSpace(user.Phone))
            {
                claims.Add(new Claim(ClaimTypes.MobilePhone, user.Phone));
            }

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = rememberMe,
                    ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(14) : null
                });
        }
    }
}
