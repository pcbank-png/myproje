using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.ViewComponents
{
    public sealed class ActiveCampaignViewComponent : ViewComponent
    {
        private readonly ApplicationDbContext _context;

        public ActiveCampaignViewComponent(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync(IEnumerable<Campaign>? preloadedCampaigns = null)
        {
            var now = DateTime.Now;
            var candidates = new List<ActiveCampaignCandidate>();

            if (preloadedCampaigns != null)
            {
                candidates.AddRange(preloadedCampaigns
                    .Where(x => x.IsActive)
                    .Where(x => x.StartDate == null || x.StartDate <= now)
                    .Where(x => x.EndDate == null || x.EndDate >= now)
                    .Select(x => new ActiveCampaignCandidate
                    {
                        Name = x.Name,
                        Description = x.Description ?? string.Empty,
                        DiscountType = x.DiscountType,
                        DiscountValue = x.DiscountValue,
                        MinimumAmount = x.MinimumCartTotal,
                        EndDate = x.EndDate,
                        CreatedAt = x.CreatedAt
                    }));
            }
            else
            {
            try
            {
                candidates.AddRange(await _context.CampaignCoupons
                    .AsNoTracking()
                    .Where(x => x.IsActive && !x.IsDeleted)
                    .Where(x => x.StartDate == null || x.StartDate <= now)
                    .Where(x => x.EndDate == null || x.EndDate >= now)
                    .Where(x => x.UsageLimit == null || x.UsageLimit <= 0 || x.UsedCount < x.UsageLimit)
                    .Select(x => new ActiveCampaignCandidate
                    {
                        Name = x.Title,
                        Description = x.Title,
                        DiscountType = x.DiscountType,
                        DiscountValue = x.DiscountValue,
                        MinimumAmount = x.MinimumCartAmount ?? 0,
                        EndDate = x.EndDate,
                        CreatedAt = x.CreatedAt
                    })
                    .ToListAsync());
            }
            catch
            {
                // Eski veritabanlarında kupon tablosu bulunmayabilir.
            }

            try
            {
                candidates.AddRange(await _context.Campaigns
                    .AsNoTracking()
                    .Where(x => x.IsActive)
                    .Where(x => x.StartDate == null || x.StartDate <= now)
                    .Where(x => x.EndDate == null || x.EndDate >= now)
                    .Select(x => new ActiveCampaignCandidate
                    {
                        Name = x.Name,
                        Description = x.Description ?? string.Empty,
                        DiscountType = x.DiscountType,
                        DiscountValue = x.DiscountValue,
                        MinimumAmount = x.MinimumCartTotal,
                        EndDate = x.EndDate,
                        CreatedAt = x.CreatedAt
                    })
                    .ToListAsync());
            }
            catch
            {
                // Eski veritabanlarında kampanya tablosu bulunmayabilir.
            }
            }

            var campaign = candidates
                .Where(x => x.DiscountValue > 0 && !string.IsNullOrWhiteSpace(x.Name))
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.DiscountValue)
                .FirstOrDefault();

            if (campaign == null)
                return Content(string.Empty);

            var fixedDiscount = string.Equals(campaign.DiscountType, "Fixed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(campaign.DiscountType, "FixedAmount", StringComparison.OrdinalIgnoreCase)
                || string.Equals(campaign.DiscountType, "Amount", StringComparison.OrdinalIgnoreCase);

            return View(new ActiveCampaignBannerViewModel
            {
                Name = campaign.Name.Trim(),
                Description = campaign.Description.Trim(),
                DiscountText = fixedDiscount
                    ? $"{campaign.DiscountValue:N0} TL İNDİRİM"
                    : $"%{campaign.DiscountValue:N0} İNDİRİM",
                MinimumAmount = campaign.MinimumAmount,
                EndDate = campaign.EndDate
            });
        }

        private sealed class ActiveCampaignCandidate
        {
            public string Name { get; init; } = string.Empty;
            public string Description { get; init; } = string.Empty;
            public string DiscountType { get; init; } = "Percent";
            public decimal DiscountValue { get; init; }
            public decimal MinimumAmount { get; init; }
            public DateTime? EndDate { get; init; }
            public DateTime CreatedAt { get; init; }
        }
    }
}
