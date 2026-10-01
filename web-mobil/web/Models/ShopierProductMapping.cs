using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class ShopierProductMapping
    {
        public int Id { get; set; }

        public int ProductId { get; set; }
        public Product? Product { get; set; }

        [Required, MaxLength(80)]
        public string ShopierProductId { get; set; } = string.Empty;

        [Required, MaxLength(300)]
        public string ShopierTitle { get; set; } = string.Empty;

        [Required, MaxLength(500)]
        public string ShopierUrl { get; set; } = string.Empty;

        [MaxLength(40)]
        public string MatchMethod { get; set; } = "auto";

        public int MatchScore { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastSyncedAt { get; set; } = DateTime.Now;
    }
}
