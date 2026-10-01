using System.ComponentModel.DataAnnotations;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public class AdminDealerFormViewModel
    {
        public int? Id { get; set; }

        [Required, MaxLength(180)]
        public string BusinessName { get; set; } = string.Empty;

        [Required, MaxLength(150)]
        public string ContactName { get; set; } = string.Empty;

        [Required, EmailAddress, MaxLength(180)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(30)]
        public string? Phone { get; set; }

        [Required, MaxLength(80)]
        public string Province { get; set; } = string.Empty;

        [MaxLength(120)]
        public string? District { get; set; }

        [MaxLength(500)]
        public string? Address { get; set; }

        [MinLength(6)]
        public string? Password { get; set; }

        [Required, MaxLength(20)]
        public string CommissionMode { get; set; } = "Percent";

        [Range(0, 100)]
        public decimal CommissionPercent { get; set; }

        [Range(0, 999999999)]
        public decimal CommissionFixedAmount { get; set; }

        public bool IsActive { get; set; } = true;

        public IReadOnlyList<string> Provinces { get; set; } = Array.Empty<string>();
    }

    public class DealerLoginViewModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; } = true;
    }

    public class DealerNewSaleViewModel
    {
        [Range(1, int.MaxValue)]
        public int CustomerUserId { get; set; }

        [Range(1, int.MaxValue)]
        public int ProductId { get; set; }

        [Required]
        public string LicenseType { get; set; } = "Yearly";

        [Range(1, 10)]
        public int Quantity { get; set; } = 1;

        public List<User> Customers { get; set; } = new();
        public List<Product> Products { get; set; } = new();
    }

    public class DealerBankTransferViewModel
    {
        public int SaleId { get; set; }
        public DealerSale? Sale { get; set; }

        [Required, MaxLength(150)]
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
    }

    public class DealerDashboardViewModel
    {
        public Dealer Dealer { get; set; } = new();
        public List<DealerSale> Sales { get; set; } = new();
        public int TotalSales { get; set; }
        public int PendingSales { get; set; }
        public int ApprovedSales { get; set; }
        public decimal ApprovedCommission { get; set; }
    }
}
