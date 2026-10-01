using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed record ProductSeoProfile(
        string ProductCode,
        string Name,
        string MetaTitle,
        string MetaDescription,
        string[] Keywords);

    public static class ProductSeoCatalog
    {
        public static readonly IReadOnlyList<ProductSeoProfile> Profiles = new[]
        {
            new ProductSeoProfile(
                "NSXDATARECOVERYPRO",
                "NSX Veri Kurtarma Pro",
                "NSX Veri Kurtarma Pro | HDD, SSD ve USB Dosya Kurtarma",
                "NSX Veri Kurtarma Pro ile HDD, SSD, USB ve hafıza kartlarında kayıp dosyaları arayın; hızlı ve derin tarama, önizleme ve filtrelemeyle sonuçları inceleyin.",
                new[] { "veri kurtarma programı", "silinen dosyaları kurtarma", "HDD veri kurtarma", "SSD veri kurtarma", "USB dosya kurtarma", "hafıza kartı fotoğraf kurtarma" }),
            new ProductSeoProfile(
                "NSXOKULPLANPRO",
                "NSX Okul Plan Pro",
                "NSX Okul Plan Pro | Ders Programı ve Nöbet Planlama",
                "NSX Okul Plan Pro ile öğretmen, sınıf, derslik, haftalık ders programı ve nöbet çizelgesini tek merkezden düzenleyin, raporlayın ve PDF olarak paylaşın.",
                new[] { "okul ders programı", "ders programı hazırlama programı", "öğretmen ders programı", "nöbet planlama programı", "öğretmen nöbet çizelgesi", "okul yönetim yazılımı" }),
            new ProductSeoProfile(
                "NSXKASADEFTERIPRO",
                "NSX Kasa Defteri Pro",
                "NSX Kasa Defteri Pro | Kasa, Döviz ve Cari Takip",
                "NSX Kasa Defteri Pro ile gelir, gider, tahsilat, ödeme, çoklu kasa, döviz kasası ve cari hesap hareketlerini tek merkezden güvenle yönetin.",
                new[] { "kasa defteri programı", "gelir gider takip programı", "çoklu kasa programı", "döviz kasa takibi", "cari hesap programı", "tahsilat takip programı" }),
            new ProductSeoProfile(
                "NSXSIGORTAACENTAPRO",
                "NSX Sigorta Acente Pro",
                "NSX Sigorta Acente Pro | Poliçe ve Yenileme Takibi",
                "NSX Sigorta Acente Pro ile müşteri, poliçe, teklif, tahsilat, komisyon ve yenileme süreçlerini tek merkezden yönetin; yaklaşan poliçeleri zamanında takip edin.",
                new[] { "sigorta acente programı", "poliçe takip programı", "poliçe yenileme programı", "sigorta müşteri takip programı", "acente komisyon takip programı" }),
            new ProductSeoProfile(
                "NSXSECURITYAUDITORPRO",
                "NSX Security Auditor Pro",
                "NSX Security Auditor Pro | AI Kod Güvenlik Analizi",
                "NSX Security Auditor Pro ile kaynak kod, API, bağımlılık ve yayın çıktılarındaki güvenlik risklerini, gizli anahtarları ve riskli kod akışlarını analiz edin.",
                new[] { "kod güvenlik analizi", "kaynak kod güvenlik taraması", "AI kod analizi", "API güvenlik analizi", "gizli anahtar tespiti", "bağımlılık zafiyet taraması" }),
            new ProductSeoProfile(
                "NSXBARKODLUCARISTOKTAKIP",
                "NSX Barkodlu Satış, Cari ve Stok Takibi",
                "NSX Barkodlu Satış Pro | Cari, Stok ve Hızlı Satış",
                "NSX Barkodlu Satış Pro ile barkodlu satış, ürün, stok, kasa, cari hesap, veresiye, tahsilat ve tedarikçi işlemlerini tek programdan yönetin.",
                new[] { "barkodlu satış programı", "stok takip programı", "hızlı satış programı", "cari takip programı", "market satış programı", "perakende satış programı" }),
            new ProductSeoProfile(
                "NSXTEKNIKSERVISPRO",
                "NSX Teknik Servis Pro",
                "NSX Teknik Servis Pro | Cihaz ve Servis Takip Programı",
                "NSX Teknik Servis Pro ile müşteri, cihaz kabul, arıza, servis kaydı, işlem geçmişi, ödeme ve teslimat süreçlerini tek merkezden düzenli yönetin.",
                new[] { "teknik servis takip programı", "servis takip programı", "cihaz takip programı", "müşteri servis programı", "iş emri takip programı" }),
            new ProductSeoProfile(
                "NSXCARITAKIPPRO",
                "NSX Cari Takip Pro",
                "NSX Cari Takip Pro | Borç, Tahsilat ve Cari Hesap",
                "NSX Cari Takip Pro ile firma ve müşteri carilerini, borç, alacak, tahsilat, ödeme, hatırlatma ve raporlama süreçlerini tek merkezden yönetin.",
                new[] { "cari takip programı", "cari hesap programı", "borç alacak takip programı", "müşteri bakiye takip programı", "tahsilat takip programı" }),
            new ProductSeoProfile(
                "NSXCARITAKIPPROBULUT",
                "NSX Cari Takip Pro Bulut",
                "NSX Cari Takip Pro Bulut | Çoklu Bilgisayar ve Mobil Cari Takip",
                "NSX Cari Takip Pro Bulut ile firma ve müşteri carilerini, borç, alacak, tahsilat, ödeme ve raporları bilgisayarlar arasında canlı eşitleyin ve mobil cihazlardan yönetin.",
                new[] { "bulut cari takip programı", "çoklu bilgisayar cari takip", "mobil cari takip", "cari hesap programı", "tahsilat takip programı" }),
            new ProductSeoProfile(
                "NSXDUGUNSALONUPRO",
                "NSX Düğün Salonu Pro",
                "NSX Düğün Salonu Pro | Rezervasyon ve Tahsilat Takibi",
                "NSX Düğün Salonu Pro ile rezervasyon, etkinlik, müşteri, salon, sözleşme, ödeme ve tahsilat süreçlerini tek merkezden planlayıp takip edin.",
                new[] { "düğün salonu programı", "organizasyon takip programı", "salon rezervasyon programı", "etkinlik takip programı", "müşteri tahsilat takip programı" }),
            new ProductSeoProfile(
                "NSXKOMISYONLUCARITAKIPPRO",
                "NSX Komisyonlu Cari Takip Pro",
                "NSX Komisyonlu Cari Takip Pro | Komisyon ve Tahsilat",
                "NSX Komisyonlu Cari Takip Pro ile cari hesap, borç, tahsilat, komisyon ve raporlama süreçlerini Server Client yapısıyla merkezi olarak yönetin.",
                new[] { "komisyonlu cari takip programı", "komisyon takip programı", "server client cari takip programı", "tahsilat takip programı", "cari hesap programı" }),
            new ProductSeoProfile(
                "NSXOTOTAMIRSERVISPRO",
                "NSX Oto Tamir Servis Pro",
                "NSX Oto Tamir Servis Pro | Araç Kabul ve Servis Takibi",
                "NSX Oto Tamir Servis Pro ile araç kabul, müşteri, iş emri, yedek parça, bakım, onarım, ödeme ve teslimat süreçlerini tek programdan yönetin.",
                new[] { "oto tamir programı", "oto servis programı", "araç kabul programı", "araç servis takip programı", "oto tamir müşteri takip programı" }),
            new ProductSeoProfile(
                "NSXOTOGALERIPRO",
                "NSX Oto Galeri Pro",
                "NSX Oto Galeri Pro | Araç Stok ve Satış Takip Programı",
                "NSX Oto Galeri Pro ile araç stoklarını, müşteri görüşmelerini, ilan, alış, satış, ödeme ve tahsilat süreçlerini tek merkezden düzenli takip edin.",
                new[] { "oto galeri programı", "araç stok takip programı", "galeri müşteri takip programı", "araç satış takip programı" }),
            new ProductSeoProfile(
                "NSXSERVISPROLIVE",
                "NSX Servis Pro Live",
                "NSX Servis Pro Live | QR Kodlu Canlı Servis Takibi",
                "NSX Servis Pro Live ile QR kodlu canlı servis durumunu, cihaz kabulünü, müşteri bilgilendirmesini, iş emrini ve teslimat akışını tek sistemden yönetin.",
                new[] { "servis live programı", "QR kodlu servis takibi", "canlı servis takip programı", "teknik servis takip programı", "müşteri servis sorgulama" }),
            new ProductSeoProfile(
                "NSXKLINIKVERANDEVUTAKIPPRO",
                "NSX Klinik ve Randevu Takip Programı",
                "NSX Klinik ve Randevu Takip | Hasta ve Doktor Programı",
                "NSX Klinik ve Randevu Takip Programı ile hasta, doktor, randevu, muayene, ödeme ve klinik iş akışını tek merkezden düzenli biçimde yönetin.",
                new[] { "klinik programı", "hasta takip programı", "doktor takip programı", "randevu programı", "klinik randevu programı" }),
            new ProductSeoProfile(
                "NSXVERESIYETAKIPPROFREE",
                "NSX Veresiye Takip Pro Free",
                "NSX Veresiye Takip Pro Free | Ücretsiz Cari Takip",
                "NSX Veresiye Takip Pro Free ile cari hesap, borç, tahsilat ve açık bakiye takibini Windows, Bulut ve Mobil üzerinden ömür boyu ücretsiz yönetin.",
                new[] { "ücretsiz veresiye programı", "ücretsiz cari takip programı", "bulut veresiye programı", "mobil veresiye programı", "veresiye takip programı", "borç alacak takip programı", "cari takip programı" }),
            new ProductSeoProfile(
                "NSXTURBOBILGISAYARPERFORMANSPRO",
                "NSX Turbo Performans Programı",
                "NSX Turbo | Bilgisayar Hızlandırma ve FPS Artırma",
                "NSX Turbo ile Windows bakımını ve oyun performansını iyileştirin; gereksiz yükleri azaltın, gecikmeyi düşürün ve daha akıcı bir bilgisayar deneyimi elde edin.",
                new[] { "bilgisayar hızlandırma programı", "PC performans programı", "Windows hızlandırma programı", "oyun hızlandırma programı", "FPS artırma programı" })
        };

        public static ProductSeoProfile? Find(Product? product)
        {
            if (product == null)
                return null;

            var normalizedCode = Normalize(product.ProductCode);
            var byCode = Profiles.FirstOrDefault(x => Normalize(x.ProductCode) == normalizedCode);
            if (byCode != null)
                return byCode;

            var normalizedName = Normalize(product.Name);
            return Profiles.FirstOrDefault(x => Normalize(x.Name) == normalizedName);
        }

        private static string Normalize(string? value)
        {
            return string.Concat((value ?? string.Empty)
                .Where(char.IsLetterOrDigit))
                .ToUpperInvariant();
        }
    }
}
