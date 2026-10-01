using System.ComponentModel.DataAnnotations;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public class ProductVideoFormViewModel
    {
        public int Id { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Lütfen videonun bağlı olduğu programı seçin.")]
        [Display(Name = "Bağlı Program")]
        public int ProductId { get; set; }

        [Required(ErrorMessage = "YouTube video bağlantısı zorunludur.")]
        [MaxLength(700)]
        [Display(Name = "YouTube Video Bağlantısı")]
        public string YouTubeUrl { get; set; } = string.Empty;

        [MaxLength(220)]
        [Display(Name = "Video Başlığı (İsteğe Bağlı)")]
        public string? Title { get; set; }

        [MaxLength(80)]
        [Display(Name = "Video Türü")]
        public string? VideoType { get; set; } = "Tanıtım";

        [Display(Name = "Sıra")]
        public int SortOrder { get; set; }

        [Display(Name = "Öne Çıkan")]
        public bool IsFeatured { get; set; }

        [Display(Name = "Yayında")]
        public bool IsActive { get; set; } = true;

        public IReadOnlyList<Product> Products { get; set; } = Array.Empty<Product>();
    }

    public class ProductVideoCardViewModel
    {
        public int Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductDescription { get; set; } = string.Empty;
        public string ProductUrl { get; set; } = string.Empty;
        public string DisplayTitle { get; set; } = string.Empty;
        public string VideoType { get; set; } = string.Empty;
        public string YouTubeVideoId { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public bool IsFeatured { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ProductVideosIndexViewModel
    {
        public IReadOnlyList<ProductVideoCardViewModel> Videos { get; set; } = Array.Empty<ProductVideoCardViewModel>();
    }

    public class ProductVideoDetailViewModel
    {
        public ProductVideo Video { get; set; } = new();
        public Product Product { get; set; } = new();
        public string DisplayTitle { get; set; } = string.Empty;
        public string ProductDescription { get; set; } = string.Empty;
        public string ProductUrl { get; set; } = string.Empty;
        public string EmbedUrl { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public bool IsFreeProduct { get; set; }
        public bool HasStock { get; set; }
        public IReadOnlyList<ProductVideoCardViewModel> RelatedVideos { get; set; } = Array.Empty<ProductVideoCardViewModel>();
    }
}
