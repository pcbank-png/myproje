using System.Text;
using System.Text.RegularExpressions;

namespace NSYazilim.Web.Services
{
    public static class SeoTextHelper
    {
        public const string SiteName = "NSX Yazılım";
        public const string Domain = "https://www.nsxyazilim.com";

        public static readonly string[] MainKeywords =
        {
            "ücretsiz veresiye programı",
            "bulut veresiye programı",
            "mobil veresiye programı",
            "telefondan veresiye takibi",
            "bulut cari takip programı",
            "mobil cari hesap",
            "QR ile cari takip",
            "canlı senkronizasyon",
            "ücretsiz veresiye defteri",
            "veresiye defteri",
            "veresiye takip programı",
            "borç alacak takip programı",
            "cari takip programı",
            "komisyonlu cari takip programı",
            "komisyon takip programı",
            "server client cari takip programı",
            "komisyonlu tahsilat takip programı",
            "cari hesap takibi",
            "bakkal veresiye defteri",
            "bakkal veresiye programı",
            "müşteri borç takip programı",
            "müşteri bakiye takip programı",
            "excel veresiye takip",
            "excel yerine veresiye programı",
            "esnaf veresiye programı",
            "ücretsiz teknik servis programı",
            "teknik servis takip programı",
            "servis takip programı",
            "cihaz takip programı",
            "oto tamir programı",
            "oto tamir servis programı",
            "oto servis programı",
            "oto galeri programı",
            "araç takip programı",
            "servis live programı",
            "oto servis live programı",
            "ücretsiz bilgisayar hızlandırma programı",
            "ücretsiz pc hızlandırma programı",
            "bilgisayar hızlandırma programı",
            "pc hızlandırma programı",
            "windows hızlandırma programı",
            "oyun hızlandırma programı",
            "fps artırma programı",
            "klinik programı",
            "hasta takip programı",
            "randevu programı",
            "klinik randevu programı",
            "NSX Teknik Servis Pro",
            "NSX Cari Takip Pro",
            "NSX Düğün Salonu Pro",
            "NSX Komisyonlu Cari Takip Pro",
            "NSX Oto Tamir Servis Pro",
            "NSX Oto Galeri Pro",
            "NSX ServisPro Live",
            "NSX Klinik ve Randevu Takip Programı",
            "NSX Veresiye Takip Programı Ücretsiz",
            "NSX Barkodlu Satış, Cari ve Stok Takip Pro",
            "NSX Kasa Defteri Pro",
            "kasa defteri programı",
            "gelir gider takip programı",
            "çoklu kasa takip programı",
            "döviz kasa takip programı",
            "NSX Sigorta Acente Pro",
            "sigorta acente programı",
            "poliçe takip programı",
            "poliçe yenileme programı",
            "sigorta komisyon takip programı",
            "NSX Security Auditor Pro",
            "kod güvenlik analizi",
            "kaynak kod güvenlik taraması",
            "AI kod analizi",
            "API güvenlik analizi",
            "gizli anahtar tespiti",
            "bağımlılık zafiyet taraması",
            "NSX Okul Plan Pro",
            "okul ders programı",
            "ders programı hazırlama programı",
            "öğretmen ders programı",
            "sınıf ders programı",
            "nöbet planlama programı",
            "öğretmen nöbet çizelgesi",
            "okul yönetim yazılımı",
            "barkodlu satış programı",
            "barkodlu cari takip programı",
            "barkodlu stok takip programı",
            "stok takip programı",
            "hızlı satış programı",
            "market satış programı",
            "perakende satış programı",
            "lisanslı yazılım",
            "NSX Turbo Performans Programı",
            "NSX Turbo",
            "NSX Yazılım"
        };

