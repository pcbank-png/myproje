using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [Route("language")]
    public sealed class LanguageController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly SiteLocalizationService _localization;
        private readonly SeoLanguageUrlService _seoUrls;
        private readonly ILogger<LanguageController> _logger;

        public LanguageController(
            ApplicationDbContext db,
            SiteLocalizationService localization,
            SeoLanguageUrlService seoUrls,
            ILogger<LanguageController> logger)
        {
            _db = db;
            _localization = localization;
            _seoUrls = seoUrls;
            _logger = logger;
        }

        [HttpGet("set")]
        public Task<IActionResult> SetGet(string? code, string? returnUrl = null)
            => SetCoreAsync(code, returnUrl);

        [HttpPost("set")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Set(string? code, string? returnUrl = null)
            => SetCoreAsync(code, returnUrl);

        private async Task<IActionResult> SetCoreAsync(string? code, string? returnUrl)
        {
            var language = await _localization.FindActiveLanguageAsync(code, HttpContext.RequestAborted);
            if (language == null)
                return RedirectToLocal(returnUrl);

            _localization.WriteLanguageCookie(Response, Request, language.Code);

            try
            {
                if (User.Identity?.IsAuthenticated == true
                    && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                {
                    var preference = await _db.UserLanguagePreferences
                        .FirstOrDefaultAsync(x => x.UserId == userId, HttpContext.RequestAborted);
                    if (preference == null)
                    {
                        _db.UserLanguagePreferences.Add(new NSYazilim.Web.Models.UserLanguagePreference
                        {
                            UserId = userId,
                            LanguageCode = language.Code,
                            UpdatedAt = DateTime.Now
                        });
                    }
                    else if (!string.Equals(preference.LanguageCode, language.Code, StringComparison.OrdinalIgnoreCase))
                    {
                        preference.LanguageCode = language.Code;
                        preference.UpdatedAt = DateTime.Now;
                    }
    
                    await _db.SaveChangesAsync(HttpContext.RequestAborted);
                }
            }
            catch (Exception ex)
            {
                // DB tercih kaydı başarısız olsa bile cookie seçimi çalışmaya devam etsin.
                _logger.LogWarning(ex, "Kullanıcının dil tercihi veritabanına kaydedilemedi.");
            }

            var localizedReturnUrl = await _seoUrls.BuildLocalizedReturnUrlAsync(
                returnUrl,
                language,
                HttpContext.RequestAborted);

            return RedirectToLocal(localizedReturnUrl);
        }

        private IActionResult RedirectToLocal(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return Redirect("/");
        }
    }
}
