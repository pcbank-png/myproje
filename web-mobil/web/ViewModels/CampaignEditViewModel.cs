using System;
using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.ViewModels
{
    public class CampaignEditViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Kampanya adı zorunludur.")]
        [Display(Name = "Kampanya Adı")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Açıklama")]
        public string? Description { get; set; }

        [Required]
        [Display(Name = "İndirim Tipi")]
        public string DiscountType { get; set; } = "Percent";

        [Required]
        [Range(0.01, 999999)]
        [Display(Name = "İndirim Değeri")]
        public decimal DiscountValue { get; set; }

        [Range(0, 999999999)]
        [Display(Name = "Minimum Tutar")]
        public decimal MinimumCartTotal { get; set; }

        [Display(Name = "Aktif")]
        public bool IsActive { get; set; } = true;

        [Display(Name = "Başlangıç Tarihi")]
        public DateTime? StartDate { get; set; }

        [Display(Name = "Bitiş Tarihi")]
        public DateTime? EndDate { get; set; }
    }
}
