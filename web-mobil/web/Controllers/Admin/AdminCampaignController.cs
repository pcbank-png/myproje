using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers.Admin
{
    [Authorize(Roles = "Admin")]
    public class AdminCampaignController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AdminCampaignController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var campaigns = await _context.Set<Campaign>()
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            return View(campaigns);
        }

        [HttpGet]
        public IActionResult Create()
        {
            return View(new CampaignFormViewModel
            {
                IsActive = true,
                DiscountType = "Percent",
                StartDate = DateTime.Today,
                EndDate = DateTime.Today.AddMonths(1)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CampaignFormViewModel model)
        {
            Normalize(model);

            if (!ModelState.IsValid)
                return View(model);

            if (model.DiscountType == "Percent" && model.DiscountValue > 100)
            {
                ModelState.AddModelError(nameof(model.DiscountValue), "Yüzde indirim 100'den büyük olamaz.");
                return View(model);
            }

            var campaign = new Campaign
            {
                Code = model.Code,
                Name = model.Name,
                Description = model.Description,
                IsActive = model.IsActive,
                MinimumCartTotal = model.MinimumCartTotal,
                DiscountValue = model.DiscountValue,
                DiscountType = model.DiscountType,
                StartDate = model.StartDate,
                EndDate = model.EndDate,
                CreatedAt = DateTime.Now
            };

            _context.Set<Campaign>().Add(campaign);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Kampanya oluşturuldu.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var campaign = await _context.Set<Campaign>().FindAsync(id);
            if (campaign == null)
                return NotFound();

            var model = new CampaignFormViewModel
            {
                Id = campaign.Id,
                Code = campaign.Code,
                Name = campaign.Name,
                Description = campaign.Description,
                IsActive = campaign.IsActive,
                MinimumCartTotal = campaign.MinimumCartTotal,
                DiscountValue = campaign.DiscountValue,
                DiscountType = NormalizeDiscountType(campaign.DiscountType),
                StartDate = campaign.StartDate,
                EndDate = campaign.EndDate
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(CampaignFormViewModel model)
        {
            Normalize(model);

            if (!ModelState.IsValid)
                return View(model);

            if (model.DiscountType == "Percent" && model.DiscountValue > 100)
            {
                ModelState.AddModelError(nameof(model.DiscountValue), "Yüzde indirim 100'den büyük olamaz.");
                return View(model);
            }

            var campaign = await _context.Set<Campaign>().FindAsync(model.Id);
            if (campaign == null)
                return NotFound();

            campaign.Code = model.Code;
            campaign.Name = model.Name;
            campaign.Description = model.Description;
            campaign.IsActive = model.IsActive;
            campaign.MinimumCartTotal = model.MinimumCartTotal;
            campaign.DiscountValue = model.DiscountValue;
            campaign.DiscountType = model.DiscountType;
            campaign.StartDate = model.StartDate;
            campaign.EndDate = model.EndDate;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Kampanya güncellendi.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var campaign = await _context.Set<Campaign>().FindAsync(id);
            if (campaign == null)
                return NotFound();

            _context.Set<Campaign>().Remove(campaign);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Kampanya silindi.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var campaign = await _context.Set<Campaign>().FindAsync(id);
            if (campaign == null)
                return NotFound();

            campaign.IsActive = !campaign.IsActive;
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        private static void Normalize(CampaignFormViewModel model)
        {
            model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
            model.Name = (model.Name ?? string.Empty).Trim();
            model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
            model.DiscountType = NormalizeDiscountType(model.DiscountType);
        }

        private static string NormalizeDiscountType(string? discountType)
        {
            return string.Equals(discountType, "Fixed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(discountType, "FixedAmount", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(discountType, "Amount", StringComparison.OrdinalIgnoreCase)
                ? "Fixed"
                : "Percent";
        }
    }
}
