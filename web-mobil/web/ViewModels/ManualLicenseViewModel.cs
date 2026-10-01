using System;
using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.ViewModels
{
    public class ManualLicenseViewModel
    {
        [Required(ErrorMessage = "Ürün seçimi zorunludur.")]
        [Range(1, int.MaxValue, ErrorMessage = "Ürün seçimi zorunludur.")]
        public int ProductId { get; set; }

        [Required(ErrorMessage = "Üye seçimi zorunludur.")]
        [Range(1, int.MaxValue, ErrorMessage = "Üye seçimi zorunludur.")]
        public int UserId { get; set; }

        [Required]
        public string LicenseType { get; set; } = "Yearly";

        public DateTime StartDate { get; set; } = DateTime.Today;

        public bool IsActive { get; set; } = true;
    }
}
