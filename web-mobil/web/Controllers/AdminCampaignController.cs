using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.ViewModels;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin")]
    public class AdminCampaignController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly LocalizationTranslationQueueService _translationQueue;

        public AdminCampaignController(ApplicationDbContext context, LocalizationTranslationQueueService translationQueue)
        {
            _context = context;
            _translationQueue = translationQueue;
        }

        [HttpGet("Campaigns")]
        public async Task<IActionResult> Campaigns()
        {
            return View("~/Views/Admin/Campaigns.cshtml", await CreateCampaignListModelAsync());
        }

        [HttpGet("CampaignEdit/{id:int}")]
        public async Task<IActionResult> CampaignEdit(int id)
        {
            var coupon = await _context.CampaignCoupons.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (coupon == null)
            {
                TempData["Error"] = "Kupon bulunamadı.";
                return RedirectToAction(nameof(Campaigns));
            }

            var model = await CreateCampaignListModelAsync();
            model.Form = new CampaignCouponFormViewModel
            {
                Id = coupon.Id,
                Code = coupon.Code,
                Title = coupon.Title,
                DiscountType = NormalizeDiscountType(coupon.DiscountType),
                DiscountValue = coupon.DiscountValue,
                MinimumCartAmount = coupon.MinimumCartAmount,
                UsageLimit = coupon.UsageLimit,
                StartDate = coupon.StartDate,
                EndDate = coupon.EndDate,
                IsActive = coupon.IsActive
            };

            return View("~/Views/Admin/Campaigns.cshtml", model);
        }

        [HttpPost("CampaignSave")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CampaignSave(CampaignCouponFormViewModel model)
        {
            Normalize(model);

            if (model.DiscountType == "Percent" && model.DiscountValue > 100)
                ModelState.AddModelError(nameof(model.DiscountValue), "Yüzde indirim 100'den büyük olamaz.");

            if (model.EndDate.HasValue && model.StartDate.HasValue && model.EndDate.Value < model.StartDate.Value)
                ModelState.AddModelError(nameof(model.EndDate), "Bitiş tarihi başlangıç tarihinden küçük olamaz.");

            if (!ModelState.IsValid)
            {
                var listModel = await CreateCampaignListModelAsync();
                listModel.Form = model;
                return View("~/Views/Admin/Campaigns.cshtml", listModel);
            }

            // ÖNEMLİ:
            // Code alanında unique index var. Form bazen Id=0 ile gelebiliyor;
            // bu durumda aynı kodu yeniden INSERT etmek MySQL'de duplicate hatası veriyordu.
            // Artık aynı Code varsa yeni kayıt açmak yerine mevcut kayıt güncellenir.
            CampaignCoupon? sameCodeCoupon = await _context.CampaignCoupons
                .FirstOrDefaultAsync(x => x.Code == model.Code);

            CampaignCoupon coupon;
            CampaignCoupon? oldCouponToClose = null;

            if (sameCodeCoupon != null)
            {
                // Aynı kupon kodu veritabanında zaten varsa duplicate hatasına düşme.
                // Admin formunda istenen son düzenleme hangi Id'den gelirse gelsin,
                // bu kodun tek gerçek kaydını güncelle.
                coupon = sameCodeCoupon;

                if (model.Id > 0 && model.Id != sameCodeCoupon.Id)
                {
                    oldCouponToClose = await _context.CampaignCoupons.FirstOrDefaultAsync(x => x.Id == model.Id);
                }
            }
            else if (model.Id > 0)
            {
                var existingCoupon = await _context.CampaignCoupons.FirstOrDefaultAsync(x => x.Id == model.Id);
                if (existingCoupon == null)
                {
                    TempData["Error"] = "Güncellenecek kupon bulunamadı.";
                    return RedirectToAction(nameof(Campaigns));
                }

                coupon = existingCoupon;
            }
            else
            {
                coupon = new CampaignCoupon { CreatedAt = DateTime.Now };
                _context.CampaignCoupons.Add(coupon);
            }

            if (oldCouponToClose != null)
            {
                oldCouponToClose.IsActive = false;
                oldCouponToClose.IsDeleted = true;
            }

            coupon.Code = model.Code;
            coupon.Title = model.Title;
            coupon.DiscountType = model.DiscountType;
            coupon.DiscountValue = model.DiscountValue;
            coupon.MinimumCartAmount = model.MinimumCartAmount;
            coupon.UsageLimit = model.UsageLimit;
            coupon.StartDate = model.StartDate;
            coupon.EndDate = model.EndDate;
            coupon.IsActive = model.IsActive;
            coupon.IsDeleted = false;

            // Admin kampanya düzenleme sadece CampaignCoupons tablosuna kayıt yapar.
            // Ana sayfa kampanya bilgisini doğrudan CampaignCoupons üzerinden okur.
            // Campaigns tablosuna zorla senkron yapmak eski/veritabanı farklı kurulumlarda /Admin/CampaignSave'i bozuyordu.
            await _context.SaveChangesAsync();
            var queued = await _translationQueue.QueueCampaignCouponAsync(coupon, HttpContext.RequestAborted);

            TempData["Success"] = $"Kupon kaydedildi. {queued} dil çevirisi kuyruğa alındı.";
            return RedirectToAction(nameof(Campaigns));
        }

        [HttpPost("CampaignDelete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CampaignDelete(int id)
        {
            var coupon = await _context.CampaignCoupons.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (coupon == null)
            {
                TempData["Error"] = "Kupon bulunamadı.";
                return RedirectToAction(nameof(Campaigns));
            }

            coupon.IsDeleted = true;
            coupon.IsActive = false;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Kupon silindi.";
            return RedirectToAction(nameof(Campaigns));
        }

        [HttpPost("CampaignToggle")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CampaignToggle(int id)
        {
            var coupon = await _context.CampaignCoupons.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
            if (coupon == null)
            {
                TempData["Error"] = "Kupon bulunamadı.";
                return RedirectToAction(nameof(Campaigns));
            }

            coupon.IsActive = !coupon.IsActive;

            await _context.SaveChangesAsync();

            TempData["Success"] = coupon.IsActive ? "Kupon aktif edildi." : "Kupon pasife alındı.";
            return RedirectToAction(nameof(Campaigns));
        }

        private async Task<CampaignCouponListViewModel> CreateCampaignListModelAsync()
        {
            return new CampaignCouponListViewModel
            {
                Form = new CampaignCouponFormViewModel
                {
                    IsActive = true,
                    DiscountType = "Percent",
                    DiscountValue = 10,
                    MinimumCartAmount = 0,
                    StartDate = DateTime.Now,
                    EndDate = DateTime.Now.AddMonths(1)
                },
                Coupons = await _context.CampaignCoupons
                    .Where(x => !x.IsDeleted)
                    .OrderByDescending(x => x.Id)
                    .ToListAsync()
            };
        }

        private static void Normalize(CampaignCouponFormViewModel model)
        {
            model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
            model.Title = (model.Title ?? string.Empty).Trim();
            model.DiscountType = NormalizeDiscountType(model.DiscountType);
            if (model.MinimumCartAmount.HasValue && model.MinimumCartAmount.Value < 0)
                model.MinimumCartAmount = 0;
            if (model.UsageLimit.HasValue && model.UsageLimit.Value <= 0)
                model.UsageLimit = null;
        }

        private static string NormalizeDiscountType(string? value)
        {
            return string.Equals(value, "Fixed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Amount", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "FixedAmount", StringComparison.OrdinalIgnoreCase)
                ? "Fixed"
                : "Percent";
        }
    }
}
