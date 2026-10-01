using System;
using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.ViewModels
{
    public class CampaignFormViewModel
    {
        public int Id { get; set; }

        [StringLength(50)]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "Kampanya adı zorunludur.")]
        [StringLength(120)]
        public string Name { get; set; } = string.Empty;

        [StringLength(300)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        [Range(0, 9999999, ErrorMessage = "Minimum tutar 0 veya daha büyük olmalıdır.")]
        public decimal MinimumCartTotal { get; set; }

        [Range(0.01, 9999999, ErrorMessage = "İndirim değeri 0'dan büyük olmalıdır.")]
        public decimal DiscountValue { get; set; }

        // Percent veya Fixed
        public string DiscountType { get; set; } = "Percent";

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }
    }
}
