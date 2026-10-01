using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class BankTransferNotification
    {
        public int Id { get; set; }

        public int ProductId { get; set; }
        public Product? Product { get; set; }

        public int UserId { get; set; }
        public User? User { get; set; }

        public int? OrderId { get; set; }
        public Order? Order { get; set; }

        [Required, MaxLength(150)]
        public string FullName { get; set; } = string.Empty;

        [Required, MaxLength(180)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? Phone { get; set; }

        [Required, MaxLength(30)]
        public string LicenseType { get; set; } = "Yearly";

        public int Quantity { get; set; } = 1;

        public decimal Amount { get; set; }

        [Required, MaxLength(150)]
        public string SenderName { get; set; } = string.Empty;

        [MaxLength(120)]
        public string? SenderBank { get; set; }

        [MaxLength(120)]
        public string? ReceiptNumber { get; set; }

        public DateTime TransferDate { get; set; } = DateTime.Now;

        [MaxLength(1000)]
        public string? Note { get; set; }

        [Required, MaxLength(30)]
        public string Status { get; set; } = "Pending";

        [MaxLength(1000)]
        public string? AdminNote { get; set; }

        [MaxLength(80)]
        public string? IpAddress { get; set; }

        [MaxLength(500)]
        public string? UserAgent { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? ReviewedAt { get; set; }
    }
}
