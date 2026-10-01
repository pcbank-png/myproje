using System.ComponentModel.DataAnnotations;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public class CampaignCouponFormViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Kupon kodu zorunludur.")]
        [MaxLength(50)]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "Başlık zorunludur.")]
        [MaxLength(150)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string DiscountType { get; set; } = "Percent";

        [Range(1, 999999, ErrorMessage = "İndirim değeri 1 veya daha büyük olmalıdır.")]
        public decimal DiscountValue { get; set; }

        public decimal? MinimumCartAmount { get; set; }
        public int? UsageLimit { get; set; }

        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class CampaignCouponListViewModel
    {
        public List<CampaignCoupon> Coupons { get; set; } = new();
        public CampaignCouponFormViewModel Form { get; set; } = new();
    }
}
