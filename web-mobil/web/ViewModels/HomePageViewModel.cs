using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public class HomePageViewModel
    {
        public List<Product> FeaturedProducts { get; set; } = new();
        public List<Campaign> ActiveCampaigns { get; set; } = new();
        public int ActiveProductCount { get; set; }
    }
}
