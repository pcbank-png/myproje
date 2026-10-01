using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminCampaignsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly LocalizationTranslationQueueService _translationQueue;

        public AdminCampaignsController(ApplicationDbContext context, LocalizationTranslationQueueService translationQueue)
        {
            _context = context;
            _translationQueue = translationQueue;
        }

        public IActionResult Index()
        {
            var campaigns = _context.Set<Campaign>()
                .AsNoTracking()
                .OrderByDescending(x => x.Id)
                .ToList();

            return View(campaigns);
        }

        [HttpGet]
        public IActionResult Create()
        {
            return View(new Campaign
            {
                IsActive = true,
                DiscountType = "Percent",
                StartDate = DateTime.Today,
                EndDate = DateTime.Today.AddMonths(1)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Campaign campaign)
        {
            if (!ModelState.IsValid)
            {
                return View(campaign);
            }

            NormalizeCampaign(campaign);
            campaign.CreatedAt = DateTime.Now;
            _context.Set<Campaign>().Add(campaign);
            SyncCouponFromCampaign(campaign);
            await _context.SaveChangesAsync();
            var queued = await _translationQueue.QueueCampaignAsync(campaign, HttpContext.RequestAborted);

            TempData["Success"] = $"Kampanya oluşturuldu. {queued} dil çevirisi kuyruğa alındı.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult Edit(int id)
        {
            var campaign = _context.Set<Campaign>().FirstOrDefault(x => x.Id == id);
            if (campaign == null)
            {
                return NotFound();
            }

            return View(campaign);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Campaign campaign)
        {
            if (!ModelState.IsValid)
            {
                return View(campaign);
            }

            var dbCampaign = _context.Set<Campaign>().FirstOrDefault(x => x.Id == campaign.Id);
            if (dbCampaign == null)
            {
                return NotFound();
            }

            NormalizeCampaign(campaign);

            dbCampaign.Code = campaign.Code;
            dbCampaign.Name = campaign.Name;
            dbCampaign.Description = campaign.Description;
            dbCampaign.IsActive = campaign.IsActive;
            dbCampaign.MinimumCartTotal = campaign.MinimumCartTotal;
            dbCampaign.DiscountType = campaign.DiscountType;
            dbCampaign.DiscountValue = campaign.DiscountValue;
            dbCampaign.StartDate = campaign.StartDate;
            dbCampaign.EndDate = campaign.EndDate;

            SyncCouponFromCampaign(dbCampaign);
            await _context.SaveChangesAsync();
            var queued = await _translationQueue.QueueCampaignAsync(dbCampaign, HttpContext.RequestAborted);

            TempData["Success"] = $"Kampanya güncellendi. {queued} dil çevirisi kuyruğa alındı.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var campaign = _context.Set<Campaign>().FirstOrDefault(x => x.Id == id);
            if (campaign == null)
            {
                return NotFound();
            }

            campaign.IsActive = false;

            if (!string.IsNullOrWhiteSpace(campaign.Code))
            {
                var coupon = _context.CampaignCoupons.FirstOrDefault(x => x.Code == campaign.Code && !x.IsDeleted);
                if (coupon != null)
                {
                    coupon.IsActive = false;
                    coupon.IsDeleted = true;
                }
            }

            _context.Set<Campaign>().Remove(campaign);
            _context.SaveChanges();

            TempData["Success"] = "Kampanya silindi.";
            return RedirectToAction(nameof(Index));
        }

        private void NormalizeCampaign(Campaign campaign)
        {
            campaign.Code = (campaign.Code ?? string.Empty).Trim().ToUpperInvariant();
            campaign.Name = (campaign.Name ?? string.Empty).Trim();
            campaign.Description = string.IsNullOrWhiteSpace(campaign.Description) ? campaign.Name : campaign.Description.Trim();
            campaign.DiscountType = NormalizeDiscountType(campaign.DiscountType);

            if (campaign.MinimumCartTotal < 0)
                campaign.MinimumCartTotal = 0;
        }

        private string NormalizeDiscountType(string? value)
        {
            return string.Equals(value, "Fixed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Amount", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "FixedAmount", StringComparison.OrdinalIgnoreCase)
                ? "Fixed"
                : "Percent";
        }

        private void SyncCouponFromCampaign(Campaign campaign)
        {
            if (string.IsNullOrWhiteSpace(campaign.Code))
                return;

            var coupon = _context.CampaignCoupons.FirstOrDefault(x => x.Code == campaign.Code);
            if (coupon == null)
            {
                coupon = new CampaignCoupon { CreatedAt = DateTime.Now };
                _context.CampaignCoupons.Add(coupon);
            }

            coupon.Code = campaign.Code;
            coupon.Title = campaign.Name;
            coupon.DiscountType = campaign.DiscountType;
            coupon.DiscountValue = campaign.DiscountValue;
            coupon.MinimumCartAmount = campaign.MinimumCartTotal;
            coupon.StartDate = campaign.StartDate;
            coupon.EndDate = campaign.EndDate;
            coupon.IsActive = campaign.IsActive;
            coupon.IsDeleted = false;
        }
    }
}
