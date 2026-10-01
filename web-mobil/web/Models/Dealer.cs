using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class Dealer
    {
        public int Id { get; set; }

        [Required, MaxLength(40)]
        public string DealerCode { get; set; } = string.Empty;

        [Required, MaxLength(180)]
        public string BusinessName { get; set; } = string.Empty;

        [Required, MaxLength(150)]
        public string ContactName { get; set; } = string.Empty;

        [Required, MaxLength(180)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(30)]
        public string? Phone { get; set; }

        [Required, MaxLength(80)]
        public string Province { get; set; } = string.Empty;

        [MaxLength(120)]
        public string? District { get; set; }

        [MaxLength(500)]
        public string? Address { get; set; }

        [Required]
        public string PasswordHash { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string CommissionMode { get; set; } = "Percent";

        public decimal CommissionPercent { get; set; }

        public decimal CommissionFixedAmount { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? UpdatedAt { get; set; }

        public ICollection<DealerSale> Sales { get; set; } = new List<DealerSale>();
    }
}
