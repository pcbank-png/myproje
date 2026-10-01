using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.ViewModels;
using NSYazilim.Web.Services;
using System.Diagnostics;

namespace NSYazilim.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailSender _emailSender;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;

        public HomeController(ApplicationDbContext context, IEmailSender emailSender, IConfiguration configuration, IWebHostEnvironment environment)
        {
            _context = context;
            _emailSender = emailSender;
            _configuration = configuration;
            _environment = environment;
        }

        [HttpGet("")]
        [HttpGet("home")]
        [HttpGet("home/index")]
        public async Task<IActionResult> Index()
        {
            var requestPath = Request.Path.Value ?? "/";
            if (!string.Equals(requestPath, "/", StringComparison.Ordinal))
                return RedirectPermanent($"{Request.PathBase}/");

            var activeProductCount = await _context.Products
                .AsNoTracking()
                .CountAsync(x => !x.IsDeleted && x.IsActive);

            var products = await _context.Products
                .AsNoTracking()
                .Include(x => x.Images
                    .OrderByDescending(image => image.IsMain)
                    .ThenBy(image => image.SortOrder)
                    .ThenBy(image => image.Id)
                    .Take(1))
                .Where(x => !x.IsDeleted && x.IsActive)
                .OrderByDescending(x => x.CreatedAt)
                .Take(8)
                .ToListAsync();

            var ratingProductIds = products.Select(x => x.Id).ToList();

            var ratingRaw = await _context.ProductComments
                .Where(x => ratingProductIds.Contains(x.ProductId) && x.IsApproved && !x.IsDeleted)
                .GroupBy(x => x.ProductId)
                .Select(g => new
                {
                    ProductId = g.Key,
                    Average = g.Average(x => x.Rating)
                })
                .ToListAsync();

            ViewBag.RatingAverage = ratingRaw.ToDictionary(x => x.ProductId, x => x.Average);

            var activeCampaigns = await GetActiveCampaignsAsync();
            ViewData["PreloadedActiveCampaigns"] = activeCampaigns;

            var model = new HomePageViewModel
            {
                FeaturedProducts = products,
                ActiveCampaigns = activeCampaigns,
                ActiveProductCount = activeProductCount
            };

            return View(model);
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
                // Eski kurulumlarda CampaignCoupons tablosu yoksa ana sayfa bozulmasın.
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
                // Campaign tablosu yoksa ana sayfa yine çalışmaya devam etsin.
            }

            return campaigns
                .Where(x => x.DiscountValue > 0)
                .Where(x => !string.IsNullOrWhiteSpace(x.Code) || !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Code) ? $"ID:{x.Id}" : x.Code.Trim().ToUpperInvariant())
                .Select(g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First())
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.DiscountValue)
                .ToList();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FooterContact(string footerMail, string footerMessage)
        {
            footerMail = (footerMail ?? string.Empty).Trim();
            footerMessage = (footerMessage ?? string.Empty).Trim();

            var returnUrl = Request.Headers["Referer"].ToString();
            if (string.IsNullOrWhiteSpace(returnUrl))
                returnUrl = "/";

            if (string.IsNullOrWhiteSpace(footerMail) || string.IsNullOrWhiteSpace(footerMessage))
            {
                TempData["Error"] = "Lütfen mail adresinizi ve mesajınızı yazın.";
                return Redirect(returnUrl);
            }

            var html = EmailTemplates.FooterContactMessage(footerMail, footerMessage);

            var toEmail = _configuration["EmailSettings:ToEmail"]
                ?? _configuration["MailSettings:ToEmail"]
                ?? _configuration["EmailSettings:FromEmail"]
                ?? _configuration["MailSettings:FromEmail"]
                ?? _configuration["EmailSettings:UserName"]
                ?? _configuration["MailSettings:UserName"];

            var log = new MailLog
            {
                ToEmail = string.IsNullOrWhiteSpace(toEmail) ? "admin" : toEmail,
                ToName = footerMail,
                Subject = "Footer iletişim mesajı",
                Body = footerMessage,
                MailType = "FooterContact",
                CreatedAt = DateTime.Now
            };

            try
            {
                if (string.IsNullOrWhiteSpace(toEmail))
                    throw new InvalidOperationException("EmailSettings:ToEmail ayarı bulunamadı.");

                await _emailSender.SendEmailAsync(toEmail, "NSX Footer İletişim Mesajı", html);
                log.IsSuccess = true;
                TempData["Success"] = "Mesajınız gönderildi. En kısa sürede dönüş yapılacaktır.";
            }
            catch (Exception ex)
            {
                log.IsSuccess = false;
                log.ErrorMessage = ex.Message;
                TempData["Error"] = "Mesaj kaydedildi fakat mail gönderilemedi. Lütfen SMTP ayarlarını kontrol edin veya WhatsApp destek hattını kullanın.";
            }

            try
            {
                _context.MailLogs.Add(log);
                await _context.SaveChangesAsync();
            }
            catch
            {
                // Mesaj gönderim akışını bozmasın.
            }

            return Redirect(returnUrl);
        }



        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LiveSupportMessage(string contact, string message)
        {
            contact = (contact ?? string.Empty).Trim();
            message = (message ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(message))
            {
                return Json(new { ok = false, message = "Lütfen mesajınızı yazın." });
            }

            if (message.Length > 700)
                message = message.Substring(0, 700);

            if (contact.Length > 120)
                contact = contact.Substring(0, 120);

            var log = new MailLog
            {
                ToEmail = string.IsNullOrWhiteSpace(contact) ? "Canlı Destek" : contact,
                ToName = string.IsNullOrWhiteSpace(contact) ? "Ziyaretçi" : contact,
                Subject = "Canlı destek mesajı",
                Body = message,
                MailType = "LiveSupport",
                IsSuccess = true,
                CreatedAt = DateTime.Now
            };

            try
            {
                _context.MailLogs.Add(log);
                await _context.SaveChangesAsync();

                return Json(new
                {
                    ok = true,
                    message = "Mesajınız alındı. NSX ekibi en kısa sürede dönüş yapacak."
                });
            }
            catch
            {
                return Json(new
                {
                    ok = false,
                    message = "Mesaj alınamadı. Lütfen tekrar deneyin veya WhatsApp destek hattını kullanın."
                });
            }
        }

        [HttpGet("kvkk")]
        [HttpGet("home/kvkk")]
        public IActionResult Kvkk()
        {
            return CanonicalView("/kvkk", nameof(Kvkk));
        }

        [HttpGet("iade-politikasi")]
        [HttpGet("home/iadepolitikasi")]
        public IActionResult IadePolitikasi()
        {
            return CanonicalView("/iade-politikasi", nameof(IadePolitikasi));
        }

        [HttpGet("iletisim")]
        [HttpGet("home/iletisim")]
        public IActionResult Iletisim()
        {
            ViewBag.SupportProgramAvailable = System.IO.File.Exists(Path.Combine(
                _environment.ContentRootPath,
                "App_Data",
                "SupportProgram",
                "NSXUzaktanYardimLite.exe"));

            return CanonicalView("/iletisim", nameof(Iletisim));
        }

        [HttpGet("uzaktan-yardim-indir")]
        public IActionResult DownloadRemoteSupport()
        {
            var filePath = Path.Combine(_environment.ContentRootPath, "App_Data", "SupportProgram", "NSXUzaktanYardimLite.exe");
            if (!System.IO.File.Exists(filePath))
                return NotFound();

            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";

            return PhysicalFile(
                filePath,
                "application/octet-stream",
                "NSX-Uzaktan-Yardim.exe",
                enableRangeProcessing: true);
        }

        [HttpGet("hakkimizda")]
        public IActionResult Hakkimizda()
        {
            return CanonicalView("/hakkimizda", nameof(Hakkimizda));
        }

        [HttpGet("sikca-sorulan-sorular")]
        public IActionResult SikcaSorulanSorular()
        {
            return CanonicalView("/sikca-sorulan-sorular", nameof(SikcaSorulanSorular));
        }

        [HttpGet("gizlilik-politikasi")]
        [HttpGet("home/privacy")]
        public IActionResult Privacy()
        {
            return CanonicalView("/gizlilik-politikasi", nameof(Privacy));
        }

        [HttpGet("hata")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            ViewData["Title"] = "Bir sorun oluştu";
            ViewData["MetaDescription"] = "İşleminiz tamamlanırken beklenmeyen bir sorun oluştu.";
            ViewData["Robots"] = "noindex, nofollow, noarchive";
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        [HttpGet("sayfa-bulunamadi")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult NotFoundPage()
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            ViewData["Title"] = "Sayfa bulunamadı";
            ViewData["MetaDescription"] = "Aradığınız sayfa taşınmış, kaldırılmış veya adresi değişmiş olabilir.";
            ViewData["Robots"] = "noindex, nofollow, noarchive";
            return View("NotFound");
        }

        private IActionResult CanonicalView(string canonicalPath, string viewName)
        {
            var requestPath = Request.Path.Value ?? "/";
            if (!string.Equals(requestPath, canonicalPath, StringComparison.Ordinal))
                return RedirectPermanent($"{Request.PathBase}{canonicalPath}");

            return View(viewName);
        }
    }
}
