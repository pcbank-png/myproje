using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class Product
    {
        public int Id { get; set; }

        [Required, MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required, MaxLength(220)]
        public string Slug { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? ProductCode { get; set; }

        [MaxLength(40)]
        public string? Category { get; set; }

        public string? Description { get; set; }

        [MaxLength(200)]
        public string? MetaTitle { get; set; }

        [MaxLength(300)]
        public string? MetaDescription { get; set; }

        public decimal YearlyPrice { get; set; }

        public decimal LifetimePrice { get; set; }

        public int StockQuantity { get; set; } = 999;

        public bool IsActive { get; set; } = true;

        public bool IsDeleted { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public bool HasStock => StockQuantity > 0;

        public ICollection<ProductImage> Images { get; set; } = new List<ProductImage>();
        public ICollection<ProductFile> Files { get; set; } = new List<ProductFile>();
        public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
        public ICollection<License> Licenses { get; set; } = new List<License>();
    }
}
