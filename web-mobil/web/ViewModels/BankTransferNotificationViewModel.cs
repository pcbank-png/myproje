using System.ComponentModel.DataAnnotations;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public class BankTransferNotificationViewModel
    {
        public int ProductId { get; set; }

        public Product? Product { get; set; }

        public string LicenseType { get; set; } = "Yearly";

        public decimal Amount { get; set; }

        public decimal OriginalAmount { get; set; }

        public decimal CampaignDiscountAmount { get; set; }

        public string CampaignName { get; set; } = string.Empty;

        public string CampaignDiscountText { get; set; } = string.Empty;

        public decimal YearlyPrice { get; set; }

        public decimal LifetimePrice { get; set; }

        public decimal YearlyPayableAmount { get; set; }

        public decimal LifetimePayableAmount { get; set; }

        public decimal YearlyCampaignDiscountAmount { get; set; }

        public decimal LifetimeCampaignDiscountAmount { get; set; }

        public string YearlyCampaignName { get; set; } = string.Empty;

        public string LifetimeCampaignName { get; set; } = string.Empty;

        public string YearlyCampaignDiscountText { get; set; } = string.Empty;

        public string LifetimeCampaignDiscountText { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string? Phone { get; set; }

        [Required(ErrorMessage = "Gönderen adını yazın.")]
        [MaxLength(150)]
        public string SenderName { get; set; } = string.Empty;

        [MaxLength(120)]
        public string? SenderBank { get; set; }

        [MaxLength(120)]
        public string? ReceiptNumber { get; set; }

        [DataType(DataType.Date)]
        public DateTime TransferDate { get; set; } = DateTime.Today;

        [MaxLength(1000)]
        public string? Note { get; set; }

        public string BankName { get; set; } = string.Empty;

        public string AccountHolder { get; set; } = string.Empty;

        public string Iban { get; set; } = string.Empty;

        public string BankBranch { get; set; } = string.Empty;

        public string TransferDescription { get; set; } = string.Empty;

        public string YearlyTransferDescription { get; set; } = string.Empty;

        public string LifetimeTransferDescription { get; set; } = string.Empty;

        public bool HasBankInfo =>
            !string.IsNullOrWhiteSpace(BankName) &&
            !string.IsNullOrWhiteSpace(AccountHolder) &&
            !string.IsNullOrWhiteSpace(Iban);

        public bool HasCampaignDiscount => CampaignDiscountAmount > 0;
    }
}
