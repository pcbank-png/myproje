using System;
using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class Campaign
    {
        public int Id { get; set; }

        [StringLength(50)]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "Kampanya adı zorunludur.")]
        [StringLength(120)]
        public string Name { get; set; } = string.Empty;

        [StringLength(300)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        public decimal MinimumCartTotal { get; set; }

        // Değerler: Percent veya Fixed
        public string DiscountType { get; set; } = "Percent";

        public decimal DiscountValue { get; set; }

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