        public static readonly (string Slug, string Title, string Description, string[] Keywords)[] PrioritySearchPages =
        {
            (
                "veresiye-programi-nedir",
                "Veresiye Programı Nedir? | Bulut ve Mobil Cari Takip Rehberi",
                "Veresiye programı nedir? Bulut ve mobil cari takip, QR bağlantısı, canlı senkronizasyon, borç, tahsilat ve açık bakiye yönetimini keşfedin.",
                new[] { "veresiye programı nedir", "ücretsiz veresiye programı", "bulut veresiye programı", "mobil veresiye programı", "telefondan veresiye takibi", "cari takip programı" }
            ),
            (
                "ucretsiz-veresiye-programi-nedir",
                "Ücretsiz Veresiye Programı Nedir? | Bulut ve Mobil NSX Veresiye",
                "Ücretsiz veresiye programı nedir? Bulut ve Mobil NSX Veresiye ile cari hesapları masaüstü ve telefondan nasıl yöneteceğinizi öğrenin.",
                new[] { "ücretsiz veresiye programı nedir", "ücretsiz veresiye programı", "veresiye takip programı", "müşteri borç takip programı" }
            ),
            (
                "ucretsiz-cari-takip-programi",
                "Ücretsiz Cari Takip Programı | Bulut ve Mobil Müşteri Carisi",
                "Ücretsiz cari takip programı ile müşteri borç, tahsilat ve açık bakiyeyi masaüstü ve mobil Bulut panelinden canlı takip edin.",
                new[] { "ücretsiz cari takip programı", "ücretsiz cari hesap programı", "cari takip programı", "borç alacak takip programı" }
            ),
            (
                "esnaflar-icin-veresiye-takip-programi",
                "Esnaflar İçin Veresiye Takip Programı | Bulut ve Mobil Ücretsiz Çözüm",
                "Esnaflar için ücretsiz veresiye takip programı ile müşteri cari, borç, tahsilat ve açık bakiyeyi masaüstü ve telefondan yönetin.",
                new[] { "esnaflar için veresiye takip programı", "esnaf veresiye defteri programı", "ücretsiz veresiye programı", "veresiye takip programı" }
            ),
            (
                "borc-alacak-takip-programi-ucretsiz",
                "Borç Alacak Takip Programı Ücretsiz | Bulut ve Mobil NSX Veresiye",
                "Ücretsiz borç alacak takip programı ile cari hesap, borç, tahsilat ve açık bakiyeyi masaüstü ve cep telefonundan Bulut sistemiyle yönetin.",
                new[] { "borç alacak takip programı ücretsiz", "borç alacak takip programı", "bulut cari takip programı", "mobil cari takip", "telefondan borç alacak takibi", "tahsilat takip programı" }
            ),
            (
                "bakkal-veresiye-defteri-nasil-tutulur",
                "Bakkal Veresiye Defteri Nasıl Tutulur? | Bulut ve Mobil Takip",
                "Bakkal veresiye defterini dijitalleştirin; müşteri borç, tahsilat ve açık bakiyeyi masaüstü ve cep telefonundan takip edin.",
                new[] { "bakkal veresiye defteri", "bakkal veresiye programı", "veresiye defteri nasıl tutulur", "müşteri borç takibi" }
            ),
            (
                "cari-hesap-takibi-nasil-yapilir",
                "Cari Hesap Takibi Nasıl Yapılır? | Bulut ve Mobil Borç Tahsilat",
                "Cari hesap takibi nasıl yapılır? Borç, tahsilat, geçmiş hareket ve açık bakiyeyi masaüstü ve telefondan takip edin.",
                new[] { "cari hesap takibi", "cari takip programı", "borç alacak takip programı", "müşteri bakiye takibi" }
            ),
            (
                "ucretsiz-cari-hesap-programi",
                "Ücretsiz Cari Hesap Programı | Bulut, Mobil ve Tahsilat Takibi",
                "Ücretsiz cari hesap programı ile borç, tahsilat, cari hareket ve açık bakiyeyi masaüstü ile cep telefonundan canlı takip edin.",
                new[] { "ücretsiz cari hesap programı", "ücretsiz cari takip programı", "cari takip programı", "veresiye takip programı" }
            ),
            (
                "excel-yerine-veresiye-takip-programi",
                "Excel Yerine Bulut ve Mobil Veresiye Takip Programı Kullanmak",
                "Excel yerine veresiye takip programı ile müşteri carilerini, borç ve tahsilatları masaüstü ve mobil Bulut sisteminde yönetin.",
                new[] { "excel veresiye takip", "excel yerine veresiye programı", "veresiye takip programı", "cari takip programı" }
            ),
            (
                "nsx-komisyonlu-cari-takip-pro",
                "NSX Komisyonlu Cari Takip Pro | Server Client Cari Takip Programı",
                "NSX Komisyonlu Cari Takip Pro ile cari hesap, tahsilat, komisyon ve raporlama süreçlerini Server Client yapısıyla merkezi yönetin.",
                new[] { "NSX Komisyonlu Cari Takip Pro", "komisyonlu cari takip programı", "komisyon takip programı", "server client cari takip programı" }
            ),
            (
                "ucretsiz-teknik-servis-programi",
                "Ücretsiz Teknik Servis Programı | Servis ve Cihaz Takibi",
                "Ücretsiz teknik servis programı ile müşteri, cihaz kabul, servis durumu, işlem geçmişi ve teslimat süreçlerini düzenli yönetin.",
                new[] { "ücretsiz teknik servis programı", "teknik servis takip programı", "servis takip programı", "cihaz takip programı" }
            ),
            (
                "oto-tamir-programi",
                "Oto Tamir Programı | Araç Kabul, Servis ve Müşteri Takibi",
                "Oto tamir programı ile araç kabul, müşteri bilgisi, servis işlemleri, ödeme ve teslimat süreçlerini düzenli yönetin.",
                new[] { "oto tamir programı", "oto tamir servis programı", "oto servis programı", "araç servis takip programı" }
            ),
            (
                "oto-galeri-programi",
                "Oto Galeri Programı | Araç Stok, Müşteri ve Satış Takibi",
                "Oto galeri programı ile araç stok, müşteri, satış, ödeme ve galeri süreçlerini tek merkezden daha düzenli takip edin.",
                new[] { "oto galeri programı", "araç takip programı", "galeri programı", "araç satış takip programı" }
            ),
            (
                "servis-live-programi",
                "Servis Live Programı | Canlı Servis ve İş Takibi",
                "Servis Live programı ile servis süreçlerini canlı takip edin, müşteri, iş emri, durum ve teslimat akışını daha pratik yönetin.",
                new[] { "servis live programı", "oto servis live programı", "canlı servis takip programı", "servis takip programı" }
            ),
            (
                "ucretsiz-bilgisayar-hizlandirma-programi",
                "Ücretsiz Bilgisayar Hızlandırma Programı | PC Performans",
                "Ücretsiz bilgisayar hızlandırma programı ile Windows bakım, oyun performansı, FPS artırma ve PC optimizasyonu çözümlerini keşfedin.",
                new[] { "ücretsiz bilgisayar hızlandırma programı", "ücretsiz pc hızlandırma programı", "oyun hızlandırma programı", "fps artırma programı" }
            ),
            (
                "klinik-programi",
                "Klinik Programı | Hasta, Doktor ve Klinik Takip Yazılımı",
                "Klinik programı ile hasta kayıt, doktor yönetimi, randevu, cari işlem ve klinik süreçlerini tek merkezden düzenli takip edin.",
                new[] { "klinik programı", "hasta takip programı", "doktor takip programı", "klinik takip programı" }
            ),
            (
                "randevu-programi",
                "Randevu Programı | Klinik ve İşletme Randevu Takibi",
                "Randevu programı ile hasta, müşteri, doktor, personel ve günlük randevu planlamasını daha düzenli yönetin.",
                new[] { "randevu programı", "klinik randevu programı", "hasta randevu programı", "randevu takip programı" }
            )
        };

