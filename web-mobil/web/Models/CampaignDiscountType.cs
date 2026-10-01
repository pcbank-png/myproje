namespace NSYazilim.Web.Models
{
    // Eski paketlerden kalmış enum referansları hata vermesin diye tutuldu.
    // Yeni Campaign modeli DiscountType değerini string olarak kullanır: "Percent" veya "Fixed".
    public enum CampaignDiscountType
    {
        Percent = 0,
        Percentage = 0,
        Fixed = 1,
        FixedAmount = 1
    }
}
