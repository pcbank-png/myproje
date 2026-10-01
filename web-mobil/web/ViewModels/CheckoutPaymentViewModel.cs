using NSYazilim.Web.Models;

namespace NSYazilim.Web.ViewModels
{
    public sealed class CheckoutPaymentViewModel
    {
        public required Product Product { get; init; }
        public string LicenseType { get; init; } = "Yearly";
        public string LicenseLabel { get; init; } = "Yıllık Lisans";
        public decimal Amount { get; init; }
        public string ShopierUrl { get; init; } = string.Empty;
        public bool ShopierAvailable => !string.IsNullOrWhiteSpace(ShopierUrl);
        public string ProductUrl { get; init; } = "/Products";
        public string BankTransferUrl { get; init; } = string.Empty;
        public string CustomerEmail { get; init; } = string.Empty;
    }
}
