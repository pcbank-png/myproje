using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class Advertisement
    {
        public int Id { get; set; }

        public int? ProductId { get; set; }
        public Product? Product { get; set; }

        [Required, MaxLength(100)]
        public string ProductCode { get; set; } = "NSXVERESIYETAKIPPROFREE";

        [Required, MaxLength(80)]
        public string SlotCode { get; set; } = "FREE_BOTTOM_728X90";

        [Required, MaxLength(180)]
        public string Title { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Description { get; set; }

        [MaxLength(300)]
        public string? AltText { get; set; }

        [Required, MaxLength(700)]
        public string ImagePath { get; set; } = string.Empty;

        [MaxLength(700)]
        public string? TargetUrl { get; set; }

        public int Width { get; set; } = 728;
        public int Height { get; set; } = 90;
        public int DisplaySeconds { get; set; } = 10;
        public int SortOrder { get; set; } = 0;
        public int Priority { get; set; } = 0;

        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public bool IsActive { get; set; } = true;
        public bool IsDeleted { get; set; } = false;
        public bool TrackClicks { get; set; } = true;

        public int ImpressionCount { get; set; } = 0;
        public int ClickCount { get; set; } = 0;
        public DateTime? LastShownAt { get; set; }
        public DateTime? LastClickedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }
    }
}
