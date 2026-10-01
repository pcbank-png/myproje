using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Services
{
    public class CampaignDiscountService
    {
        private readonly ApplicationDbContext _context;

        public CampaignDiscountService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<CampaignDiscountResult> CalculateBestDiscountAsync(decimal subTotal)
        {
            subTotal = NormalizeAmount(subTotal);

            if (subTotal <= 0)
                return CampaignDiscountResult.Empty(subTotal);

            var campaigns = await GetActiveCampaignsAsync();

            return campaigns
                .Where(x => x.MinimumCartTotal <= 0 || x.MinimumCartTotal <= subTotal)
                .Select(x => CreateResult(x, subTotal))
                .Where(x => x.DiscountAmount > 0)
                .OrderByDescending(x => x.DiscountAmount)
                .ThenByDescending(x => x.CreatedAt)
                .FirstOrDefault() ?? CampaignDiscountResult.Empty(subTotal);
        }

        private async Task<List<CampaignDiscountCandidate>> GetActiveCampaignsAsync()
        {
            var now = DateTime.Now;
            var campaigns = new List<CampaignDiscountCandidate>();

            try
            {
                var couponCampaigns = await _context.CampaignCoupons
                    .AsNoTracking()
                    .Where(x => x.IsActive && !x.IsDeleted)
                    .Where(x => x.StartDate == null || x.StartDate <= now)
                    .Where(x => x.EndDate == null || x.EndDate >= now)
                    .Where(x => x.UsageLimit == null || x.UsageLimit <= 0 || x.UsedCount < x.UsageLimit)
                    .Select(x => new CampaignDiscountCandidate
                    {
                        Id = x.Id,
                        Source = "Coupon",
                        Code = x.Code,
                        Name = x.Title,
                        DiscountType = x.DiscountType,
                        DiscountValue = x.DiscountValue,
                        MinimumCartTotal = x.MinimumCartAmount ?? 0,
                        CreatedAt = x.CreatedAt
                    })
                    .ToListAsync();

                campaigns.AddRange(couponCampaigns);
            }
            catch
            {
            }

            try
            {
                var legacyCampaigns = await _context.Campaigns
                    .AsNoTracking()
                    .Where(x => x.IsActive)
                    .Where(x => x.StartDate == null || x.StartDate <= now)
                    .Where(x => x.EndDate == null || x.EndDate >= now)
                    .Select(x => new CampaignDiscountCandidate
                    {
                        Id = x.Id,
                        Source = "Campaign",
                        Code = x.Code,
                        Name = x.Name,
                        DiscountType = x.DiscountType,
                        DiscountValue = x.DiscountValue,
                        MinimumCartTotal = x.MinimumCartTotal,
                        CreatedAt = x.CreatedAt
                    })
                    .ToListAsync();

                campaigns.AddRange(legacyCampaigns);
            }
            catch
            {
            }

            return campaigns
                .Where(x => x.DiscountValue > 0)
                .Where(x => !string.IsNullOrWhiteSpace(x.Code) || !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Code)
                    ? $"{x.Source}:{x.Id}"
                    : x.Code.Trim().ToUpperInvariant())
                .Select(g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First())
                .ToList();
        }

        private static CampaignDiscountResult CreateResult(CampaignDiscountCandidate campaign, decimal subTotal)
        {
            var discountAmount = CalculateDiscountAmount(subTotal, campaign.DiscountType, campaign.DiscountValue);

            return new CampaignDiscountResult
            {
                CampaignId = campaign.Id,
                Code = campaign.Code,
                CampaignName = campaign.Name,
                DiscountType = NormalizeDiscountType(campaign.DiscountType),
                DiscountValue = campaign.DiscountValue,
                DiscountAmount = discountAmount,
                DiscountText = CreateDiscountText(campaign.DiscountType, campaign.DiscountValue),
                SubTotal = subTotal,
                CreatedAt = campaign.CreatedAt
            };
        }

        private static decimal CalculateDiscountAmount(decimal subTotal, string? discountType, decimal discountValue)
        {
            if (subTotal <= 0 || discountValue <= 0)
                return 0;

            var discount = IsFixedDiscount(discountType)
                ? discountValue
                : subTotal * discountValue / 100m;

            if (discount < 0)
                discount = 0;

            if (discount > subTotal)
                discount = subTotal;

            return NormalizeAmount(discount);
        }

        private static string CreateDiscountText(string? discountType, decimal discountValue)
        {
            return IsFixedDiscount(discountType)
                ? $"{NormalizeAmount(discountValue):N2} TL indirim"
                : $"%{NormalizeAmount(discountValue):N0} indirim";
        }

        private static bool IsFixedDiscount(string? discountType)
        {
            return string.Equals(discountType, "Fixed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(discountType, "FixedAmount", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(discountType, "Amount", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDiscountType(string? discountType)
        {
            return IsFixedDiscount(discountType) ? "Fixed" : "Percent";
        }

        private static decimal NormalizeAmount(decimal amount)
        {
            return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        }
    }

    public sealed class CampaignDiscountResult
    {
        public int? CampaignId { get; init; }
        public string Code { get; init; } = string.Empty;
        public string CampaignName { get; init; } = string.Empty;
        public string DiscountType { get; init; } = "Percent";
        public decimal DiscountValue { get; init; }
        public decimal DiscountAmount { get; init; }
        public string DiscountText { get; init; } = string.Empty;
        public decimal SubTotal { get; init; }
        public DateTime CreatedAt { get; init; }
        public decimal GrandTotal => SubTotal - DiscountAmount < 0 ? 0 : SubTotal - DiscountAmount;
        public bool IsApplied => DiscountAmount > 0;

        public static CampaignDiscountResult Empty(decimal subTotal)
        {
            return new CampaignDiscountResult
            {
                SubTotal = Math.Round(subTotal < 0 ? 0 : subTotal, 2, MidpointRounding.AwayFromZero)
            };
        }
    }

    internal sealed class CampaignDiscountCandidate
    {
        public int Id { get; init; }
        public string Source { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string DiscountType { get; init; } = "Percent";
        public decimal DiscountValue { get; init; }
        public decimal MinimumCartTotal { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
