namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Curated static status text used during public purchase/demo/free-download flows.
    /// These messages are safe to machine translate because they never include customer,
    /// payment, order, token or license values. Reviewed English is kept as an immediate
    /// fallback so a newly-added language never exposes Turkish during a conversion flow.
    /// </summary>
    public static class LocalizationConversionFlowSeedData
    {
        public static readonly IReadOnlyList<LocalizationSeedItem> Items = new[]
        {
            new LocalizationSeedItem("Demo indirmek için önce üye girişi yapmalısınız.", "Please sign in before downloading the demo.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("Ücretsiz indirmek için önce üye girişi yapmalısınız.", "Please sign in before downloading the free software.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("Bu ürün şu anda stokta yok. Demo indirme kapalıdır.", "This product is currently out of stock. Demo download is unavailable.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("İndirme dosyası sunucuda bulunamadı.", "The download file could not be found on the server.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("Demo dosyası sunucuda bulunamadı.", "The demo file could not be found on the server.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("Bu ürün ücretsiz indirme kapsamında değil.", "This product is not available as a free download.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("Bu ürün şu anda stokta yok. İndirme kapalıdır.", "This product is currently out of stock. Download is unavailable.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("Dosya sunucuda bulunamadı.", "The file could not be found on the server.", "/Public/Conversion/Download"),
            new LocalizationSeedItem("Bu ürün şu anda stokta yok. Satın alma bildirimi kapalıdır.", "This product is currently out of stock. Purchase notification is unavailable.", "/Public/Conversion/Purchase"),
            new LocalizationSeedItem("Havale bildirimi kaydedilirken bir sorun oluştu. Lütfen tekrar deneyin.", "There was a problem saving the bank transfer notification. Please try again.", "/Public/Conversion/Purchase"),
            new LocalizationSeedItem("Çıkış yapıldı.", "Signed out.", "/Public/Conversion/Account")
        };

        public static readonly IReadOnlyDictionary<string, string> EnglishFallbackByKey = Items
            .GroupBy(x => LocalizationTextKey.Create(x.Source), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last().English, StringComparer.Ordinal);
    }
}