        public static string BuildTitle(string? title)
        {
            title = Clean(title);
            if (string.IsNullOrWhiteSpace(title))
                return $"{SiteName} | Ücretsiz Veresiye, Teknik Servis, Oto Tamir, Oto Galeri ve PC Programları";

            if (!title.Contains("NSX", StringComparison.OrdinalIgnoreCase) && title.Length <= 50)
                return $"{title} | {SiteName}";

            return title;
        }

        public static string BuildDescription(string? description)
        {
            description = Clean(description);
            if (string.IsNullOrWhiteSpace(description))
            {
                return "NSX Yazılım; ücretsiz veresiye, teknik servis, oto tamir, oto galeri, servis live, bilgisayar hızlandırma, klinik ve randevu programı çözümleri sunar.";
            }

            return description.Length <= 300 ? description : description[..297] + "...";
        }

        public static readonly string[] CatalogKeywords =
        {
            "NSX Yazılım",
            "işletme yazılımları",
            "ücretsiz veresiye programı",
            "cari takip programı",
            "barkodlu satış programı",
            "stok takip programı",
            "kasa defteri programı",
            "gelir gider takip programı",
            "sigorta acente programı",
            "poliçe takip programı",
            "kod güvenlik analizi",
            "kaynak kod güvenlik taraması",
            "okul ders programı",
            "ders programı hazırlama programı",
            "öğretmen nöbet çizelgesi",
            "okul yönetim yazılımı",
            "teknik servis takip programı",
            "oto tamir programı",
            "oto galeri programı",
            "düğün salonu programı",
            "klinik programı",
            "randevu programı",
            "bilgisayar hızlandırma programı",
            "Windows masaüstü programı"
        };

