namespace NSYazilim.Web.Models
{
    public class CampaignCoupon
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;

        // Percent veya Fixed
        public string DiscountType { get; set; } = "Percent";

        public decimal DiscountValue { get; set; }

        public decimal? MinimumCartAmount { get; set; }

        public int? UsageLimit { get; set; }
        public int UsedCount { get; set; }

        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public bool IsActive { get; set; } = true;
        public bool IsDeleted { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
