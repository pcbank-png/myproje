using Microsoft.AspNetCore.Http;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public class AdminSliderFormViewModel
    {
        public int Id { get; set; }
        public int? ProductId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string? ButtonText { get; set; } = "Ürünü İncele";
        public string? LinkUrl { get; set; }
        public IFormFile? Image { get; set; }
        public string? ExistingImagePath { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public List<Product> Products { get; set; } = new();
    }
}