        public static string Keywords => string.Join(", ", CatalogKeywords);

        public static string ProductKeywords(string productName)
        {
            var name = Clean(productName);
            var lower = name.ToLowerInvariant();
            var keywords = new List<string> { name };

            void Add(params string[] values) => keywords.AddRange(values);

            if (lower.Contains("okul") || lower.Contains("ders program") || lower.Contains("nöbet") || lower.Contains("nobet"))
                Add("okul ders programı", "ders programı hazırlama programı", "öğretmen ders programı", "sınıf ders programı", "nöbet planlama programı", "öğretmen nöbet çizelgesi", "derslik planlama", "okul yönetim yazılımı");
            else if (lower.Contains("security") || lower.Contains("auditor") || lower.Contains("güvenlik"))
                Add("kod güvenlik analizi", "kaynak kod güvenlik taraması", "AI kod analizi", "API güvenlik analizi", "gizli anahtar tespiti", "bağımlılık zafiyet taraması", "SARIF raporu", "SBOM raporu");
            else if (lower.Contains("sigorta") || lower.Contains("acente") || lower.Contains("acenta"))
                Add("sigorta acente programı", "sigorta acentesi programı", "poliçe takip programı", "poliçe yenileme programı", "sigorta müşteri takip programı", "acente komisyon takip programı", "sigorta teklif programı");
            else if (lower.Contains("kasa") && lower.Contains("defter"))
                Add("kasa defteri programı", "gelir gider takip programı", "kasa takip programı", "çoklu kasa programı", "döviz kasa takibi", "tahsilat takip programı", "işletme gelir gider programı");
            else if (lower.Contains("barkod") || (lower.Contains("satış") && lower.Contains("stok")))
                Add("barkodlu satış programı", "stok takip programı", "hızlı satış programı", "cari takip programı", "veresiye takip programı", "market satış programı", "perakende satış programı");
            else if (lower.Contains("klinik") || lower.Contains("randevu"))
                Add("klinik programı", "hasta takip programı", "doktor takip programı", "randevu programı", "klinik randevu programı", "hasta randevu programı");
            else if (lower.Contains("düğün") || lower.Contains("dugun") || lower.Contains("salon") || lower.Contains("organizasyon"))
                Add("düğün salonu programı", "organizasyon takip programı", "salon rezervasyon programı", "etkinlik takip programı", "müşteri tahsilat takip programı");
            else if ((lower.Contains("komisyon") || lower.Contains("komisyonlu")) && lower.Contains("cari"))
                Add("komisyonlu cari takip programı", "komisyon takip programı", "server client cari takip programı", "tahsilat takip programı", "cari hesap programı");
            else if (lower.Contains("veresiye"))
                Add("ücretsiz veresiye programı", "veresiye takip programı", "veresiye defteri", "borç alacak takip programı", "cari takip programı", "müşteri bakiye takip programı");
            else if (lower.Contains("oto") && lower.Contains("tamir"))
                Add("oto tamir programı", "oto servis programı", "araç kabul programı", "araç servis takip programı", "oto tamir müşteri takip programı");
            else if (lower.Contains("oto") && lower.Contains("galeri"))
                Add("oto galeri programı", "araç stok takip programı", "galeri müşteri takip programı", "araç satış takip programı");
            else if (lower.Contains("teknik") || lower.Contains("servis"))
                Add("teknik servis takip programı", "servis takip programı", "cihaz takip programı", "müşteri servis programı", "iş emri takip programı");
            else if (lower.Contains("turbo") || lower.Contains("fps") || lower.Contains("performans") || lower.Contains("hız") || lower.Contains("hiz"))
                Add("bilgisayar hızlandırma programı", "PC performans programı", "Windows hızlandırma programı", "oyun hızlandırma programı", "FPS artırma programı");
            else if (lower.Contains("cari"))
                Add("cari takip programı", "cari hesap programı", "borç alacak takip programı", "müşteri bakiye takip programı", "tahsilat takip programı");

            Add($"{name} indir", $"{name} fiyat", "NSX Yazılım");
            return string.Join(", ", keywords.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(16));
        }

