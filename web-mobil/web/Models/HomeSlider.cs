using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class HomeSlider
    {
        public int Id { get; set; }

        public int? ProductId { get; set; }
        public Product? Product { get; set; }

        [Required, MaxLength(180)]
        public string Title { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Subtitle { get; set; }

        [MaxLength(80)]
        public string? ButtonText { get; set; }

        [MaxLength(300)]
        public string? LinkUrl { get; set; }

        [Required, MaxLength(500)]
        public string ImagePath { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? UpdatedAt { get; set; }
    }
}
