using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class ShopierOrderRecord
    {
        public int Id { get; set; }

        [Required, MaxLength(80)]
        public string ShopierOrderId { get; set; } = string.Empty;

        [MaxLength(40)]
        public string PaymentStatus { get; set; } = "pending";

        [MaxLength(40)]
        public string FulfillmentStatus { get; set; } = "unfulfilled";

        [MaxLength(40)]
        public string PaymentMethod { get; set; } = string.Empty;

        public bool IsInstallments { get; set; }

        [MaxLength(8)]
        public string Currency { get; set; } = "TRY";

        public decimal TotalAmount { get; set; }

        [MaxLength(120)]
        public string? CustomerFirstName { get; set; }

        [MaxLength(120)]
        public string? CustomerLastName { get; set; }

        [MaxLength(180)]
        public string? CustomerEmail { get; set; }

        [MaxLength(60)]
        public string? CustomerPhone { get; set; }

        [MaxLength(1000)]
        public string? Note { get; set; }

        [MaxLength(80)]
        public string? ShopierProductId { get; set; }

        [MaxLength(300)]
        public string? ProductTitle { get; set; }

        [MaxLength(120)]
        public string? LicenseSelection { get; set; }

        public int Quantity { get; set; } = 1;

        [MaxLength(80)]
        public string? LastWebhookId { get; set; }

        public DateTime ShopierCreatedAt { get; set; } = DateTime.Now;

        public DateTime FirstSeenAt { get; set; } = DateTime.Now;

        public DateTime LastSyncedAt { get; set; } = DateTime.Now;

        public int? LocalOrderId { get; set; }

        [MaxLength(40)]
        public string LocalFulfillmentStatus { get; set; } = "Pending";

        [MaxLength(1000)]
        public string? LocalFulfillmentMessage { get; set; }

        public DateTime? LastFulfillmentAttemptAt { get; set; }

        public DateTime? FulfilledAt { get; set; }

        public DateTime? DeliveryEmailQueuedAt { get; set; }

        public bool IsAdminHidden { get; set; }
    }
}
