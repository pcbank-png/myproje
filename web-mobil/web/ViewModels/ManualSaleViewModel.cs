using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.ViewModels
{
    public class ManualSaleViewModel
    {
        [Range(1, int.MaxValue, ErrorMessage = "Geçerli bir ürün seçin.")]
        public int ProductId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Geçerli bir üye seçin.")]
        public int UserId { get; set; }

        [Range(0.01, 999999999, ErrorMessage = "Alınan ücret 0'dan büyük olmalıdır.")]
        public decimal Amount { get; set; }

        [MaxLength(30)]
        public string LicenseType { get; set; } = "Lifetime";

        public bool CreateLicense { get; set; } = true;

        [DataType(DataType.Date)]
        public DateTime SaleDate { get; set; } = DateTime.Today;
    }
}
