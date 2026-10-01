using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class ProductVideo
    {
        public int Id { get; set; }

        [Required]
        public int ProductId { get; set; }

        [Required, MaxLength(700)]
        public string YouTubeUrl { get; set; } = string.Empty;

        [Required, MaxLength(32)]
        public string YouTubeVideoId { get; set; } = string.Empty;

        [MaxLength(220)]
        public string? Title { get; set; }

        [MaxLength(80)]
        public string? VideoType { get; set; }

        [Required, MaxLength(240)]
        public string Slug { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public bool IsFeatured { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? UpdatedAt { get; set; }

        public Product? Product { get; set; }
    }
}
