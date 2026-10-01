using Microsoft.AspNetCore.Http;

namespace NSYazilim.Web.ViewModels
{
    public class ProductFormViewModel
    {
        public string Name { get; set; } = string.Empty;

        public string? ProductCode { get; set; }

        public string? Category { get; set; }

        public string? Description { get; set; }

        public string? MetaTitle { get; set; }

        public string? MetaDescription { get; set; }

        public decimal YearlyPrice { get; set; }

        public decimal LifetimePrice { get; set; }

        public int StockQuantity { get; set; } = 999;

        public bool IsActive { get; set; }
        public List<IFormFile>? Images { get; set; }


        public IFormFile? DemoFile { get; set; }
    }
}
