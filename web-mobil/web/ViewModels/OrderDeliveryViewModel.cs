using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public class OrderDeliveryViewModel
    {
        public Order Order { get; set; } = new();
        public List<License> Licenses { get; set; } = new();
        public bool IsSuccessPage { get; set; }
    }
}
