namespace NSYazilim.Web.ViewModels
{
    public sealed class ActiveCampaignBannerViewModel
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string DiscountText { get; init; } = string.Empty;
        public decimal MinimumAmount { get; init; }
        public DateTime? EndDate { get; init; }
    }
}