        public static string CreateSlug(string text)
        {
            var value = (text ?? string.Empty).Trim().ToLowerInvariant();
            value = value.Replace("ç", "c").Replace("ğ", "g").Replace("ı", "i").Replace("ö", "o").Replace("ş", "s").Replace("ü", "u");
            value = value.Replace("Ç", "c").Replace("Ğ", "g").Replace("İ", "i").Replace("Ö", "o").Replace("Ş", "s").Replace("Ü", "u");
            value = Regex.Replace(value, "[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(value) ? "sayfa" : value;
        }

        public static string ProductTitle(string productName, string? metaTitle = null)
        {
            if (!string.IsNullOrWhiteSpace(metaTitle) && Clean(metaTitle).Length >= 50)
                return BuildTitle(metaTitle);

            var name = Clean(productName);
            var lower = name.ToLowerInvariant();

            if (lower.Contains("okul") || lower.Contains("ders program") || lower.Contains("nöbet") || lower.Contains("nobet"))
                return $"{name} | Ders Programı ve Nöbet Planlama";

            if (lower.Contains("security") || lower.Contains("auditor") || lower.Contains("güvenlik"))
                return $"{name} | AI Destekli Kod Güvenlik Analizi";

            if (lower.Contains("sigorta") || lower.Contains("acente") || lower.Contains("acenta"))
                return $"{name} | Poliçe, Müşteri ve Yenileme Takibi";

            if (lower.Contains("kasa") && lower.Contains("defter"))
                return $"{name} | Gelir Gider, Kasa ve Cari Takip";

            if (lower.Contains("barkod") || (lower.Contains("satış") && lower.Contains("stok")))
                return $"{name} | Barkodlu Satış, Stok ve Cari Takip";

            if (lower.Contains("klinik") || lower.Contains("randevu"))
                return $"{name} | Hasta, Doktor ve Randevu Takibi";

            if (lower.Contains("düğün") || lower.Contains("dugun") || lower.Contains("salon") || lower.Contains("organizasyon"))
                return $"{name} | Rezervasyon ve Organizasyon Takibi";

            if ((lower.Contains("komisyon") || lower.Contains("komisyonlu")) && lower.Contains("cari"))
                return $"{name} | Server Client Cari Takip ve Komisyon Yönetimi";

            if (lower.Contains("veresiye"))
                return $"{name} | Ücretsiz Veresiye Defteri ve Veresiye Takip Programı";

            if (lower.Contains("oto") && lower.Contains("tamir"))
                return $"{name} | Oto Tamir Programı ve Araç Servis Takibi";

            if (lower.Contains("oto") && lower.Contains("galeri"))
                return $"{name} | Oto Galeri Programı ve Araç Stok Takibi";

            if (lower.Contains("servis") && lower.Contains("live"))
                return $"{name} | Servis Live Programı ve Canlı Servis Takibi";

            if (lower.Contains("teknik") || lower.Contains("servis"))
                return $"{name} | Ücretsiz Teknik Servis Takip Programı";

            if (lower.Contains("turbo") || lower.Contains("fps") || lower.Contains("hız") || lower.Contains("hiz"))
                return $"{name} | Ücretsiz Bilgisayar Hızlandırma, Oyun Hızlandırma ve FPS Artırma";

            return $"{name} | Lisanslı Yazılım - NSX Yazılım";
        }

        public static string ProductDescription(string productName, string? metaDescription, string? description)
        {
            if (!string.IsNullOrWhiteSpace(metaDescription) && Clean(metaDescription).Length >= 100)
                return BuildDescription(metaDescription);

            var name = Clean(productName);
            var desc = Clean(description);
            var lower = name.ToLowerInvariant();

            if (lower.Contains("okul") || lower.Contains("ders program") || lower.Contains("nöbet") || lower.Contains("nobet"))
                return BuildDescription($"{name}; öğretmen, sınıf, derslik, haftalık ders programı ve nöbet çizelgesini tek merkezden düzenlemek, raporlamak ve paylaşmak için geliştirilmiştir.");

            if (lower.Contains("security") || lower.Contains("auditor") || lower.Contains("güvenlik"))
                return BuildDescription($"{name}; kaynak kod, API, bağımlılık ve yayın çıktılarındaki güvenlik risklerini, gizli anahtarları ve riskli kod akışlarını AI destekli analizle tespit eder.");

            if (lower.Contains("sigorta") || lower.Contains("acente") || lower.Contains("acenta"))
                return BuildDescription($"{name}; müşteri, poliçe, teklif, tahsilat, komisyon ve yenileme süreçlerini tek merkezden yöneten profesyonel sigorta acente programıdır.");

            if (lower.Contains("kasa") && lower.Contains("defter"))
                return BuildDescription($"{name}; gelir, gider, tahsilat, ödeme, çoklu kasa, döviz kasası ve cari hesap süreçlerini tek merkezden yönetmek için geliştirilmiş kasa defteri programıdır.");

            if (lower.Contains("barkod") || (lower.Contains("satış") && lower.Contains("stok")))
                return BuildDescription($"{name}; barkodlu satış, ürün, stok, cari hesap, veresiye ve tahsilat işlemlerini tek programdan yönetmek için geliştirilmiş profesyonel satış çözümüdür.");

            if (lower.Contains("klinik") || lower.Contains("randevu"))
                return BuildDescription($"{name}; hasta, doktor, randevu, muayene, ödeme ve klinik süreçlerini tek merkezden düzenli yönetmek için geliştirilmiş klinik takip programıdır.");

            if (lower.Contains("düğün") || lower.Contains("dugun") || lower.Contains("salon") || lower.Contains("organizasyon"))
                return BuildDescription($"{name}; rezervasyon, etkinlik, müşteri, salon, ödeme ve tahsilat süreçlerini tek merkezden yönetmeye yardımcı olan düğün salonu ve organizasyon programıdır.");

            if ((lower.Contains("komisyon") || lower.Contains("komisyonlu")) && lower.Contains("cari"))
                return BuildDescription($"{name}; cari hesap, tahsilat, komisyon ve raporlama süreçlerini Server Client yapısıyla merkezi yönetmek için geliştirilmiş profesyonel cari takip programıdır.");

            if (lower.Contains("veresiye"))
                return BuildDescription($"{name} ile müşteri borç alacak takibini kolaylaştırın. Ücretsiz veresiye programı ve veresiye takip çözümünü ömür boyu sınırsız kullanın.");

            if (lower.Contains("oto") && lower.Contains("tamir"))
                return BuildDescription($"{name}; oto tamir işletmeleri için araç kabul, müşteri, servis işlemi, ödeme ve teslimat takibini düzenli yönetmeye yardımcı olur.");

            if (lower.Contains("oto") && lower.Contains("galeri"))
                return BuildDescription($"{name}; oto galeri işletmeleri için araç stok, müşteri, satış, ödeme ve galeri süreçlerini tek merkezden takip etmeye yardımcı olur.");

            if (lower.Contains("servis") && lower.Contains("live"))
                return BuildDescription($"{name}; servis süreçlerini canlı takip etmek, iş emri durumlarını görmek ve müşteri teslimat akışını yönetmek için geliştirilmiştir.");

            if (lower.Contains("teknik") || lower.Contains("servis"))
                return BuildDescription($"{name} ile teknik servis kayıtları, müşteri takipleri, cihaz kabul süreçleri ve servis durumlarını ücretsiz teknik servis programı mantığıyla yönetin.");

            if (lower.Contains("turbo") || lower.Contains("fps") || lower.Contains("hız") || lower.Contains("hiz"))
                return BuildDescription($"{name}; ücretsiz bilgisayar hızlandırma programı, oyun hızlandırma, FPS artırma ve Windows performans iyileştirme için geliştirilen NSX çözümüdür.");

            if (!string.IsNullOrWhiteSpace(desc))
                return BuildDescription(desc);

            return BuildDescription($"{name} ürününü NSX Yazılım güvencesiyle inceleyin, demo deneyin ve lisanslı kullanıma geçin.");
        }

        public static string AbsoluteUrl(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Domain;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return path;

            return Domain.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        public static string CanonicalUrl(string? path)
        {
            return AbsoluteUrl(CanonicalPath(path));
        }

        public static string CanonicalPath(string? path)
        {
            var value = (path ?? "/").Trim();

            if (string.IsNullOrWhiteSpace(value))
                return "/";

            var queryIndex = value.IndexOf('?');
            if (queryIndex >= 0)
                value = value[..queryIndex];

            value = "/" + value.Trim('/');
            var lower = value.ToLowerInvariant();

            return lower switch
            {
                "/" or "/home" or "/home/index" => "/",
                "/store/index" => "/store",
                "/yazilimlar" => "/store",
                "/home/iletisim" => "/iletisim",
                "/home/kvkk" => "/kvkk",
                "/home/iadepolitikasi" => "/iade-politikasi",
                "/home/privacy" => "/gizlilik-politikasi",
                _ => lower
            };
        }

        private static string Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return Regex.Replace(ProductContentSanitizer.ToPlainText(value), "\\s+", " ").Trim();
        }
    }
}
