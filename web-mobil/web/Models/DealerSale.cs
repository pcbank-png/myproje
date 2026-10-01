using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class DealerSale
    {
        public int Id { get; set; }

        public int DealerId { get; set; }

        public int CustomerUserId { get; set; }

        public int ProductId { get; set; }

        public int OrderId { get; set; }

        public int? BankTransferNotificationId { get; set; }

        [Required, MaxLength(30)]
        public string LicenseType { get; set; } = "Yearly";

        public int Quantity { get; set; } = 1;

        public decimal SaleAmount { get; set; }

        [Required, MaxLength(20)]
        public string CommissionModeSnapshot { get; set; } = "Percent";

        public decimal CommissionPercentSnapshot { get; set; }

        public decimal CommissionFixedAmountSnapshot { get; set; }

        public decimal CommissionAmount { get; set; }

        [Required, MaxLength(40)]
        public string Status { get; set; } = "WaitingBankTransfer";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? BankTransferSubmittedAt { get; set; }

        public Dealer? Dealer { get; set; }

        public User? CustomerUser { get; set; }

        public Product? Product { get; set; }

        public Order? Order { get; set; }

        public BankTransferNotification? BankTransferNotification { get; set; }
    }
}
