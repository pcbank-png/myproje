using System;
using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.ViewModels
{
    public class AdminCampaignFormViewModel
    {
        public int Id { get; set; }

        [StringLength(50)]
        [Display(Name = "Kampanya Kodu")]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "Kampanya adı zorunludur.")]
        [Display(Name = "Kampanya Adı")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Açıklama")]
        public string? Description { get; set; }

        [Display(Name = "Aktif")]
        public bool IsActive { get; set; } = true;

        [Display(Name = "Minimum Tutar")]
        [Range(0, 9999999, ErrorMessage = "Minimum tutar 0 veya daha büyük olmalıdır.")]
        public decimal MinimumCartTotal { get; set; }

        [Display(Name = "İndirim Tipi")]
        public string DiscountType { get; set; } = "Percent";

        [Display(Name = "İndirim Değeri")]
        [Range(0.01, 9999999, ErrorMessage = "İndirim değeri 0'dan büyük olmalıdır.")]
        public decimal DiscountValue { get; set; }

        [Display(Name = "Başlangıç Tarihi")]
        public DateTime? StartDate { get; set; }

        [Display(Name = "Bitiş Tarihi")]
        public DateTime? EndDate { get; set; }
    }
}
