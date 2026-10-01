using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace NSYazilim.Web.ViewModels
{
    public class AdvertisementFormViewModel
    {
        public int Id { get; set; }

        [Display(Name = "Program")]
        public int? ProductId { get; set; }

        [Required(ErrorMessage = "Ürün kodu zorunludur.")]
        [MaxLength(100)]
        [Display(Name = "Program Kodu")]
        public string ProductCode { get; set; } = "NSXVERESIYEDEFTERI";

        [Required(ErrorMessage = "Reklam alanı zorunludur.")]
        [MaxLength(80)]
        [Display(Name = "Reklam Alanı")]
        public string SlotCode { get; set; } = "FREE_BOTTOM_728X90";

        [Required(ErrorMessage = "Reklam başlığı zorunludur.")]
        [MaxLength(180)]
        [Display(Name = "Başlık")]
        public string Title { get; set; } = string.Empty;

        [MaxLength(500)]
        [Display(Name = "Açıklama")]
        public string? Description { get; set; }

        [MaxLength(300)]
        [Display(Name = "Alternatif Metin")]
        public string? AltText { get; set; }

        [MaxLength(700)]
        public string? ExistingImagePath { get; set; }

        [Display(Name = "Banner Görseli")]
        public IFormFile? ImageFile { get; set; }

        [MaxLength(700)]
        [Display(Name = "Tıklama URL")]
        public string? TargetUrl { get; set; }

        [Range(1, 4000)]
        public int Width { get; set; } = 728;

        [Range(1, 2000)]
        public int Height { get; set; } = 90;

        [Range(3, 3600)]
        [Display(Name = "Geçiş Süresi")]
        public int DisplaySeconds { get; set; } = 10;

        [Range(0, 999999)]
        [Display(Name = "Sıra")]
        public int SortOrder { get; set; } = 0;

        [Range(0, 999999)]
        [Display(Name = "Öncelik")]
        public int Priority { get; set; } = 0;

        [Display(Name = "Başlangıç")]
        public DateTime? StartDate { get; set; }

        [Display(Name = "Bitiş")]
        public DateTime? EndDate { get; set; }

        public bool IsActive { get; set; } = true;
        public bool TrackClicks { get; set; } = true;
    }
}
