using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    public class BlogController : Controller
    {
        private static readonly DateTimeOffset BlogContentPublishedAt = new(2026, 6, 27, 12, 0, 0, TimeSpan.FromHours(3));
        private static readonly DateTimeOffset BlogContentModifiedAt = new(2026, 7, 31, 2, 20, 0, TimeSpan.FromHours(3));
        private static readonly DateTimeOffset VeresiyeCloudGuideModifiedAt = new(2026, 8, 10, 23, 44, 0, TimeSpan.FromHours(3));
        private readonly ApplicationDbContext _context;
        private readonly SiteLocalizationService _localization;

        public BlogController(ApplicationDbContext context, SiteLocalizationService localization)
        {
            _context = context;
            _localization = localization;
        }

        public sealed record SeoSection(string Heading, string Content);
        public sealed record SeoFaq(string Question, string Answer);

        public sealed record SeoPage(
            string Slug,
            string Title,
            string Description,
            string H1,
            string Body,
            string[] Keywords,
            bool IsLanding = false,
            string? CtaText = null,
            string? CtaUrl = null,
            string? ProductSearch = null,
            SeoSection[]? Sections = null,
            SeoFaq[]? Faqs = null)
        {
            public string? ResolvedImageUrl { get; init; }
            public string? ResolvedImageAlt { get; init; }
            public string ImageUrl => ResolvedImageUrl ?? GetBlogImageUrl(Slug);
            public string ImageAlt => ResolvedImageAlt ?? GetBlogImageAlt(Slug, H1);
            public int ImageWidth => GetBlogImageSize(Slug).Width;
            public int ImageHeight => GetBlogImageSize(Slug).Height;
            public DateTimeOffset PublishedAt => IsSeptemberGuide(Slug) ? SeptemberGuidePublishedAt : BlogContentPublishedAt;
            public DateTimeOffset ModifiedAt => IsSeptemberGuide(Slug) ? SeptemberGuidePublishedAt : IsFreeVeresiyePageSlug(Slug) ? VeresiyeCloudGuideModifiedAt : BlogContentModifiedAt;
        }

        private static readonly DateTimeOffset SeptemberGuidePublishedAt = new(2026, 9, 14, 0, 0, 0, TimeSpan.FromHours(3));
        private static bool IsSeptemberGuide(string slug) => slug is "nsx-cari-takip-pro-bulut-programi" or "nsx-veri-kurtarma-pro-programi";

        public static readonly IReadOnlyList<SeoPage> Pages = new List<SeoPage>
        {
            new(
                "nsx-cari-takip-pro-bulut-programi",
                "NSX Cari Takip Pro Bulut | Çoklu Bilgisayar ve Mobil Rehberi",
                "NSX Cari Takip Pro Bulut ile çoklu bilgisayarda cari hesap yönetin. QR ile mobil bağlantı, çevrimdışı çalışma, borç ve tahsilat adımlarını keşfedin.",
                "NSX Cari Takip Pro Bulut ile Bilgisayar ve Mobilde Ortak Cari Takip",
                "Kasa, muhasebe ve yönetici bilgisayarında ayrı cari listeleri tutulduğunda aynı müşterinin bakiyesi farklı görünebilir. NSX Cari Takip Pro Bulut; müşteri, borç, alacak, tahsilat ve ödeme kayıtlarını yetkili bilgisayarlar arasında senkronize ederek işletmenin ortak veriler üzerinde çalışmasını sağlar.\n\nMasaüstü programı yerel veritabanını bulutla birlikte kullanır. İnternet bağlantısı geçici olarak kesildiğinde yerel kayıtlarla çalışılabilir; bağlantı yeniden sağlandığında bekleyen işlemler aktarılır. Diğer cihazlardaki güncel durumu değerlendirirken senkronizasyonun tamamlandığını kontrol etmek gerekir.\n\nOfis dışındaki kullanım için telefonun internet tarayıcısından mobil panele bağlanılır. Müşteri aranabilir, cari hareketler incelenebilir, yeni müşteri oluşturulabilir ve borç veya tahsilat kaydedilebilir. Örneğin sahada kaydedilen bir tahsilat, senkronizasyon tamamlandığında işletmedeki yetkili bilgisayarların müşteri hesabına yansır.\n\nBu rehber, ilk bağlantıdan günlük hesap kontrolüne kadar temel çalışma düzenini açıklar. Kurulum dosyası, güncel sürüm ve lisans seçeneklerine aşağıdaki ürün bağlantısından ulaşabilirsiniz.",
                new[] { "NSX Cari Takip Pro Bulut", "bulut cari takip programı", "çoklu bilgisayar cari takip", "mobil cari takip", "QR ile cari takip", "borç tahsilat takibi", "çevrimdışı cari takip" },
                false, "NSX Cari Takip Pro Bulut'u İncele", "/urun/nsx-cari-takip-pro-bulut", "NSX Cari Takip Pro Bulut",
                Sections: new[]
                {
                    new SeoSection("1. Merkez ve terminal düzenini hazırlayın", "Programı işletmenin kullanacağı Windows bilgisayarlarda kurun. Ana bilgisayardaki lisans ve firma düzenini tamamlayarak yetkili terminalleri tanımlayın. Kasa, muhasebe ve yönetici bilgisayarlarının aynı firma verileriyle çalıştığını kontrol edin."),
                    new SeoSection("2. Kullanıcı hesaplarını ve yetkileri düzenleyin", "Her çalışan için ayrı kullanıcı hesabı oluşturun. Admin ve personel rollerini görev dağılımına göre belirleyin; aktif ve pasif kullanıcıları düzenli kontrol edin. Terminal ve kullanıcı yönetimini yetkili kişiler üzerinden yürütün."),
                    new SeoSection("3. QR kod ile mobil bağlantı kurun", "Masaüstünde Ayarlar > Bulut Ayarları bölümünü açıp mobil bağlantı için QR kod oluşturun. Telefon kamerasıyla kodu tarayın ve açılan bağlantıyı onaylayın. Süreli mobil oturum üzerinden cari panele ulaşabilir, gerektiğinde oturumu bilgisayardan sonlandırabilirsiniz."),
                    new SeoSection("4. Müşteri, borç ve tahsilat işlemlerini kaydedin", "Müşteriyi adına veya telefon numarasına göre bulun. İşlem tarihini, tutarını ve açıklamasını kontrol ederek borç veya tahsilatı kaydedin. Mobil panelde müşteri kartı oluşturma, bilgi güncelleme ve hareket geçmişini inceleme işlemleri de yapılabilir."),
                    new SeoSection("5. İnternet kesintilerinde senkronizasyonu takip edin", "Masaüstünde yerel verilerle çalışmaya devam edilebilir. Bağlantı geri geldiğinde bekleyen kayıtlar buluta ve diğer cihazlara aktarılır. Çevrimdışı bir bilgisayarda henüz diğer cihazlardan gelmemiş hareketler bulunabileceğini dikkate alın; ortak bakiye kontrolünü eşitleme sonrasında yapın."),
                    new SeoSection("6. Vade, ekstre ve raporlarla günü kontrol edin", "Taksit ve vadeleri takip edin; günlük ve dönemsel tahsilat özetlerini, müşteri bakiyelerini ve hesap ekstrelerini inceleyin. PDF ve yazıcı çıktılarıyla raporları paylaşabilir, borç ve ödeme hareketlerini düzenli olarak değerlendirebilirsiniz."),
                    new SeoSection("Hangi işletmeler için uygundur?", "Vadeli satış yapan mağazalar, toptancılar, perakendeciler, teknik servisler ve sahada tahsilat yapan ekipler için uygundur. Birden fazla bilgisayarda ortak kayıt tutmak ve ofis dışında cari hesaplara erişmek isteyen işletmelere yöneliktir.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Birden fazla bilgisayar aynı cari hesapları kullanabilir mi?", "Evet. Yetkili merkez ve terminal bilgisayarları aynı firma verileriyle çalışabilir. İşlemler senkronizasyon tamamlandığında diğer cihazlara yansır."),
                    new SeoFaq("Telefona ayrı bir uygulama kurmak gerekir mi?", "Hayır. Masaüstündeki Ayarlar > Bulut Ayarları bölümünden oluşturulan QR kodla telefonun güncel internet tarayıcısında mobil panele bağlanılır."),
                    new SeoFaq("İnternet kesilince program kullanılabilir mi?", "Masaüstü programında yerel verilerle çalışılabilir. İnternet bağlantısı geri geldiğinde bekleyen işlemler eşitlenir. Mobil erişim ve cihazlar arası güncelleme için bağlantı gerekir."),
                    new SeoFaq("Telefondan borç ve tahsilat kaydedilebilir mi?", "Evet. Mobil panelde müşteri seçilerek borç veya tahsilat girilebilir; cari hareketler ve müşteri bakiyesi görüntülenebilir."),
                    new SeoFaq("Çalışanların erişimi ayrı yönetilebilir mi?", "Evet. Ayrı kullanıcı hesapları, admin ve personel rolleri, aktif veya pasif kullanıcı yönetimi ve mobil oturum kontrolü bulunur.")
                }),
            new(
                "nsx-veri-kurtarma-pro-programi",
                "NSX Veri Kurtarma Pro | Silinen Dosyaları Kurtarma Rehberi",
                "NSX Veri Kurtarma Pro ile HDD, SSD, USB ve hafıza kartlarını tarayın. Tarama seçimi, dosya önizleme, filtreleme ve farklı diske kurtarma adımlarını öğrenin.",
                "NSX Veri Kurtarma Pro ile Tarama, Önizleme ve Dosya Kurtarma",
                "Yanlışlıkla silinen fotoğraflar, kaybolan belgeler veya erişilemeyen dosyalar için ilk adım doğru depolama aygıtını ve tarama yöntemini seçmektir. NSX Veri Kurtarma Pro; Windows üzerinden erişilebilen HDD, SSD, USB bellek, SD kart ve microSD aygıtlarında kurtarılabilir dosya adaylarını bulmak için geliştirilmiştir.\n\nHızlı Tarama, Derin Tarama ve Klasör Tarama seçenekleri farklı arama ihtiyaçlarına yanıt verir. Bulunan sonuçlar klasör yolu veya dosya türü üzerinden incelenebilir; desteklenen fotoğraf ve medya dosyaları kurtarma öncesinde önizlenebilir. Böylece geniş bir sonuç listesinden ihtiyaç duyulan dosyaları seçmek kolaylaşır.\n\nProgram kaynak aygıtı salt okunur çalışma yaklaşımıyla ele alır. Kurtarılan dosyaların farklı bir fiziksel diske kaydedilmesi, kaynak alana yazma riskini azaltan çalışma düzeninin temelidir. Tarama sonucunda bir dosyanın listelenmesi, içeriğinin eksiksiz kurtarılacağını garanti etmez; sonuçlar verinin mevcut durumuna bağlıdır.\n\nAşağıdaki adımlar tarama, sonuçları değerlendirme ve kurtarma sürecini düzenli yürütmek için hazırlanmıştır. Ürünün güncel kurulum dosyasına ve lisans seçeneklerine ürün sayfasından ulaşabilirsiniz.",
                new[] { "NSX Veri Kurtarma Pro", "veri kurtarma programı", "silinen dosyaları kurtarma", "HDD veri kurtarma", "SSD veri kurtarma", "USB dosya kurtarma", "hafıza kartı fotoğraf kurtarma" },
                false, "NSX Veri Kurtarma Pro'yu İncele", "/urun/nsx-veri-kurtarma-pro", "NSX Veri Kurtarma Pro",
                Sections: new[]
                {
                    new SeoSection("1. Kaynak aygıtı ve hedef diski belirleyin", "Dosyaların kaybolduğu HDD, SSD, USB bellek veya hafıza kartını belirleyin. Kurtarılan dosyalar için farklı bir fiziksel diskte hedef klasör hazırlayın. Kaynak aygıta yeni dosya kopyalamaktan kaçınarak kurtarma işlemini planlayın."),
                    new SeoSection("2. Uygun tarama yöntemini seçin", "Hızlı Tarama dosya sistemi kayıtlarını analiz ederek dosya adaylarını arar. Daha kapsamlı inceleme için Derin Tarama kullanılabilir. Belirli bir konuma odaklanmak istediğinizde Klasör Tarama seçeneğiyle arama alanını daraltabilirsiniz."),
                    new SeoSection("3. Sonuçları YOL ve TÜR görünümünde inceleyin", "YOL görünümüyle klasör yapısını takip edin. TÜR görünümünde fotoğraf, video, belge, ses ve arşiv gruplarını ayrı inceleyin. Bu iki görünüm, aradığınız dosyayı konumuna veya biçimine göre bulmanıza yardımcı olur."),
                    new SeoSection("4. Global Pro filtreleriyle listeyi daraltın", "0 KB dosyaları, küçük resim ve önbellek içeriklerini, geçici dosyaları veya belirlediğiniz boyutun altındaki sonuçları gizleyebilirsiniz. Minimum kurtarma güveni filtresini kullanırken filtrelerin bazı adayları görünümden çıkarabileceğini dikkate alın."),
                    new SeoSection("5. Kurtarmadan önce önizleme yapın", "Desteklenen fotoğraf ve medya dosyalarını önizleyerek içeriklerini kontrol edin. Dosya sağlık durumu ve kurtarma güveni bilgilerini birlikte değerlendirin. Önizleme desteği dosya türüne bağlıdır; listedeki her adayın eksiksiz açılacağı varsayılmamalıdır."),
                    new SeoSection("6. Seçilen dosyaları farklı diske kaydedin", "İhtiyacınız olan sonuçları seçip hazırladığınız hedef klasöre kurtarın. Kaynakla aynı fiziksel diski hedef seçmeyin; programın hedef disk güvenlik kontrollerini dikkate alın. İşlem bittiğinde kurtarılan dosyaları açarak içeriklerini doğrulayın."),
                    new SeoSection("Desteklenen dosyalar ve kullanım alanları", "Fotoğraflar, videolar, PDF, Word, Excel, metin ve arşiv dosyaları gibi farklı türler tek çalışma alanında incelenebilir. Windows 10 ve Windows 11 kullanan bireysel kullanıcılar ve teknik servisler için tarama, filtreleme ve önizlemeyi bir araya getirir.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Hangi depolama aygıtları taranabilir?", "Windows tarafından erişilebilen uyumlu dahili ve harici HDD, SSD, USB bellek, SD kart ve microSD aygıtları taranabilir."),
                    new SeoFaq("Hızlı Tarama ile Derin Tarama arasındaki fark nedir?", "Hızlı Tarama dosya sistemi kayıtlarına odaklanır. Derin Tarama depolama alanını daha kapsamlı inceleyerek standart taramada görünmeyen dosya adaylarını araştırır."),
                    new SeoFaq("Dosyaları kurtarmadan önce görebilir miyim?", "Desteklenen fotoğraf ve medya dosyaları önizlenebilir. Önizleme olanağı dosya türüne ve bulunan verinin durumuna bağlıdır."),
                    new SeoFaq("Kurtarılan dosyaları aynı diske kaydedebilir miyim?", "Farklı bir fiziksel disk hedeflenmelidir. Kaynakla aynı fiziksel diske yazmak kaynak veriyi etkileyebilir; program bu duruma yönelik güvenlik kontrolleri içerir."),
                    new SeoFaq("Bulunan her dosya eksiksiz kurtarılır mı?", "Hayır. Tarama sonuçları kurtarılabilir dosya adaylarını gösterir. Dosyanın durumu sonucu etkiler; kurtarma sonrasında dosyaları açıp kontrol etmek gerekir.")
                }),
            new(
                "ucretsiz-veresiye-programi",
                "Ücretsiz Veresiye Programı | Bulut ve Mobil Cari Takip",
                "NSX Veresiye Takip Pro ile müşteri cari hesaplarını, borç ve tahsilatları masaüstü ve cep telefonundan Bulut sistemiyle ücretsiz yönetin.",
                "Ücretsiz Veresiye Programı",
                "NSX Veresiye Takip Pro; müşteri cari hesaplarını, borçları, tahsilatları, açık bakiyeleri ve işlem geçmişini düzenli takip etmek isteyen esnaf ve işletmeler için geliştirilmiş ücretsiz bir veresiye takip programıdır. Masaüstü programı hızlı günlük kullanım için çalışırken v1.0.5 ile eklenen Bulut ve Mobil Yönetim Sistemi, temel cari işlemleri cep telefonuna taşır.\n\nCloud Bağlantısı ekranından oluşturulan güvenli QR kod telefonla okutularak işletmeye ait mobil panele bağlanılır. Mobil ana ekranda Toplam Borç, Toplam Tahsilat ve Açık Bakiye özetleri görüntülenir. Alt bölümde müşteri adına göre canlı arama yapılabilir; müşteri seçildiğinde cari hareketleri açılır ve geçmiş borç ile tahsilat kayıtları tek ekranda görülebilir.\n\nYeni borç veya tahsilat işlemi doğrudan seçilen müşterinin cari hesabından yapılır. Masaüstünde veya telefonda yapılan değişiklikler canlı senkronizasyon ile diğer tarafa aktarılır. Böylece işletme sahibi iş yerinde olmasa bile müşterinin bakiyesini kontrol edebilir ve gerekli cari işlemi mobil cihazından kaydedebilir.\n\nMevcut firma, müşteri, borç ve tahsilat kayıtları korunarak güncel sürüme geçilebilir. Masaüstü çalışma düzeni devam ederken Bulut ve Mobil erişim, veresiye takibini daha erişilebilir ve pratik hale getirir.",
                new[] { "ücretsiz veresiye programı", "bulut veresiye programı", "mobil veresiye programı", "telefondan veresiye takibi", "cari takip programı", "borç alacak takip programı", "tahsilat takip programı", "QR ile cari takip", "canlı senkronizasyon", "NSX Veresiye Takip Pro" },
                true,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("v1.0.5 Bulut ve Mobil Yönetim", "Güncel sürümde masaüstü programına Bulut ve Mobil Yönetim Sistemi eklenmiştir. QR bağlantısı ile yetkilendirilen telefon üzerinden müşteri arama, cari hareket görüntüleme, borç ve tahsilat girişi yapılabilir."),
                    new SeoSection("Sade mobil kullanım", "Mobil panel yalnızca günlük cari işlemlere odaklanır: işletme özetleri, müşteri arama, müşteri cari hareketleri, borç girişi ve tahsilat. Gereksiz menülerle kullanım karmaşıklaştırılmaz."),
                    new SeoSection("Canlı senkronizasyon", "Masaüstü ve mobil tarafta yapılan değişiklikler senkronize edilir. Toplam borç, toplam tahsilat ve açık bakiye hesapları aynı cari hareketlere göre güncel tutulur."),
                    new SeoSection("Mevcut veriler korunur", "Güncelleme, mevcut müşteri ve cari kayıtların üzerine yeni Bulut ve Mobil özelliklerini ekleyecek şekilde hazırlanmıştır; mevcut çalışma düzeni korunur.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "ucretsiz-teknik-servis-programi",
                "Ücretsiz Teknik Servis Programı | Servis ve Cihaz Takibi",
                "Ücretsiz teknik servis programı ile müşteri, cihaz kabul, servis durumu, işlem geçmişi ve teslimat süreçlerini düzenli yönetin.",
                "Ücretsiz Teknik Servis Programı",
                "Ücretsiz teknik servis programı; bilgisayar, telefon, elektronik ve benzeri teknik servis işletmelerinde cihaz kabul, arıza kaydı, müşteri bilgisi, işlem durumu, servis geçmişi, teslimat ve ödeme takibi için kullanılır. NSX Teknik Servis takip çözümü, servis yoğunluğunu daha düzenli yönetmek ve kayıtları kaybetmeden takip etmek isteyen işletmeler için geliştirilir. Cihazın hangi aşamada olduğu, müşteriye ne işlem yapıldığı ve geçmiş kayıtlar tek merkezden izlenebilir.",
                new[] { "ücretsiz teknik servis programı", "teknik servis takip programı", "servis takip programı", "cihaz takip programı" },
                true,
                "Teknik Servis Programını İncele",
                "/urun/nsx-teknik-servis-pro-2",
                "teknik servis"),
            new(
                "oto-tamir-programi",
                "Oto Tamir Programı | Araç Kabul, Servis ve Müşteri Takibi",
                "Oto tamir programı ile araç kabul, müşteri bilgisi, servis işlemleri, ödeme ve teslimat süreçlerini düzenli yönetin.",
                "Oto Tamir Programı",
                "Oto tamir programı; araç kabul, müşteri bilgisi, plaka ve araç kaydı, servis işlemleri, ödeme takibi ve teslimat süreçlerini daha düzenli yönetmek isteyen oto servis işletmeleri için hazırlanır. NSX Yazılım çözümleri, oto tamir servislerinde günlük iş akışını sadeleştirmeyi, kayıtların kaybolmasını önlemeyi ve servis durumunu daha anlaşılır takip etmeyi hedefler.",
                new[] { "oto tamir programı", "oto tamir servis programı", "oto servis programı", "araç servis takip programı" },
                true,
                "Oto Tamir Programını İncele",
                "/store?q=oto%20tamir",
                "oto tamir"),
            new(
                "oto-galeri-programi",
                "Oto Galeri Programı | Araç Stok, Müşteri ve Satış Takibi",
                "Oto galeri programı ile araç stok, müşteri, satış, ödeme ve galeri süreçlerini tek merkezden daha düzenli takip edin.",
                "Oto Galeri Programı",
                "Oto galeri programı; araç stoklarını, müşteri görüşmelerini, satış süreçlerini, ödeme durumlarını ve galeri içi takip ihtiyaçlarını daha düzenli yönetmek için geliştirilir. NSX Yazılım yaklaşımı, oto galeri işletmelerinin araç bilgilerini, müşteri kayıtlarını ve satış süreçlerini sade ve anlaşılır bir sistemde takip etmesini hedefler.",
                new[] { "oto galeri programı", "galeri programı", "araç takip programı", "araç satış takip programı" },
                true,
                "Oto Galeri Programını İncele",
                "/store?q=oto%20galeri",
                "oto galeri"),
            new(
                "servis-live-programi",
                "Servis Live Programı | Canlı Servis ve İş Takibi",
                "Servis Live programı ile servis süreçlerini canlı takip edin, müşteri, iş emri, durum ve teslimat akışını daha pratik yönetin.",
                "Servis Live Programı",
                "Servis Live programı; servis işletmelerinde iş emri, durum takibi, müşteri bilgilendirme, teslimat süreci ve günlük servis akışını daha canlı ve düzenli takip etmek için hazırlanır. NSX Yazılım çözümleri, servis kayıtlarının daha hızlı görülmesini ve işletme içinde takip kolaylığı sağlamayı hedefler.",
                new[] { "servis live programı", "oto servis live programı", "canlı servis takip programı", "servis takip programı" },
                true,
                "Servis Live Programını İncele",
                "/store?q=servis%20live",
                "servis live"),
            new(
                "ucretsiz-bilgisayar-hizlandirma-programi",
                "Ücretsiz Bilgisayar Hızlandırma Programı | PC Performans",
                "Ücretsiz bilgisayar hızlandırma programı ile Windows bakım, oyun performansı, FPS artırma ve PC optimizasyonu çözümlerini keşfedin.",
                "Ücretsiz Bilgisayar Hızlandırma Programı",
                "Ücretsiz bilgisayar hızlandırma programı; Windows performansını daha akıcı hale getirmek, gereksiz yükleri azaltmak, oyun performansını desteklemek ve FPS artırma odaklı pratik bakım yapmak isteyen kullanıcılar için hazırlanır. NSX Turbo; bilgisayar hızlandırma, oyun hızlandırma, başlangıç yüklerini azaltma ve sistem optimizasyonu gibi işlemleri daha sade bir kullanım deneyimiyle sunmayı hedefler.",
                new[] { "ücretsiz bilgisayar hızlandırma programı", "ücretsiz pc hızlandırma programı", "oyun hızlandırma programı", "fps artırma programı", "NSX Turbo" },
                true,
                "NSX Turbo Ürünlerini İncele",
                "/store?q=turbo",
                "turbo"),
            new(
                "klinik-programi",
                "Klinik Programı | Hasta, Doktor ve Klinik Takip Yazılımı",
                "Klinik programı ile hasta kayıt, doktor yönetimi, randevu, cari işlem ve klinik süreçlerini tek merkezden düzenli takip edin.",
                "Klinik Programı",
                "Klinik programı; hasta kayıtları, doktor bilgileri, randevular, muayene notları, cari işlemler ve klinik içi günlük akışı düzenli yönetmek isteyen işletmeler için hazırlanır. NSX Klinik yaklaşımı; hasta arama, doktor arama, randevu takibi, ödeme ve işlem geçmişi gibi alanları sade, hızlı ve anlaşılır bir panelde toplamayı hedefler. Klinik, muayenehane, güzellik merkezi ve randevulu çalışan işletmeler için düzenli takip sağlar.",
                new[] { "klinik programı", "hasta takip programı", "doktor takip programı", "klinik takip programı" },
                true,
                "Klinik Çözümleri İçin İletişime Geç",
                "/iletisim",
                "klinik"),
            new(
                "randevu-programi",
                "Randevu Programı | Klinik ve İşletme Randevu Takibi",
                "Randevu programı ile hasta, müşteri, doktor, personel ve günlük randevu planlamasını daha düzenli yönetin.",
                "Randevu Programı",
                "Randevu programı; doktor, personel, müşteri veya hasta randevularını günlük, haftalık ve aylık düzende takip etmek isteyen işletmeler için geliştirilir. Klinik randevu programı, güzellik merkezi randevu takibi veya servis randevu yönetimi gibi alanlarda çakışmaları azaltmaya ve planlamayı düzenli tutmaya yardımcı olur. NSX Yazılım çözümleri, sade ekranlar ve hızlı arama yapısıyla randevu takibini kolaylaştırmayı hedefler.",
                new[] { "randevu programı", "klinik randevu programı", "hasta randevu programı", "randevu takip programı" },
                true,
                "Randevu Çözümleri İçin İletişime Geç",
                "/iletisim",
                "randevu"),
            new(
                "nsx-komisyonlu-cari-takip-pro",
                "NSX Komisyonlu Cari Takip Pro | Server Client Cari Takip Programı",
                "NSX Komisyonlu Cari Takip Pro ile cari hesap, tahsilat, komisyon ve raporlama süreçlerini Server Client yapısıyla merkezi yönetin.",
                "NSX Komisyonlu Cari Takip Pro",
                @"NSX Komisyonlu Cari Takip Pro, komisyonlu çalışan işletmelerin cari hesap, tahsilat, komisyon ve raporlama süreçlerini tek merkezden yönetebilmesi için geliştirilmiş profesyonel bir Server & Client cari takip programıdır. Bayi, aracı, komisyoncu, toptancı ve saha satış ekibiyle çalışan işletmeler; müşteri hesaplarını, borç-alacak durumlarını, komisyon kayıtlarını ve tahsilat hareketlerini daha düzenli takip edebilir.

Server & Client yapısı sayesinde ana bilgisayara Server kurulumu yapılır, diğer bilgisayarlardaki Client uygulamaları aynı sisteme bağlanarak ortak veri üzerinden çalışır. Böylece her bilgisayarda ayrı kayıt tutulmaz, tüm bilgiler merkezi olarak yönetilir. Çoklu kullanıcı desteği, ekip içindeki kullanıcıların aynı sistem üzerinde düzenli şekilde işlem yapmasına yardımcı olur.

Program ile cari kartlar oluşturabilir, müşteri bakiyelerini takip edebilir, tahsilat girişleri yapabilir, komisyon bilgilerini yönetebilir ve işletmenin genel durumunu raporlar üzerinden inceleyebilirsiniz. Hangi cariden ne kadar tahsilat yapıldığı, toplam bakiye durumu, komisyon özeti ve geçmiş hareketler daha net görüntülenir. Manuel defter veya dağınık Excel dosyaları yerine cari ve komisyon işlemlerini tek program üzerinden kontrol etmek isteyen işletmeler için güçlü bir çözümdür.

Öne çıkan özellikler; cari hesap takibi, komisyon yönetimi, tahsilat kayıtları, Server & Client çalışma sistemi, çoklu kullanıcı desteği, merkezi veri yönetimi, detaylı raporlama, otomatik yedekleme desteği, kullanıcı dostu arayüz ve Windows uyumlu yapıdır. NSX Komisyonlu Cari Takip Pro ürün sayfasından lisans seçeneklerini inceleyebilir ve demo indirme işlemini başlatabilirsiniz.",
                new[] { "NSX Komisyonlu Cari Takip Pro", "komisyonlu cari takip programı", "komisyon takip programı", "server client cari takip programı", "cari takip programı", "tahsilat takip programı", "komisyon yönetimi", "çoklu kullanıcı cari takip", "bayi cari takip programı", "komisyonlu tahsilat takip programı" },
                false,
                "NSX Komisyonlu Cari Takip Pro Ürün Sayfası",
                "/urun/nsx-komisyonlu-cari-takip-pro",
                "nsx-komisyonlu-cari-takip-pro komisyonlu cari takip pro server client cari takip komisyon tahsilat"),
            new(
                "veresiye-programi-nedir",
                "Veresiye Programı Nedir? | Bulut ve Mobil Cari Takip Rehberi",
                "Veresiye programı nedir? Bulut ve mobil cari takip, QR bağlantısı, canlı senkronizasyon, borç, tahsilat ve açık bakiye yönetimini keşfedin.",
                "Veresiye Programı Nedir?",
                "Veresiye programı; müşterilere ait borç, tahsilat, cari hareket ve kalan bakiye bilgilerinin düzenli şekilde takip edilmesini sağlayan dijital cari takip yazılımıdır. Kağıt defter veya dağınık Excel dosyaları yerine her müşteri için ayrı cari hesap tutularak geçmiş hareketlere daha hızlı ulaşılır.\n\nNSX Veresiye Takip Pro v1.0.5, masaüstü kullanımına Bulut ve Mobil Yönetim Sistemini ekler. Programdaki Cloud Bağlantısı ekranından güvenli QR kod oluşturulur ve cep telefonu ile bağlantı kurulur. Mobil panelde Toplam Borç, Toplam Tahsilat ve Açık Bakiye özetleri görülür; müşteri adına göre arama yapılır ve müşteriye dokunulduğunda cari hareketleri açılır.\n\nMüşterinin geçmiş borç ve tahsilat kayıtları aynı cari ekranda görüntülenir. Yeni borç veya tahsilat işlemi de yine bu ekrandan yapılır. Canlı senkronizasyon sayesinde masaüstü ve mobil tarafın aynı cari verilerle çalışması sağlanır.\n\nBu yapı özellikle bakkal, market, bayi, küçük işletme ve müşteri bazlı veresiye çalışan esnafların iş yerinden uzaktayken de hesaplarını kontrol edebilmesini kolaylaştırır. Masaüstünün hızını mobil erişim kolaylığıyla birleştirir.",
                new[] { "veresiye programı nedir", "ücretsiz veresiye programı", "bulut veresiye programı", "mobil veresiye programı", "telefondan veresiye takibi", "cari takip programı", "borç alacak takip programı", "tahsilat takip programı", "QR ile cari takip", "canlı senkronizasyon" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Bulut veresiye programı ne sağlar?", "İşletmenin cari hesaplarını yalnızca ana bilgisayardan değil yetkilendirilmiş telefondan da görüntüleyip temel işlemleri yapabilmesini sağlar."),
                    new SeoSection("Telefondan cari hesap ve tahsilat takibi", "Müşteri aranır, cari hesabı açılır, geçmiş hareketler incelenir ve borç veya tahsilat işlemi aynı ekrandan kaydedilir."),
                    new SeoSection("QR kod ile mobil bağlantı", "Cloud Bağlantısı ekranından oluşturulan QR kod, mobil oturumu ilgili işletmenin Cloud hesabıyla eşleştirir."),
                    new SeoSection("Canlı senkronizasyon neden önemlidir?", "Masaüstü ve telefonda farklı bakiye oluşmasını önlemek için cari değişiklikler iki taraf arasında güncel tutulur.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "ucretsiz-veresiye-programi-nedir",
                "Ücretsiz Veresiye Programı Nedir? | Bulut ve Mobil NSX Veresiye",
                "Ücretsiz veresiye programı nedir? NSX Veresiye Takip Pro ile cari hesap, borç ve tahsilatları masaüstü ve telefondan nasıl yöneteceğinizi öğrenin.",
                "Ücretsiz Veresiye Programı Nedir?",
                "Ücretsiz veresiye programı; müşterilerin borç, tahsilat, ödeme geçmişi ve açık bakiye bilgilerinin ücretli bir lisans zorunluluğu olmadan dijital ortamda takip edilmesini sağlayan yazılımdır. Amaç, kağıt defterde kaybolabilen hareketleri müşteri bazlı cari hesaplarda düzenli tutmaktır.\n\nNSX Veresiye Takip Pro ücretsiz kullanım yapısını v1.0.5 ile Bulut ve Mobil Yönetim desteğiyle genişletir. Masaüstünde müşteri ve cari işlemler yürütülürken Cloud Bağlantısı üzerinden QR ile telefona bağlanılabilir. Mobil tarafta işletmenin toplam borcu, toplam tahsilatı ve açık bakiyesi görüntülenir.\n\nMüşteri arama alanında isim yazarak ilgili cari hızlıca bulunur. Müşteriye dokunulduğunda geçmiş borç ve tahsilat hareketleri açılır; yeni borç veya tahsilat aynı cari ekranından kaydedilir. Canlı senkronizasyon ile değişiklikler masaüstüne aktarılır.\n\nBöylece ücretsiz veresiye takibi yalnızca tek bilgisayarda yapılan bir kayıt işleminden çıkar; işletme sahibinin gerektiğinde cep telefonundan da günlük cari kontrol yapabildiği modern bir sisteme dönüşür.",
                new[] { "ücretsiz veresiye programı nedir", "ücretsiz veresiye programı", "bulut veresiye programı", "mobil cari takip", "müşteri borç takip programı", "telefondan borç takibi", "cari takip programı" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Ücretsiz programda Bulut ve Mobil kullanım", "Güncel NSX Veresiye sürümünde QR bağlantılı mobil yönetim bulunur. Temel cari işlemler telefon üzerinden de sürdürülebilir."),
                    new SeoSection("Mobil panelde neler var?", "Toplam Borç, Toplam Tahsilat, Açık Bakiye, müşteri arama, cari hareketler, borç girişi ve tahsilat işlemleri bulunur."),
                    new SeoSection("Kimler kullanabilir?", "Bakkal, market, bayi, küçük işletme ve müşteri bazlı veresiye çalışan esnaflar için sade bir kullanım sunar.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "ucretsiz-cari-takip-programi",
                "Ücretsiz Cari Takip Programı | Bulut ve Mobil Müşteri Carisi",
                "Ücretsiz cari takip programı ile müşteri borç, tahsilat ve açık bakiyeyi masaüstü ve mobil Bulut panelinden canlı takip edin.",
                "Ücretsiz Cari Takip Programı",
                "Ücretsiz cari takip programı, müşterilere ait borç ve tahsilat hareketlerini düzenli kaydederek güncel açık bakiyeyi görmeyi kolaylaştırır. Müşteri sayısı arttığında kağıt defter veya Excel ile yapılan takipte geçmiş hareketleri bulmak ve doğru bakiyeyi korumak zorlaşabilir.\n\nNSX Veresiye Takip Pro v1.0.5 ile cari takip masaüstünün yanında Bulut ve Mobil sisteme de taşınır. QR bağlantısı sonrası telefondaki sade ana ekranda Toplam Borç, Toplam Tahsilat ve Açık Bakiye bilgileri görülür. Müşteri araması ile ilgili cari saniyeler içinde bulunabilir.\n\nMüşteri seçildiğinde cari hareketler açılır. Borç ve tahsilatlar tarih sırasıyla görüntülenir; yeni borç veya tahsilat seçilen müşteri hesabına doğrudan eklenir. Canlı senkronizasyon, mobilde yapılan işlemi masaüstüne ve masaüstündeki değişiklikleri mobil tarafa aktarır.",
                new[] { "ücretsiz cari takip programı", "ücretsiz cari hesap programı", "bulut cari takip", "mobil cari takip", "cari takip programı", "borç alacak takip programı", "tahsilat takip programı" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Cari hareketleri tek ekranda görün", "Müşterinin borç ve tahsilat geçmişi ayrı listeler yerine aynı cari hareket ekranında izlenir."),
                    new SeoSection("Telefondan müşteri arama", "Canlı arama ile müşteri adına göre sonuçlar daralır; uzun listelerde manuel arama yapmaya gerek kalmaz."),
                    new SeoSection("Açık bakiye takibi", "Toplam borç ve toplam tahsilat hareketleri üzerinden kalan açık bakiye masaüstü ve mobilde aynı mantıkla hesaplanır.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "esnaflar-icin-veresiye-takip-programi",
                "Esnaflar İçin Veresiye Takip Programı | Bulut ve Mobil Ücretsiz Çözüm",
                "Esnaflar için ücretsiz veresiye takip programı: müşteri cari, borç, tahsilat ve açık bakiyeyi masaüstü ve cep telefonundan yönetin.",
                "Esnaflar İçin Veresiye Takip Programı",
                "Esnaf için veresiye takibinde en önemli ihtiyaç, müşterinin ne kadar borcu kaldığını ve son ödemenin ne zaman yapıldığını hızlıca görebilmektir. Kayıtlar kağıt defterde tutulduğunda müşteri sayısı arttıkça arama ve bakiye kontrolü zaman kaybettirir.\n\nNSX Veresiye Takip Pro; bakkal, market, bayi ve küçük işletmeler için müşteri carilerini sade şekilde yönetir. v1.0.5 Bulut ve Mobil Yönetim sistemi sayesinde işletme sahibi dükkanda bulunmadığında da telefonundan cari hesaplara erişebilir.\n\nMobil panelde Toplam Borç, Toplam Tahsilat ve Açık Bakiye özetleri bulunur. Müşteri aranıp seçildiğinde cari hareketleri görüntülenir; borç ve tahsilat işlemleri doğrudan aynı müşteri hesabından yapılır. Masaüstü ile mobil sistem canlı senkronize çalışır.",
                new[] { "esnaflar için veresiye takip programı", "esnaf veresiye programı", "bakkal veresiye programı", "bulut veresiye", "mobil veresiye", "ücretsiz veresiye programı", "cari takip" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Dükkan dışında da cari kontrol", "İşletme sahibi yetkilendirilmiş telefonundan müşteriyi bulup cari hareketleri inceleyebilir ve gerektiğinde borç veya tahsilat kaydı girebilir."),
                    new SeoSection("Esnaf için sade ekran", "Mobil kullanım yalnızca özet, müşteri arama ve cari işlemlere odaklanır; günlük işlem akışı gereksiz menülerle zorlaştırılmaz."),
                    new SeoSection("Masaüstü çalışma düzeni korunur", "İnternet veya mobil kullanım günlük masaüstü işleyişinin yerine geçmez; Bulut erişim mevcut programa ek bir yönetim kanalı sağlar.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "borc-alacak-takip-programi-ucretsiz",
                "Borç Alacak Takip Programı Ücretsiz | Bulut ve Mobil NSX Veresiye",
                "Ücretsiz borç alacak takip programı ile cari hesap, borç, tahsilat ve açık bakiyeyi masaüstü ve cep telefonundan Bulut sistemiyle yönetin.",
                "Borç Alacak Takip Programı Ücretsiz",
                "Borç alacak takip programı; müşterilerin işletmeye olan borçlarını, yapılan tahsilatları, geçmiş cari hareketleri ve kalan açık bakiyeyi düzenli şekilde takip etmek için kullanılır. Özellikle çok sayıda müşteriyle çalışan işletmelerde her borcun ve ödemenin doğru müşteriye kaydedilmesi, geçmiş işlemlerin kolay bulunması ve güncel bakiyenin hızlı görülmesi günlük işleyiş açısından önemlidir.\n\nNSX Veresiye Takip Pro v1.0.5, ücretsiz borç alacak ve cari takip ihtiyacını masaüstü programıyla karşılamanın yanında Bulut ve Mobil Yönetim Sistemi ile cep telefonuna da taşır. Masaüstü programındaki güvenli QR bağlantısı kullanılarak mobil panele giriş yapılabilir. Mobil panelde müşteri adına göre hızlı arama yapılır; müşteri seçildiğinde cari hareketleri açılır ve o müşterinin borç, tahsilat ve açık bakiye bilgileri tek ekranda görüntülenir.\n\nYeni borç veya tahsilat girmek için ayrı ve karmaşık ekranlar arasında dolaşmaya gerek kalmaz. İlgili müşteri cari hesabı açıldıktan sonra borç veya tahsilat işlemi doğrudan aynı hesap üzerinden yapılabilir. Böylece işletme sahibi iş yerinde olmasa bile cep telefonundan müşterinin son durumunu kontrol edebilir ve gerekli cari işlemi kaydedebilir.\n\nCanlı senkronizasyon altyapısı, masaüstü ile mobil panel arasında yapılan değişikliklerin güncel tutulmasına yardımcı olur. Ana ekrandaki toplam borç, toplam tahsilat ve açık bakiye değerleri işletmenin genel durumunu hızlıca görmeyi sağlar. Firma verileri birbirinden ayrılmış Cloud yapısında tutulur ve her işletme kendi kayıtlarıyla çalışır.\n\nNSX Veresiye Takip Pro'yu ücretsiz incelemek, masaüstü programını kullanmak ve Bulut/Mobil yönetim özelliklerinden yararlanmak için ürün sayfamızı ziyaret edebilirsiniz.",
                new[] { "borç alacak takip programı ücretsiz", "borç alacak takip programı", "ücretsiz veresiye programı", "bulut cari takip programı", "mobil cari takip", "telefondan borç alacak takibi", "tahsilat takip programı", "müşteri borç takip programı", "açık bakiye takibi", "QR mobil bağlantı" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil",
                new[]
                {
                    new SeoSection("Borç alacak takibi nasıl yapılır?", "Her müşteri için ayrı cari hesap tutulur; borç ve tahsilat hareketleri tarih sırasıyla kaydedilir. Güncel açık bakiye, toplam borçtan toplam tahsilatın düşülmesiyle takip edilir. Dijital cari takip sistemi, geçmiş işlemleri aramayı ve müşterinin gerçek durumunu görmeyi kolaylaştırır."),
                    new SeoSection("Mobil borç ve tahsilat yönetimi", "NSX Veresiye Takip Pro'nun mobil panelinde müşteri aranır ve seçilir. Cari hareketler açıldıktan sonra geçmiş borç ve tahsilatlar görüntülenir; yeni işlem doğrudan müşterinin cari hesabına eklenir. Bu sade akış, telefonda hızlı kullanım için tasarlanmıştır."),
                    new SeoSection("Toplam borç, toplam tahsilat ve açık bakiye", "Mobil ana ekranda işletmenin toplam borç, toplam tahsilat ve açık bakiye değerleri özetlenir. Böylece tek tek müşteri kartlarını açmadan önce işletmenin genel cari durumu hızlı şekilde kontrol edilebilir."),
                    new SeoSection("Masaüstü ve Bulut birlikte nasıl çalışır?", "Masaüstünde yapılan cari işlemler Cloud sistemine, mobil tarafta yapılan işlemler de masaüstüne senkronize edilir. Bu yapı, işletmenin bilgisayardan çalışmaya devam ederken gerektiğinde cep telefonundan da aynı cari sisteme erişebilmesini sağlar."),
                    new SeoSection("Kağıt defter ve Excel yerine dijital takip", "Defter veya dağınık Excel dosyalarında ödeme geçmişini ve doğru bakiyeyi bulmak zorlaşabilir. Müşteri bazlı cari hareket ekranı, borç ve tahsilat kayıtlarını aynı yerde tutarak daha düzenli ve denetlenebilir bir işleyiş oluşturur.")
                },
                new[]
                {
                    new SeoFaq("Borç alacak takip programı ücretsiz kullanılabilir mi?", "NSX Veresiye Takip Pro ürün sayfasından ücretsiz kullanım seçeneğiyle edinilebilir ve temel cari, borç ve tahsilat işlemleri yönetilebilir."),
                    new SeoFaq("Borç ve tahsilatları cep telefonundan girebilir miyim?", "Evet. QR ile mobil sisteme bağlandıktan sonra müşteri cari hesabını açarak borç veya tahsilat işlemi girebilirsiniz."),
                    new SeoFaq("Müşterinin eski cari hareketleri mobilde görünür mü?", "Evet. Senkronize edilen müşteri hesabında geçmiş borç ve tahsilat hareketleri cari hareketler bölümünde görüntülenir."),
                    new SeoFaq("Açık bakiye nasıl hesaplanır?", "Açık bakiye, müşterinin veya işletmenin toplam borç tutarından toplam tahsilat tutarının düşülmesiyle oluşan kalan cari tutarı ifade eder."),
                    new SeoFaq("Farklı işletmelerin verileri birbirine karışır mı?", "Cloud sistemi firma/tenant ayrımıyla çalışacak şekilde tasarlanmıştır. Her işletme kendi bağlantısı ve kendi kayıtlarıyla işlem yapar.")
                }),
            new(
                "bakkal-veresiye-defteri-nasil-tutulur",
                "Bakkal Veresiye Defteri Nasıl Tutulur? | Bulut ve Mobil Takip",
                "Bakkal veresiye defterini dijitalleştirin; müşteri borç, tahsilat ve açık bakiyeyi masaüstü ve cep telefonundan Bulut sistemiyle takip edin.",
                "Bakkal Veresiye Defteri Nasıl Tutulur?",
                "Bakkal veresiye defteri tutarken her müşteri için borç, tahsilat ve kalan bakiye hareketlerinin ayrı ve tarih sıralı kaydedilmesi gerekir. Kağıt defterde müşteri sayısı arttığında eski kayıtları bulmak, toplam borcu hesaplamak ve yapılan ödemeleri kontrol etmek zorlaşabilir.\n\nNSX Veresiye Takip Pro, bakkal ve marketlerde müşteri bazlı dijital cari hesap oluşturur. Müşterinin geçmiş borç ve tahsilatları tek cari hareket ekranında tutulur. Böylece hem müşterinin güncel bakiyesi hem de işletmenin toplam borç, toplam tahsilat ve açık bakiye durumu hızlıca görülebilir.\n\nv1.0.5 Bulut ve Mobil Yönetim özelliği sayesinde dükkandaki bilgisayara ek olarak cep telefonundan da müşteriler aranabilir. Müşteri seçildiğinde cari hareketleri açılır ve yeni borç veya tahsilat aynı ekrandan kaydedilir. İşlem canlı senkronizasyonla masaüstüne aktarılır.\n\nBu yaklaşım, klasik veresiye defterinin sadeliğini korurken arama, geçmiş hareket ve mobil erişim gibi dijital avantajlar kazandırır.",
                new[] { "bakkal veresiye defteri", "bakkal veresiye programı", "mobil veresiye defteri", "bulut veresiye programı", "müşteri borç takibi", "tahsilat takibi", "ücretsiz veresiye programı" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Her müşteriye ayrı cari hesap", "Borç ve tahsilat hareketleri müşteri bazında tutulur. Böylece farklı müşterilerin kayıtlarının karışması önlenir."),
                    new SeoSection("Telefonla hızlı müşteri bulma", "Mobil panelde müşteri adı yazıldıkça sonuçlar filtrelenir ve ilgili cari hesabına hızlıca geçilir."),
                    new SeoSection("Günlük tahsilatı anında kaydetme", "Dükkan dışında alınan veya teyit edilen ödeme, mobil cari ekranından tahsilat olarak girilebilir ve masaüstüyle senkronize edilir.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "cari-hesap-takibi-nasil-yapilir",
                "Cari Hesap Takibi Nasıl Yapılır? | Bulut ve Mobil Borç Tahsilat",
                "Cari hesap takibi nasıl yapılır? Müşteri borç, tahsilat, geçmiş hareket ve açık bakiyeyi masaüstü ve telefondan nasıl takip edeceğinizi öğrenin.",
                "Cari Hesap Takibi Nasıl Yapılır?",
                "Cari hesap takibi, bir müşterinin borçlarını ve yaptığı tahsilatları tarih sırasıyla kaydedip kalan açık bakiyeyi izleme işlemidir. Sağlıklı cari takipte her müşteriye ayrı kart açılır, her borç ve ödeme hareketi doğru tarihle kaydedilir ve bakiye düzenli kontrol edilir.\n\nNSX Veresiye Takip Pro bu süreci müşteri bazlı cari hareket ekranında toplar. Müşteriye ait eski borçlar ve tahsilatlar aynı yerde görüntülenir. Açık bakiye, borç ve tahsilat hareketleri üzerinden hesaplanır; böylece son durum için farklı defter veya dosyaları karşılaştırmaya gerek kalmaz.\n\nv1.0.5 sürümüyle cari takip Bulut ve Mobil sisteme de açılmıştır. QR bağlantısından sonra telefonda müşteri araması yapılır, cari hareketler görüntülenir ve yeni borç veya tahsilat doğrudan ilgili müşteriye kaydedilir. Masaüstü ve mobil canlı senkronizasyon ile güncel kalır.",
                new[] { "cari hesap takibi", "cari takip programı", "mobil cari hesap", "bulut cari takip programı", "borç alacak takip programı", "müşteri bakiye takibi", "tahsilat takibi" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Cari hesapta hangi bilgiler tutulur?", "Müşteri bilgisi, borç hareketleri, tahsilatlar, işlem tarihleri ve kalan açık bakiye temel cari bilgileridir."),
                    new SeoSection("Cariyi telefondan yönetmek", "Yetkilendirilmiş mobil bağlantı üzerinden müşteri bulunur, geçmiş cari hareketler incelenir ve yeni işlem kaydedilir."),
                    new SeoSection("Masaüstü ve mobil bakiye uyumu", "İki taraf aynı cari hareketleri kullandığı için toplam borç, tahsilat ve açık bakiye aynı hesaplama mantığıyla güncel tutulur.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "excel-yerine-veresiye-takip-programi",
                "Excel Yerine Bulut ve Mobil Veresiye Takip Programı Kullanmak",
                "Excel yerine veresiye takip programı ile müşteri cari hesaplarını, borç ve tahsilatları masaüstü ve mobil Bulut sisteminde daha düzenli yönetin.",
                "Excel Yerine Veresiye Takip Programı Kullanmanın Avantajları",
                "Excel ile veresiye takibi ilk başta pratik görünebilir; ancak müşteri sayısı arttıkça satırların karışması, yanlış formül, farklı dosya sürümleri ve eski hareketleri bulma gibi sorunlar ortaya çıkabilir. Ayrıca cari hesabı telefondan yönetmek için ayrı dosya paylaşım yöntemleri gerekir.\n\nNSX Veresiye Takip Pro, müşterileri ayrı cari kartlarda tutar ve borç ile tahsilat hareketlerini hazır bir iş akışıyla kaydeder. Kullanıcı formül yazmaz; müşteriyi seçer, cari hareketlerini görür ve işlemi ilgili hesaba ekler.\n\nv1.0.5 Bulut ve Mobil Yönetim sistemi Excel'e göre önemli bir erişim avantajı sağlar. QR ile bağlanan telefondan müşteri aranabilir, cari geçmiş görüntülenebilir, borç veya tahsilat girilebilir. Canlı senkronizasyon sonucu masaüstündeki kayıtlar da güncellenir.\n\nBu nedenle amaç yalnızca Excel tablosunu başka bir ekrana taşımak değil; cari hesabı müşteri bazlı, aranabilir ve mobil erişilebilir bir iş akışına dönüştürmektir.",
                new[] { "excel veresiye takip", "excel yerine veresiye programı", "bulut veresiye programı", "mobil veresiye programı", "cari takip programı", "ücretsiz veresiye programı", "telefondan cari takip" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Formül yerine hazır cari mantığı", "Borç ve tahsilat hareketleri program tarafından cari hesap yapısında tutulur; kullanıcı tablo formülleriyle uğraşmaz."),
                    new SeoSection("Tek müşteri, tek hareket geçmişi", "Müşteri seçildiğinde ilgili borç ve tahsilatlar aynı cari ekranında görünür."),
                    new SeoSection("Mobil erişim avantajı", "Telefon üzerinden müşteri arama ve cari işlem yapma, dosya gönderip güncel Excel kopyasını takip etme ihtiyacını azaltır.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "veresiye-defteri-programi",
                "Veresiye Defteri Programı | Bulut ve Mobil Borç Tahsilat Takibi",
                "Veresiye defteri programı ile müşteri borç, tahsilat ve açık bakiyeyi dijital cari hesaplarda tutun; telefondan Bulut sistemiyle yönetin.",
                "Veresiye Defteri Programı",
                "Veresiye defteri programı, klasik borç defterini dijital müşteri cari hesaplarına dönüştürür. Her müşterinin borç ve tahsilat geçmişi ayrı tutulduğu için eski hareketler daha hızlı bulunur ve açık bakiye daha kolay kontrol edilir.\n\nNSX Veresiye Takip Pro masaüstünde müşteri arama, borç girişi, tahsilat, cari hareket ve raporlama gibi işlemleri sunar. v1.0.5 ile bu yapıya Bulut ve Mobil Yönetim sistemi eklenmiştir.\n\nTelefon bağlantısı masaüstündeki Cloud Bağlantısı ekranından QR ile yapılır. Mobil panelde işletme toplamları görüntülenir; müşteri aranıp seçildiğinde cari hareketler açılır. Yeni borç ve tahsilat aynı müşteri hesabından girilir ve canlı senkronizasyon ile masaüstüne aktarılır.\n\nBöylece veresiye defteri hem iş yerindeki bilgisayarda hem de ihtiyaç halinde cep telefonunda erişilebilir hale gelir.",
                new[] { "veresiye defteri programı", "dijital veresiye defteri", "bulut veresiye defteri", "mobil veresiye takibi", "borç alacak takip programı", "tahsilat takip programı", "ücretsiz veresiye programı" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Kağıt defterden dijital cariye geçiş", "Müşterinin tüm borç ve tahsilat hareketleri tek hesapta toplanır; arama ve geçmiş kontrolü hızlanır."),
                    new SeoSection("Mobil veresiye defteri", "QR ile yetkilendirilen telefon, sade mobil panel üzerinden müşteri cari hesaplarına ulaşır."),
                    new SeoSection("Anlık işletme özeti", "Toplam Borç, Toplam Tahsilat ve Açık Bakiye mobil ana ekranda özet olarak gösterilir.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "ucretsiz-cari-hesap-programi",
                "Ücretsiz Cari Hesap Programı | Bulut, Mobil ve Tahsilat Takibi",
                "Ücretsiz cari hesap programı ile borç, tahsilat, cari hareket ve açık bakiyeleri masaüstü ile cep telefonundan canlı takip edin.",
                "Ücretsiz Cari Hesap Programı",
                "Ücretsiz cari hesap programı; müşteri bazında borç, tahsilat ve kalan bakiye takibi yapmak isteyen işletmeler için pratik bir çözümdür. Cari hareketlerin tek müşteri hesabında tutulması, ödeme geçmişini ve güncel durumu kontrol etmeyi kolaylaştırır.\n\nNSX Veresiye Takip Pro v1.0.5 ile masaüstü cari takibine Bulut ve Mobil Yönetim eklenmiştir. İşletmenin Toplam Borç, Toplam Tahsilat ve Açık Bakiye değerleri mobil ana ekranda görüntülenir.\n\nMüşteri arama alanına isim yazılarak ilgili hesap bulunur. Müşteriye dokunulduğunda cari hareket geçmişi açılır ve borç veya tahsilat işlemi aynı ekrandan yapılabilir. Canlı senkronizasyon, masaüstü ve mobil kayıtların güncel kalmasını sağlar.",
                new[] { "ücretsiz cari hesap programı", "ücretsiz cari takip programı", "bulut cari hesap", "mobil cari hesap", "veresiye takip programı", "tahsilat takip programı", "açık bakiye" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                new[]
                {
                    new SeoSection("Ücretsiz cari hesapta mobil erişim", "Güncel sürümde müşteri carileri yetkilendirilmiş telefondan görüntülenebilir ve temel cari işlemler yapılabilir."),
                    new SeoSection("Borç ve tahsilat aynı cari ekranda", "Müşterinin geçmiş hareketleri görüntülenirken yeni işlem de aynı hesap üzerinden kaydedilir."),
                    new SeoSection("Canlı toplamlar", "Toplam borç, toplam tahsilat ve açık bakiye işletmenin genel durumunu hızlıca gösterir.")
                },
                new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro ücretsiz mi?", "Evet. NSX Veresiye Takip Pro ücretsiz kullanım için sunulur; masaüstü cari takibin yanında güncel v1.0.5 sürümünde Bulut ve Mobil Yönetim özellikleri de bulunur."),
                    new SeoFaq("Cep telefonundan cari hesapları görebilir miyim?", "Evet. Masaüstündeki Cloud Bağlantısı ekranından oluşturulan QR kod ile mobil panele bağlanabilir, müşteri arayabilir ve müşterinin cari hareketlerini görüntüleyebilirsiniz."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Müşteriyi seçtikten sonra aynı cari ekran üzerinden yeni borç veya tahsilat işlemi kaydedilebilir."),
                    new SeoFaq("Masaüstü ile mobil veriler aynı kalır mı?", "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.")
                }),
            new(
                "teknik-servis-takip-programi",
                "Teknik Servis Takip Programı | Cihaz, Müşteri ve Servis Yönetimi",
                "Teknik servis takip programı ile cihaz kabul, müşteri kaydı, servis durumu ve teslimat süreçlerini profesyonel yönetin.",
                "Teknik Servis Takip Programı",
                "Teknik servis takip programı; cihaz kabulü, arıza kaydı, müşteri bilgisi, servis aşaması, teslimat ve ödeme takibi gibi süreçleri tek merkezde toplar. Bilgisayar, telefon, elektronik ve benzeri servis işletmeleri için düzenli bir iş akışı sağlar.",
                new[] { "teknik servis takip programı", "servis takip yazılımı", "cihaz takip programı" },
                false,
                "Teknik Servis Programını İncele",
                "/urun/nsx-teknik-servis-pro-2",
                "teknik servis"),
            new(
                "bilgisayar-hizlandirma-programi",
                "Bilgisayar Hızlandırma Programı | Windows Performans Optimizasyonu",
                "Bilgisayar hızlandırma programı ile gereksiz yükleri azaltın, sistem performansını iyileştirin ve Windows kullanımını daha akıcı hale getirin.",
                "Bilgisayar Hızlandırma Programı",
                "Bilgisayar hızlandırma programı; başlangıç yüklerini, gereksiz dosya birikimini, sistem yavaşlığına neden olan ayarları ve performans problemlerini azaltmaya yardımcı olur. NSX Turbo gibi çözümler, kullanıcıya daha sade ve hızlı bir bakım deneyimi sunmak için tasarlanır.",
                new[] { "bilgisayar hızlandırma programı", "pc hızlandırma", "windows hızlandırma" },
                false,
                "NSX Turbo Ürünlerini İncele",
                "/store?q=turbo",
                "turbo"),
            new(
                "nsx-servispro-live-teknik-servis-programi",
                "NSX ServisPro Live | QR Kodlu Teknik Servis Takip Programı",
                "NSX ServisPro Live ile servis kayıtlarını, SMS bildirimlerini, QR canlı takibi, müşteri onaylarını ve cari işlemleri tek merkezden yönetin.",
                "NSX ServisPro Live: QR Kodlu Teknik Servis Takip Programı",
                "Teknik servis işletmelerinde müşteri beklentisi artık yalnızca hızlı onarım değildir. Müşteri, cihazının hangi aşamada olduğunu bilmek, fiyat onayı vermek ve teslim sürecini kolayca takip etmek ister. NSX ServisPro Live; servis kabulünden teslimata kadar tüm süreci kayıt altına alan, QR kodlu canlı takip ve SMS bilgilendirme özellikleriyle servis iletişimini güçlendiren profesyonel bir teknik servis programıdır.\n\nProgram; müşteri ve cihaz kayıtları, arıza açıklamaları, servis durumları, fiyat onayı, ödeme hareketleri, servis geçmişi, raporlama ve kullanıcı yetkilendirme gibi günlük operasyonları tek merkezde toplar. Böylece telefon trafiği azalır, personel aynı kayıt üzerinden çalışır ve müşteriye daha şeffaf bir hizmet sunulur.",
                new[] { "NSX ServisPro Live", "QR kodlu teknik servis programı", "servis takip programı", "SMS bildirimli teknik servis", "müşteri canlı takip", "teknik servis otomasyonu", "cihaz servis takip" },
                false,
                "NSX ServisPro Live'ı İncele",
                "/urun/nsx-servispro-live",
                "nsx servispro live",
                Sections: new[]
                {
                    new SeoSection("Teknik servislerde dijital takip neden önemlidir?", "Kağıt formlar ve dağınık mesajlaşmalar, cihazın durumunun unutulmasına ve müşteriye farklı bilgiler verilmesine neden olabilir. Dijital servis kaydı; cihaz kabulü, arıza, yapılan işlem, fiyat, ödeme ve teslim bilgilerini aynı kayıt altında tutar."),
                    new SeoSection("QR kod ile canlı servis takibi", "Servis kaydı için oluşturulan QR veya güvenli bağlantı sayesinde müşteri cihazının güncel durumunu görüntüleyebilir. Uygun süreçlerde fiyat onayı verebilir, reddedebilir veya servise bilgi iletebilir. Bu yapı servis masasındaki tekrar eden telefon görüşmelerini azaltır."),
                    new SeoSection("SMS bilgilendirme ve müşteri iletişimi", "Onay bekleyen işlem, servis durumu veya teslime hazır bilgisi SMS ile müşteriye iletilebilir. Hazır şablonlar iletişim standardını yükseltir ve personelin aynı mesajı tekrar tekrar yazmasını önler."),
                    new SeoSection("Cari hesap, ödeme ve raporlama", "Servis ücretleri, tahsilatlar ve kalan bakiyeler müşteri kaydıyla birlikte izlenebilir. Günlük, aylık ve dönemsel raporlar işletmenin servis hacmini ve finansal durumunu daha anlaşılır hale getirir."),
                    new SeoSection("Kimler kullanabilir?", "Bilgisayar, telefon, tablet, elektronik cihaz, beyaz eşya, küçük ev aletleri ve benzeri ürünlere servis veren işletmeler için uygundur. Çok sayıda cihaz kabul eden ve müşteriye düzenli bilgi vermek isteyen servisler için özellikle faydalıdır."),
                    new SeoSection("NSX ServisPro Live'ın işletmeye katkısı", "Düzenli kayıt, şeffaf müşteri iletişimi ve merkezi takip sayesinde servis süreçleri daha kontrollü ilerler. Personel iş yükü azalırken müşteri memnuniyeti ve kurumsal görünüm güçlenir.")
                },
                Faqs: new[]
                {
                    new SeoFaq("NSX ServisPro Live ne işe yarar?", "Teknik servis kayıtlarını, cihaz durumlarını, müşteri iletişimini, QR canlı takibi, SMS bildirimlerini ve ödeme süreçlerini tek merkezden yönetmeye yardımcı olur."),
                    new SeoFaq("Müşteri cihazını telefondan takip edebilir mi?", "Evet. Oluşturulan güvenli QR veya takip bağlantısı üzerinden servis durumunu mobil tarayıcıdan görüntüleyebilir."),
                    new SeoFaq("SMS bildirim özelliği var mı?", "Evet. Servis durumu, onay ve teslimat gibi bilgilendirmeler için SMS yönetimi kullanılabilir."),
                    new SeoFaq("Hangi teknik servisler için uygundur?", "Bilgisayar, telefon, tablet, elektronik, beyaz eşya ve benzeri cihazlara servis veren işletmeler için uygundur.")
                }),
            new(
                "dugun-salonu-yonetim-programi",
                "Düğün Salonu Programı | Rezervasyon, Sözleşme ve Cari Takip",
                "Düğün salonu programı ile rezervasyon, müşteri, sözleşme, tahsilat, cari hesap, kasa ve organizasyon süreçlerini profesyonel yönetin.",
                "Düğün Salonu Yönetim Programı: Rezervasyondan Tahsilata Tek Sistem",
                "Düğün salonu işletmelerinde aynı tarih için birden fazla görüşme, farklı salonlar, kapora ödemeleri, sözleşmeler ve organizasyon detayları birlikte yönetilir. NSX Düğün Salonu Pro; rezervasyon, müşteri, sözleşme, cari hesap, tahsilat, kasa, SMS ve organizasyon süreçlerini tek merkezde toplayarak kayıt karmaşasını azaltmak için geliştirilmiştir.\n\nDüğün, nişan, kına, sünnet, toplantı ve özel davetler tarih ve salon bilgileriyle kaydedilebilir. Müşteriyle yapılan anlaşma, alınan kapora, kalan borç, ek hizmetler ve sözleşme bilgileri aynı müşteri kartından izlenebilir. QR destekli web paneli sayesinde yetkili firma sahibi ofis dışında da müşteri ve rezervasyon işlemlerini sürdürebilir.",
                new[] { "düğün salonu programı", "düğün salonu rezervasyon programı", "organizasyon takip programı", "salon yönetim yazılımı", "sözleşme takip", "tahsilat takip", "NSX Düğün Salonu Pro" },
                false,
                "NSX Düğün Salonu Pro'yu İncele",
                "/urun/nsx-dugun-salonu-pro",
                "nsx düğün salonu pro",
                Sections: new[]
                {
                    new SeoSection("Rezervasyon çakışmalarını önleme", "Takvim ve salon bazlı rezervasyon yönetimi, aynı tarih ve salonda birden fazla organizasyon oluşturma riskini azaltır. Yaklaşan etkinlikler tek ekranda görüntülenebilir."),
                    new SeoSection("Müşteri ve organizasyon detayları", "Müşteri iletişim bilgileri, davet türü, kişi sayısı, salon, menü, ek hizmetler ve özel notlar rezervasyonla ilişkilendirilir. Böylece görüşme sırasında verilen sözler kaybolmaz."),
                    new SeoSection("Sözleşme ve belge yönetimi", "Rezervasyon ve güncel cari bilgiler kullanılarak profesyonel sözleşmeler hazırlanabilir. Sözleşme geçmişi müşteri kartında saklanarak gerektiğinde hızlıca bulunabilir."),
                    new SeoSection("Kapora, tahsilat ve cari hesap", "Alınan kapora, sonraki tahsilatlar, ek borçlar ve kalan bakiye müşteri bazında izlenir. Kasa hareketleri işletmenin günlük finansal durumunu kontrol etmeyi kolaylaştırır."),
                    new SeoSection("QR kodlu web paneli ile uzaktan kullanım", "Firma sahibi mobil tarayıcı üzerinden güvenli QR oturumuyla müşteri oluşturabilir, rezervasyon ekleyebilir ve mevcut kayıtları inceleyebilir. Masaüstü program yeniden bağlandığında değişiklikler senkronize edilir."),
                    new SeoSection("SMS Bilgi Sistemi", "Rezervasyon, ödeme ve organizasyon süreçleriyle ilgili bilgilendirmeler hazır şablonlarla gönderilebilir. Bu özellik müşteriye zamanında ve kurumsal iletişim sağlar."),
                    new SeoSection("Hangi işletmeler için uygundur?", "Düğün salonları, kır düğünü mekanları, davet alanları, organizasyon firmaları, toplantı salonları ve rezervasyonla çalışan etkinlik işletmeleri için uygundur.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Düğün salonu programı neyi takip eder?", "Rezervasyonları, müşterileri, organizasyon detaylarını, sözleşmeleri, kapora ve tahsilatları, cari bakiyeleri ve kasa hareketlerini takip eder."),
                    new SeoFaq("Aynı gün birden fazla salon yönetilebilir mi?", "Salon ve tarih bazlı kayıt yapısı sayesinde işletmedeki farklı salonların rezervasyonları ayrı ayrı planlanabilir."),
                    new SeoFaq("Ofis dışında rezervasyon girilebilir mi?", "QR kodlu güvenli web paneli üzerinden yetkili firma sahibi mobil cihazdan müşteri ve rezervasyon işlemleri yapabilir."),
                    new SeoFaq("Müşteri sözleşmesi hazırlanabilir mi?", "Evet. Rezervasyon ve cari bilgiler kullanılarak sözleşme oluşturulabilir ve müşteri kaydıyla ilişkilendirilebilir.")
                }),
            new(
                "oto-galeri-yonetim-programi",
                "Oto Galeri Programı | Araç Stok, Satış ve Cari Takip",
                "Oto galeri programı ile araç stoklarını, alım satım işlemlerini, müşteri carilerini, kaporaları, giderleri ve kâr zarar raporlarını yönetin.",
                "Oto Galeri Programı ile Araç Stok ve Satış Süreçlerini Yönetin",
                "Oto galeri işletmelerinde araç maliyeti, satış fiyatı, kapora, müşteri borcu, noter evrakı ve araç giderleri doğru takip edilmediğinde gerçek kârlılığı görmek zorlaşır. NSX Oto Galeri Pro; araç stoklarını, müşteri kayıtlarını, alım satım işlemlerini, cari hesapları ve raporları tek merkezde toplar.\n\nHer araç için marka, model, plaka, kilometre, alış fiyatı, satış fiyatı ve durum bilgileri kaydedilebilir. Ruhsat, ekspertiz, noter belgesi ve araç fotoğrafları ilgili araç kartına eklenebilir. Satış tamamlandığında müşteri, ödeme ve sözleşme bilgileri aynı süreçte yönetilir.",
                new[] { "oto galeri programı", "araç stok takip programı", "araç alım satım programı", "galeri cari takip", "oto galeri sözleşme", "araç kâr zarar raporu", "NSX Oto Galeri Pro" },
                false,
                "NSX Oto Galeri Pro'yu İncele",
                "/urun/nsx-oto-galeri-pro",
                "nsx oto galeri pro",
                Sections: new[]
                {
                    new SeoSection("Araç stok yönetimi", "Satıştaki, rezerve edilen ve satılan araçlar durumlarına göre takip edilir. Araç bilgileri ve görselleri tek kartta toplanır."),
                    new SeoSection("Alış, satış ve maliyet hesabı", "Araç alış bedeli, satış bedeli ve araca yapılan ek giderler kaydedilerek gerçek maliyet ve kâr zarar analizi yapılabilir."),
                    new SeoSection("Kapora ve rezervasyon takibi", "Bir araç için alınan kapora ve rezervasyon bilgisi kayıt altına alınır. Böylece aynı aracın farklı müşterilere yanlışlıkla satılması riski azaltılır."),
                    new SeoSection("Müşteri cari hesapları", "Müşteri borçları, tahsilatlar ve kalan bakiyeler satış işlemiyle ilişkilendirilir. Ödeme geçmişi müşteri kartından incelenebilir."),
                    new SeoSection("Sözleşme ve evrak arşivi", "Satış sözleşmesi, ruhsat, ekspertiz, noter evrakı ve diğer belgeler araç veya müşteri kaydına eklenerek düzenli arşiv oluşturulur."),
                    new SeoSection("Raporlama ve işletme kontrolü", "Stok değeri, satışlar, giderler, tahsilatlar ve kârlılık raporları galeri yönetiminin daha sağlıklı karar vermesine yardımcı olur.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Oto galeri programı hangi kayıtları tutar?", "Araç stokları, alış ve satış işlemleri, müşteri kayıtları, kaporalar, cari hareketler, giderler, belgeler ve raporlar tutulabilir."),
                    new SeoFaq("Araç giderleri kâra dahil edilir mi?", "Araca ait giderler kaydedilerek alış maliyeti ve satış sonucu daha gerçekçi biçimde analiz edilebilir."),
                    new SeoFaq("Araç belgeleri saklanabilir mi?", "Ruhsat, ekspertiz, noter evrakı, fotoğraf ve diğer dosyalar ilgili araç kaydında arşivlenebilir."),
                    new SeoFaq("Kapora alınan araç rezerve gösterilebilir mi?", "Evet. Kapora ve rezervasyon bilgisi araç durumuyla birlikte takip edilebilir.")
                }),
            new(
                "oto-tamir-servis-yonetim-programi",
                "Oto Tamir Servis Programı | Araç Kabul ve Servis Takibi",
                "Oto tamir servis programı ile araç kabul, servis fişi, müşteri, işlem geçmişi, tahsilat, gider ve servis raporlarını profesyonel yönetin.",
                "Oto Tamir Servis Programı ile Araç Kabulden Teslimata Tam Takip",
                "Oto tamir ve özel servis işletmelerinde araç kabulü, yapılacak işlemler, değişen parçalar, müşteri onayı, ödeme ve teslim bilgileri düzenli takip edilmelidir. NSX Oto Tamir Servis Pro, araç servise girdiği andan teslim edilene kadar tüm süreci tek kayıt üzerinden yönetmeye yardımcı olur.\n\nMüşteri ve araç bilgileri, servis şikayeti, yapılan işçilikler, kullanılan parçalar, servis fişleri, tahsilatlar ve geçmiş işlemler aynı sistemde saklanır. Çoklu kullanıcı ve işlem logları sayesinde işletme içindeki görev dağılımı daha kontrollü hale gelir.",
                new[] { "oto tamir servis programı", "oto servis takip programı", "araç kabul programı", "servis fişi programı", "araç bakım takip", "oto servis cari takip", "NSX Oto Tamir Servis Pro" },
                false,
                "NSX Oto Tamir Servis Pro'yu İncele",
                "/urun/nsx-oto-tamir-servis-pro",
                "nsx oto tamir servis pro",
                Sections: new[]
                {
                    new SeoSection("Araç kabul sürecini standartlaştırma", "Müşteri, plaka, araç, kilometre, şikayet ve teslim alınan ekipmanlar kayıt altına alınır. Böylece kabul sırasında eksik bilgi bırakma riski azalır."),
                    new SeoSection("Servis fişi ve işlem geçmişi", "Yapılan işçilikler, kullanılan parçalar, açıklamalar ve ücretler servis fişine eklenir. Araç tekrar geldiğinde geçmiş işlemler hızlıca görüntülenebilir."),
                    new SeoSection("Müşteri ve araç bazlı takip", "Bir müşteriye ait birden fazla araç ayrı kartlarla yönetilebilir. Her aracın servis geçmişi, belge ve fotoğrafları kendi kaydı altında saklanır."),
                    new SeoSection("Cari hesap ve tahsilat", "Servis ücreti, alınan ödeme ve kalan bakiye müşterinin cari hesabına işlenir. Günlük ve dönemsel tahsilatlar raporlanabilir."),
                    new SeoSection("Çoklu kullanıcı ve işlem güvenliği", "Personeller ayrı kullanıcı hesaplarıyla çalışabilir. İşlem kayıtları sayesinde hangi kullanıcının ne zaman değişiklik yaptığı izlenebilir."),
                    new SeoSection("Raporlar ve yedekleme", "Günlük, haftalık, aylık ve yıllık servis raporları alınabilir. Düzenli yedekleme işletme verilerinin korunmasına yardımcı olur.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Oto tamir servis programı ne işe yarar?", "Araç kabul, servis işlemleri, müşteri ve araç geçmişi, servis fişi, tahsilat ve raporlama süreçlerini düzenli yönetir."),
                    new SeoFaq("Araç geçmişi görüntülenebilir mi?", "Evet. Araç kartından önceki servis kayıtları, yapılan işlemler ve eklenen belgeler incelenebilir."),
                    new SeoFaq("Birden fazla personel kullanabilir mi?", "Çoklu kullanıcı desteğiyle personeller ayrı hesaplardan çalışabilir ve işlem hareketleri takip edilebilir."),
                    new SeoFaq("Servis fişi yazdırılabilir mi?", "Servis kayıtları uygun çıktı biçimleriyle PDF olarak kaydedilebilir veya yazdırılabilir.")
                }),
            new(
                "klinik-randevu-takip-programi",
                "Klinik Randevu Programı | Hasta, Doktor ve Tahsilat Takibi",
                "Klinik randevu programı ile hasta kayıtlarını, doktorları, randevuları, işlem geçmişini, cari hesapları ve tahsilatları yönetin.",
                "Klinik ve Randevu Takip Programı ile Hasta Süreçlerini Düzenleyin",
                "Klinik ve randevulu çalışan işletmelerde hasta kaydı, doktor planı, işlem notları, ödeme ve sonraki randevu bilgileri aynı anda yönetilir. NSX Klinik ve Randevu Takip Programı; hasta, doktor, randevu, cari hesap ve tahsilat süreçlerini tek merkezde toplayarak günlük iş akışını düzenler.\n\nHasta kartında iletişim bilgileri, randevu geçmişi, yapılan işlemler, notlar ve ödeme hareketleri görüntülenebilir. Günlük, haftalık ve aylık planlama sayesinde boş saatler ve yoğunluk daha kolay kontrol edilir.\n\nNSX Klinik'in öne çıkan özelliklerinden biri de Hasta Monitörü ve kiosk destekli sıra takip yapısıdır. Danışmada veya bekleme alanında kullanılan kiosk ekranı üzerinden hasta kabul ve sıra yönetimi yapılabilir; muayene odası kapısı üzerine yerleştirilen ekranda sıradaki hasta bilgisi görsel olarak gösterilirken, sesli çağrı desteğiyle hasta adı veya sıra numarası anons edilebilir. Böylece klinik içindeki yönlendirme daha düzenli, daha anlaşılır ve daha profesyonel hale gelir.",
                new[] { "klinik programı", "klinik randevu programı", "hasta takip programı", "doktor randevu sistemi", "muayenehane programı", "güzellik merkezi randevu programı", "NSX Klinik" },
                false,
                "Klinik Programını İncele",
                "/store?q=klinik",
                "nsx klinik randevu",
                Sections: new[]
                {
                    new SeoSection("Hasta Monitörü, kiosk ve kapı üstü sıra takip sistemi", "NSX Klinik; bekleme alanında kullanılabilen kiosk ekranı, muayene odası kapısı üzerine yerleştirilebilen Hasta Monitörü ve sesli-görsel çağrı desteğiyle klinik içi sıra yönetimini profesyonel hale getirir. Sıradaki hasta veya sıra numarası ekranda gösterilebilir, aynı anda sesli anonsla çağrı yapılabilir. Bu yapı özellikle yoğun kliniklerde danışma yükünü azaltır, hastaların doğru bölüme yönlendirilmesini kolaylaştırır ve bekleme sürecini daha düzenli hale getirir."),
                    new SeoSection("Hasta kartı ve işlem geçmişi", "Hasta iletişim bilgileri, önceki randevular, yapılan işlemler, notlar ve ödeme hareketleri aynı kart üzerinden incelenebilir."),
                    new SeoSection("Doktor ve personel planlaması", "Randevular doktor veya personel bazında planlanarak çalışma takvimi daha düzenli hale getirilir. Yoğun saatler ve müsaitlik daha kolay görülür."),
                    new SeoSection("Günlük, haftalık ve aylık randevu görünümü", "Farklı takvim görünümleri yaklaşan randevuları takip etmeyi, iptal ve değişiklikleri yönetmeyi kolaylaştırır."),
                    new SeoSection("Cari hesap ve tahsilat", "Hastaya ait ücret, ödeme ve kalan bakiye kayıt altına alınır. Tahsilat geçmişi ve dönemsel gelirler raporlanabilir."),
                    new SeoSection("Kimler için uygundur?", "Klinikler, muayenehaneler, diyetisyenler, psikologlar, danışmanlık merkezleri, güzellik merkezleri ve randevuyla çalışan benzer işletmeler kullanabilir."),
                    new SeoSection("Düzenli kayıt ve hizmet kalitesi", "Randevu ve hasta bilgilerinin tek sistemde tutulması unutulan randevuları, dağınık notları ve ödeme karışıklıklarını azaltmaya yardımcı olur.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Hasta Monitörü ve kiosk sistemi ne işe yarar?", "Kiosk ekranı hasta kabul ve sıra yönetimini kolaylaştırır. Kapı üstü Hasta Monitörü sıradaki hastayı görsel olarak gösterir; sesli çağrı desteği ise hasta adı veya sıra numarasının anons edilmesini sağlar."),
                    new SeoFaq("Klinik randevu programı hangi işletmeler için uygundur?", "Klinik, muayenehane, diyetisyen, psikolog, güzellik merkezi ve randevuyla çalışan hizmet işletmeleri için uygundur."),
                    new SeoFaq("Hasta geçmişi tutulabilir mi?", "Hasta kartında randevu geçmişi, yapılan işlemler, notlar ve ödeme hareketleri saklanabilir."),
                    new SeoFaq("Doktor bazlı randevu planlanabilir mi?", "Evet. Randevular doktor veya personel seçilerek tarih ve saat bazında planlanabilir."),
                    new SeoFaq("Tahsilat takibi yapılabilir mi?", "Hasta bazlı borç, ödeme ve kalan bakiye hareketleri takip edilebilir.")
                }),
            new(
                "veresiye-cari-takip-programi-pro",
                "NSX Veresiye Takip Pro v1.0.5 | Bulut ve Mobil Cari Takip",
                "NSX Veresiye Takip Pro v1.0.5 ile müşteri cari hesaplarını, borç ve tahsilatları masaüstü ve cep telefonundan Bulut sistemiyle canlı yönetin.",
                "NSX Veresiye Takip Pro v1.0.5 ile Cari Hesapları Her Yerden Yönetin",
                "NSX Veresiye Takip Pro v1.0.5; müşteri cari hesaplarını, borçları, tahsilatları, açık bakiyeleri ve işlem geçmişini sade bir masaüstü uygulamasında yönetirken yeni Bulut ve Mobil Yönetim Sistemi ile günlük cari işlemleri cep telefonuna da taşır.\n\nMasaüstündeki Cloud Bağlantısı ekranından güvenli QR kod oluşturularak telefon işletmenin Cloud hesabına bağlanır. Mobil ana ekranda Toplam Borç, Toplam Tahsilat ve Açık Bakiye değerleri görüntülenir. Müşteri arama alanından ilgili cari hızlıca bulunur ve müşteriye dokunulduğunda geçmiş borç ile tahsilat hareketleri açılır.\n\nYeni borç ve tahsilat işlemleri doğrudan seçilen müşterinin cari ekranından yapılır. Canlı senkronizasyon sayesinde mobilde yapılan işlem masaüstüne, masaüstündeki cari değişiklikler de mobil sisteme aktarılır. Böylece işletme sahibi iş yerinde değilken de müşterinin güncel durumunu kontrol edebilir.\n\nGüncelleme mevcut müşteri ve cari kayıtları koruyacak şekilde hazırlanmıştır. NSX Veresiye Takip Pro, masaüstü çalışma hızını Bulut ve Mobil erişim kolaylığıyla birleştirir.",
                new[] { "NSX Veresiye Takip Pro", "veresiye takip programı", "bulut veresiye programı", "mobil cari takip", "cari takip programı", "müşteri borç takip programı", "tahsilat takip programı", "QR ile cari takip", "canlı senkronizasyon" },
                false,
                "NSX Veresiye Takip Pro'yu Ücretsiz İncele",
                "/urun/nsx-veresiye-takip-pro-free",
                "nsx-veresiye-takip-pro-free NSX Veresiye Takip Programı Ücretsiz Bulut Mobil v1.0.5",
                Sections: new[]
                {
                    new SeoSection("Bulut ve Mobil Yönetim", "QR ile yetkilendirilen telefondan işletme özetleri, müşteri arama ve müşteri cari hareketleri görüntülenebilir; borç ve tahsilat işlemleri yapılabilir."),
                    new SeoSection("Cari hareketler tek müşteri ekranında", "Müşterinin geçmiş borç ve tahsilatları aynı cari hareket listesinde tutulur. Yeni işlem de yine seçilen müşteri üzerinden kaydedilir."),
                    new SeoSection("Canlı senkronizasyon", "Masaüstü ve mobil taraf aynı cari hareketlerle güncel tutulur; toplam borç, toplam tahsilat ve açık bakiye değerleri aynı hesaplama mantığını kullanır."),
                    new SeoSection("Mevcut verilerle güncelleme", "v1.0.5 güncellemesi mevcut firma, müşteri, borç ve tahsilat kayıtlarını koruyarak Bulut ve Mobil özelliklerini programa ekler.")
                },
                Faqs: new[]
                {
                    new SeoFaq("NSX Veresiye Takip Pro v1.0.5'te Bulut sistemi var mı?", "Evet. Cloud Bağlantısı ekranından QR ile mobil panele bağlanılabilir ve temel cari işlemler telefondan yönetilebilir."),
                    new SeoFaq("Müşterinin cari hareketleri mobilde görünür mü?", "Evet. Müşteri seçildiğinde geçmiş borç ve tahsilat hareketleri aynı cari ekranında görüntülenir."),
                    new SeoFaq("Telefondan borç ve tahsilat girebilir miyim?", "Evet. Yeni borç veya tahsilat doğrudan seçili müşterinin cari hesabına kaydedilebilir."),
                    new SeoFaq("Güncelleme eski kayıtları siler mi?", "Güncelleme mevcut firma, müşteri ve cari kayıtları koruyacak şekilde hazırlanmıştır.")
                }),
            new(
                "nsx-barkodlu-satis-cari-stok-takip-pro",
                "NSX Barkodlu Satış, Cari ve Stok Takip Pro | Hızlı Satış Programı",
                "NSX Barkodlu Satış, Cari ve Stok Takip Pro ile barkodlu satış, stok, müşteri carisi, kasa, tedarikçi ve raporlama işlemlerini tek merkezden yönetin.",
                "NSX Barkodlu Satış, Cari ve Stok Takip Pro ile Satıştan Stoğa Tam Kontrol",
                "Perakende satış yapan işletmelerde ürün, barkod, stok, müşteri carisi, tedarikçi, kasa ve satış kayıtlarının ayrı ayrı tutulması zaman kaybına ve hesap hatalarına yol açabilir. NSX Barkodlu Satış, Cari ve Stok Takip Pro; hızlı satış ekranından stok hareketlerine, cari hesaplardan tahsilatlara ve raporlara kadar günlük işletme süreçlerini tek programda birleştirir.\n\nBarkod okuyucu desteğiyle ürünler hızlıca satış ekranına eklenebilir; nakit, kart ve veresiye satışlar kayıt altına alınabilir. Stok giriş çıkışları, kritik stok seviyeleri, alış ve satış fiyatları, tedarikçi işlemleri ve müşteri bakiyeleri aynı yapı üzerinden izlenebilir. Termal fiş, normal yazıcı ve barkod etiketi desteği sayesinde satış sonrası belge süreçleri de kolaylaşır.",
                new[] { "NSX Barkodlu Satış, Cari ve Stok Takip Pro", "barkodlu satış programı", "barkodlu cari takip programı", "barkodlu stok takip programı", "stok takip programı", "hızlı satış programı", "market satış programı", "perakende satış programı", "barkod etiketi programı", "veresiye satış programı" },
                false,
                "NSX Barkodlu Satış Pro'yu İncele",
                "/urun/nsx-barkodlu-satis-cari-ve-stok-takip-pro",
                "NSX Barkodlu Satış Cari Stok Takip Pro",
                Sections: new[]
                {
                    new SeoSection("Barkodlu hızlı satış", "Barkod okuyucu veya ürün arama ile satış kalemleri hızlıca eklenir; adet, iskonto, KDV ve toplam tutar anlık hesaplanır."),
                    new SeoSection("Stok ve ürün yönetimi", "Ürün kartları, alış-satış fiyatları, stok giriş çıkışları, kritik stok seviyeleri ve barkod bilgileri düzenli takip edilir."),
                    new SeoSection("Cari hesap ve veresiye satış", "Müşteri seçilerek veresiye satış yapılabilir; borç, tahsilat, ödeme geçmişi ve güncel bakiye müşteri kartında izlenir."),
                    new SeoSection("Kasa, tedarikçi ve raporlar", "Kasa hareketleri, tedarikçi alışları, satış geçmişi, kâr-zarar ve dönemsel raporlar tek merkezden yönetilir."),
                    new SeoSection("Fiş ve barkod etiketi", "Termal veya normal yazıcıdan perakende satış fişi alınabilir; ürünler için profesyonel barkod etiketi hazırlanabilir."),
                    new SeoSection("Kimler için uygundur?", "Market, bakkal, büfe, mağaza, kırtasiye, yedek parça, toptan ve perakende satış yapan işletmeler için uygundur.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Barkod okuyucu destekleniyor mu?", "Evet. Standart barkod okuyucularla hızlı ürün ekleme ve satış işlemi yapılabilir."),
                    new SeoFaq("Veresiye ve cari hesap takibi yapılabilir mi?", "Evet. Müşteri bazında veresiye satış, borç, tahsilat, ödeme geçmişi ve bakiye takip edilebilir."),
                    new SeoFaq("Stoklar satıştan sonra otomatik düşer mi?", "Satış tamamlandığında ilgili ürünlerin stok hareketleri kaydedilir ve güncel stok miktarı hesaplanır."),
                    new SeoFaq("Termal fiş ve barkod etiketi yazdırılabilir mi?", "Uygun yazıcı seçilerek perakende satış fişi ve ürün barkod etiketleri yazdırılabilir.")
                }),
            new(
                "nsx-kasa-defteri-pro-programi",
                "NSX Kasa Defteri Pro | Gelir Gider, Kasa ve Cari Takip Rehberi",
                "NSX Kasa Defteri Pro ile günlük gelir-gider, tahsilat, ödeme, çoklu kasa, döviz kasaları, cari hesap, kasa transferleri, rapor ve yedek süreçlerini düzenli yönetin.",
                "NSX Kasa Defteri Pro ile Gelir Gider ve Kasa Takibini Düzenli Yönetin",
                "NSX Kasa Defteri Pro; işletmenin günlük para giriş ve çıkışlarını, kasa bakiyelerini, tahsilat ve ödemelerini tek merkezde düzenli takip etmek için hazırlanmış profesyonel bir kasa defteri programıdır. Kağıt defter, dağınık not veya farklı Excel dosyaları yerine her finansal hareket ilgili kasa üzerinden kayıt altına alınır; böylece işletmenin güncel nakit durumu daha anlaşılır hale gelir.\n\nProgramı kullanmaya başlarken işletmede kullanılan kasalar tanımlanır ve gerekiyorsa açılış ya da devir bakiyesi girilir. Gün içinde oluşan gelir ve tahsilatlar para girişi olarak, gider ve ödemeler ise para çıkışı olarak doğru kasa üzerinden kaydedilir. Her hareketin aynı gün işlenmesi, gün sonu kasa kontrolünün daha sağlıklı yapılmasına yardımcı olur.\n\nBirden fazla kasa kullanan işletmeler TL kasasının yanında USD, EUR veya ihtiyaç duyulan farklı para birimleri için ayrı kasa yapıları oluşturabilir. Kasalar birbirinden bağımsız izlenebilir ve kasalar arası para aktarımı transfer işlemiyle kayıt altında tutulabilir. Böylece hem fiziksel kasalar hem de farklı para birimleri tek ekranda daha düzenli yönetilebilir.\n\nCari hesap yapısı sayesinde müşteri veya firma bazlı tahsilat ve ödeme hareketleri finansal kayıtlarla ilişkilendirilebilir. Kasa hareketleri, gelir-gider dengesi ve mevcut bakiyeler raporlar üzerinden kontrol edilerek işletmenin finansal görünümü daha net izlenebilir. Düzenli yedekleme ise kasa ve cari kayıtlarının güvenliğini destekler. NSX Kasa Defteri Pro, günlük finans takibini sadeleştirirken kayıt disiplinini korumak isteyen esnaf ve işletmeler için pratik bir Windows masaüstü çözümüdür.",
                new[] { "NSX Kasa Defteri Pro", "kasa defteri programı", "gelir gider takip programı", "kasa takip programı", "tahsilat takip programı", "ödeme takip programı", "çoklu kasa programı", "döviz kasa takibi", "cari hesap takip programı", "kasalar arası transfer", "işletme gelir gider programı", "Windows kasa defteri programı" },
                false,
                "NSX Kasa Defteri Pro'yu İncele",
                "/urun/nsx-kasa-defteri-pro",
                "NSX Kasa Defteri Pro kasa defteri gelir gider cari tahsilat ödeme",
                Sections: new[]
                {
                    new SeoSection("Kasaları ve açılış bakiyelerini tanımlayın", "İşletmede kullandığınız her kasa için ayrı kayıt oluşturun. İlk kullanıma geçerken mevcut tutarı açılış veya devir bakiyesi olarak girerek programdaki kasa bakiyesini gerçek durumla eşitleyin."),
                    new SeoSection("Gelir ve tahsilatları aynı gün kaydedin", "Satış geliri, tahsilat veya işletmeye giren diğer tutarları ilgili kasa üzerinden kayıt altına alın. Para girişlerinin düzenli işlenmesi güncel kasa bakiyesinin doğru görünmesini sağlar."),
                    new SeoSection("Gider ve ödemeleri doğru kasadan işleyin", "İşletme giderleri, tedarikçi ödemeleri ve diğer para çıkışlarını işlem yapılan kasaya kaydedin. Böylece hangi kasadan ne kadar çıkış yapıldığı geçmiş hareketlerle birlikte takip edilebilir."),
                    new SeoSection("TL ve döviz kasalarını ayrı takip edin", "TL kasasının yanında USD, EUR veya kullandığınız diğer para birimleri için ayrı kasalar oluşturabilirsiniz. Her kasanın bakiyesi kendi para birimi ve hareket geçmişi üzerinden izlenir."),
                    new SeoSection("Kasalar arası transferleri kayıt altında tutun", "Bir kasadan diğerine para aktarıldığında transfer işlemini kullanarak iki kasa arasındaki hareketi kayıt altına alın. Bu yöntem manuel bakiye düzeltmeleri yerine daha izlenebilir bir çalışma düzeni sağlar."),
                    new SeoSection("Cari hesap ile tahsilat ve ödeme düzeni", "Müşteri veya firma cari kartları üzerinden tahsilat ve ödeme hareketlerini ilgili hesapla ilişkilendirin. Cari geçmiş ile kasa hareketlerini birlikte takip etmek hesap kontrolünü kolaylaştırır."),
                    new SeoSection("Gün sonu kasa ve gelir-gider kontrolü", "Gün sonunda kasa bakiyelerini, para giriş-çıkışlarını ve gelir-gider durumunu kontrol edin. Rapor ekranları kayıtların gözden geçirilmesine ve işletmenin finansal durumunun daha net görülmesine yardımcı olur."),
                    new SeoSection("Düzenli yedekleme ile kayıtları koruyun", "Kasa ve cari hareketleri işletmenin kritik finansal verileridir. Programdaki yedekleme imkanını düzenli kullanarak güncel yedeklerinizi güvenli bir konumda saklayın.")
                },
                Faqs: new[]
                {
                    new SeoFaq("NSX Kasa Defteri Pro ne işe yarar?", "İşletmenin gelir, gider, tahsilat, ödeme ve kasa bakiyelerini düzenli kaydetmeye; çoklu kasa, cari hesap ve raporlama süreçlerini tek merkezden takip etmeye yardımcı olur."),
                    new SeoFaq("Birden fazla kasa kullanabilir miyim?", "Evet. İşletmede kullanılan farklı kasaları ayrı ayrı tanımlayabilir ve her kasanın bakiyesini kendi hareketleri üzerinden takip edebilirsiniz."),
                    new SeoFaq("TL dışında döviz kasası oluşturabilir miyim?", "Evet. TL yanında USD, EUR veya kullandığınız diğer para birimleri için ayrı kasa yapıları oluşturabilirsiniz."),
                    new SeoFaq("Kasalar arasında para transferi yapılabilir mi?", "Evet. Bir kasadan diğerine yapılan para aktarımı transfer işlemi olarak kayıt altına alınabilir; böylece iki kasanın hareket geçmişi daha düzenli tutulur."),
                    new SeoFaq("Cari hesaplarla birlikte çalışabilir miyim?", "Evet. Müşteri ve firma cari kartları kullanılarak tahsilat ve ödeme hareketleri ilgili hesapla ilişkilendirilebilir."),
                    new SeoFaq("Gün sonu kasa kontrolü nasıl yapılmalı?", "Gün içinde tüm para giriş ve çıkışlarını aynı gün kaydedip gün sonunda kasa bakiyelerini ve gelir-gider hareketlerini raporlarla karşılaştırmak düzenli kullanım için iyi bir yöntemdir."),
                    new SeoFaq("Kasa kayıtlarını nasıl daha güvenli tutabilirim?", "Düzenli yedek alın ve yedeklerinizi güvenli bir konumda saklayın. Böylece işletmenin finansal kayıtlarını olası veri kayıplarına karşı korumaya yardımcı olursunuz.")
                }),
            new(
                "nsx-sigorta-acente-pro-programi",
                "NSX Sigorta Acente Pro | Poliçe, Müşteri ve Yenileme Takip Programı",
                "NSX Sigorta Acente Pro ile müşteri, poliçe, teklif, tahsilat, kasa, komisyon ve poliçe yenileme süreçlerini tek merkezden yönetin.",
                "NSX Sigorta Acente Pro ile Poliçe ve Acente Yönetimini Tek Merkezde Toplayın",
                "Sigorta acentelerinde müşteri bilgileri, poliçeler, teklifler, tahsilatlar, komisyonlar ve yenileme tarihleri farklı dosyalarda tutulduğunda günlük takip zorlaşabilir. NSX Sigorta Acente Pro; acentenin müşteri, poliçe, sigorta şirketi, teklif, cari, tahsilat, kasa ve raporlama süreçlerini tek merkezde yönetmek için geliştirilmiştir.\n\nDesteklenen poliçe PDF'leri programa aktarılabilir; müşteri ve poliçe bilgileri daha hızlı kayda dönüştürülebilir. Poliçe başlangıç ve bitiş tarihleri üzerinden yenileme takibi yapılabilir, sigorta şirketlerine komisyon oranları tanımlanabilir ve yeni poliçelerde acente kazancı otomatik hesaplanabilir. Kasa Defteri, gelir-gider yönetimi ve profesyonel raporlar sayesinde yalnızca tahsilat değil, acentenin gerçek finansal durumu da daha net izlenebilir.",
                new[] { "NSX Sigorta Acente Pro", "sigorta acente programı", "sigorta acentesi programı", "poliçe takip programı", "poliçe yenileme programı", "sigorta müşteri takip programı", "acente komisyon takip programı", "sigorta teklif programı" },
                false,
                "NSX Sigorta Acente Pro'yu İncele",
                "/urun/nsx-sigorta-acente-pro",
                "NSX Sigorta Acente Pro",
                Sections: new[]
                {
                    new SeoSection("PDF'den hızlı poliçe aktarımı", "Desteklenen poliçe PDF'leri programa aktarılabilir; müşteri, poliçe, sigorta şirketi, poliçe numarası, tarih ve prim gibi mevcut bilgiler daha hızlı şekilde kayıt ekranına taşınabilir."),
                    new SeoSection("Müşteri ve poliçe yönetimi", "Bireysel ve kurumsal müşteriler ile trafik, kasko, konut, iş yeri, sağlık ve diğer poliçe türleri ilişkilendirilerek tek merkezden takip edilebilir."),
                    new SeoSection("Poliçe yenileme takibi", "Başlangıç ve bitiş tarihlerine göre yaklaşan poliçeler önceden görülebilir. Hatırlatma süresi ayarlanarak yenileme zamanı gelen müşterilere zamanında ulaşılması kolaylaştırılır."),
                    new SeoSection("Sigorta şirketi ve komisyon yönetimi", "Çalışılan sigorta şirketleri kaydedilebilir, şirket bazında acente komisyon oranı tanımlanabilir ve poliçe kazancı ilgili oran üzerinden otomatik hesaplanabilir."),
                    new SeoSection("Teklif, cari ve tahsilat takibi", "Müşteri teklifleri, poliçe tutarları, alınan ödemeler, kalan bakiyeler ve cari hareketler birbiriyle bağlantılı şekilde takip edilebilir."),
                    new SeoSection("Kasa, gelir-gider ve net kâr", "Kasa giriş-çıkışları, işletme giderleri, acente kazancı, net kâr ve net kâr oranı ayrı ayrı izlenerek gerçek finansal performans daha anlaşılır hale getirilebilir."),
                    new SeoSection("Profesyonel raporlar", "Cari finans, sigorta şirketi performansı, gelir-gider ve dönemsel faaliyet raporları incelenebilir; uygun raporlar PDF çıktısı olarak arşivlenebilir veya paylaşılabilir.")
                },
                Faqs: new[]
                {
                    new SeoFaq("Poliçe PDF'leri programa aktarılabilir mi?", "Evet. Desteklenen poliçe PDF'leri analiz edilerek mevcut müşteri ve poliçe bilgilerinin daha hızlı kayda dönüştürülmesine yardımcı olunur."),
                    new SeoFaq("Poliçe yenileme tarihleri takip edilir mi?", "Evet. Poliçe başlangıç ve bitiş tarihleri üzerinden yaklaşan yenilemeler takip edilebilir ve hatırlatma süresi ayarlanabilir."),
                    new SeoFaq("Sigorta şirketi komisyonları otomatik hesaplanır mı?", "Sigorta şirketi için tanımlanan acente komisyon oranı yeni poliçe kayıtlarında kullanılarak acente kazancının hesaplanmasına yardımcı olur."),
                    new SeoFaq("Kasa ve gelir-gider takibi var mı?", "Evet. Kasa hareketleri, gelirler, giderler, acente kazancı ve net kâr bilgileri ayrı alanlarda izlenebilir.")
                }),
            new(
                "nsx-cari-takip-pro-programi",
                "NSX Cari Takip Pro | Borç, Alacak, Tahsilat ve Bakiye Takibi",
                "NSX Cari Takip Pro ile müşteri ve firma carilerini, borç-alacak hareketlerini, tahsilatları, ödemeleri, hatırlatmaları ve raporları yönetin.",
                "NSX Cari Takip Pro ile Müşteri ve Firma Hesaplarını Düzenleyin",
                "NSX Cari Takip Pro, müşteri ve firma hesaplarını düzenli tutmak isteyen işletmeler için geliştirilmiş profesyonel bir cari hesap programıdır. Borç, alacak, tahsilat, ödeme, işlem geçmişi ve net bakiye bilgileri cari kart bazında takip edilir.\n\nArama, sıralama, hatırlatma, raporlama, ekstre ve yedekleme araçları sayesinde günlük finansal hareketler daha hızlı kontrol edilir. Son işlem zamanı ve güncel bakiye gibi kritik bilgiler müşteri listesinde görünür; işletme hangi cariyle ne zaman işlem yaptığını kolayca izleyebilir.",
                new[] { "NSX Cari Takip Pro", "cari takip programı", "cari hesap programı", "borç alacak takip programı", "tahsilat takip programı", "müşteri bakiye takip programı", "cari kart programı" },
                false,
                "NSX Cari Takip Pro'yu İncele",
                "/urun/nsx-cari-takip-pro-2",
                "NSX Cari Takip Pro"),
            new(
                "nsx-teknik-servis-pro-programi",
                "NSX Teknik Servis Pro | Cihaz Kabul ve Servis Takip Programı",
                "NSX Teknik Servis Pro ile müşteri, cihaz kabul, arıza, servis durumu, ödeme, teslimat, geçmiş kayıt ve raporlama süreçlerini yönetin.",
                "NSX Teknik Servis Pro ile Servis Kayıtlarını Tek Merkezde Yönetin",
                "NSX Teknik Servis Pro; bilgisayar, telefon, elektronik ve benzeri teknik servis işletmelerinde müşteri ve cihaz kabulünden teslimata kadar tüm süreci düzenlemek için geliştirilmiştir. Arıza açıklaması, cihaz bilgisi, servis durumu, yapılan işlemler, ücret, ödeme ve teslim bilgileri aynı servis kaydında tutulur.\n\nMüşteri ve cihaz geçmişi sayesinde daha önce yapılan işlemler hızlıca görülebilir. Servis fişi, durum takibi, raporlama ve hatırlatma özellikleri yoğun servis iş akışında kayıt kaybını azaltır.",
                new[] { "NSX Teknik Servis Pro", "teknik servis programı", "teknik servis takip programı", "cihaz kabul programı", "servis fişi programı", "müşteri cihaz takip programı" },
                false,
                "NSX Teknik Servis Pro'yu İncele",
                "/urun/nsx-teknik-servis-pro-2",
                "NSX Teknik Servis Pro"),
            new(
                "nsx-security-auditor-pro-kod-guvenlik-analizi",
                "NSX Security Auditor Pro | AI Destekli Kod Güvenlik Analizi",
                "NSX Security Auditor Pro ile kaynak kod, API, web, masaüstü ve publish çıktılarındaki güvenlik risklerini; gizli anahtarları ve bağımlılık açıklarını yayından önce analiz edin.",
                "NSX Security Auditor Pro ile Kodunuzu Yayına Çıkmadan Önce Denetleyin",
                "Yazılım projelerinde güvenlik kontrolünü yalnızca yayından sonra yapmak; gömülü API anahtarlarının, riskli kod kalıplarının, güvensiz veri akışlarının ve zafiyetli bağımlılıkların üretime taşınmasına neden olabilir. NSX Security Auditor Pro, kaynak kodları çalıştırmadan inceleyerek bu riskleri geliştirme aşamasında görünür hale getiren profesyonel bir güvenlik analiz platformudur.\n\nKaynak kod, web projesi, API, masaüstü uygulaması, bağımlılık dosyası ve publish çıktıları tek merkezden taranır. Her bulgu; risk seviyesi, ilgili kod bağlamı, güven oranı, teknik açıklama ve uygulanabilir çözüm önerisiyle sunulur. Böylece geliştirici yalnızca bir uyarı listesi değil, hangi riski neden ve nasıl gidereceğini gösteren düzenli bir çalışma planı elde eder.\n\nİsteğe bağlı yapay zekâ ikinci görüşü; yerel model, Google Gemini, OpenAI veya OpenAI uyumlu servislerle yapılandırılabilir. Onaylanan ve maskelenen bulgu bilgileri yeniden değerlendirilerek yanlış pozitiflerin ayırt edilmesine ve çözüm adımlarının netleştirilmesine yardımcı olur. Proje dosyaları değiştirilmez, kaynak kod çalıştırılmaz.",
                new[] { "NSX Security Auditor Pro", "kod güvenlik analizi", "AI kod analizi", "kaynak kod güvenlik taraması", "API güvenlik analizi", "publish güvenlik kontrolü", "gizli anahtar tespiti", "bağımlılık zafiyet taraması", "SARIF raporu", "SBOM raporu" },
                false,
                "NSX Security Auditor Pro'yu İncele",
                "/urun/nsx-security-auditor-pro-2",
                "NSX Security Auditor Pro",
                Sections: new[]
                {
                    new SeoSection("Kaynak kod ve proje analizi", "Web, API, masaüstü ve farklı proje türlerindeki riskli kod kalıpları kaynak kod çalıştırılmadan incelenir. Bulgular dosya ve kod bağlamıyla birlikte gösterilir."),
                    new SeoSection("Gizli bilgi ve anahtar tespiti", "Kaynak koda yanlışlıkla eklenen API anahtarları, tokenlar, parolalar ve diğer hassas bilgiler taranır; yayına çıkmadan önce temizlenmesi gereken noktalar belirlenir."),
                    new SeoSection("Bağımlılık ve CVE denetimi", "NuGet, npm, PyPI, Maven, Composer, Go Modules ve Cargo bağımlılıkları bilinen güvenlik açıkları açısından incelenerek riskli paketler önceliklendirilir."),
                    new SeoSection("AI destekli ikinci görüş", "İsteğe bağlı yapay zekâ analizi, maskelenmiş bulguları yeniden değerlendirir; olası yanlış pozitifleri ayırmaya ve geliştirici odaklı çözüm önerileri oluşturmaya yardımcı olur."),
                    new SeoSection("Profesyonel güvenlik raporları", "HTML ve PDF yönetici raporlarının yanında SARIF ve SBOM çıktıları hazırlanabilir. Böylece bulgular geliştirme, denetim ve dokümantasyon süreçlerine taşınabilir."),
                    new SeoSection("Kimler için uygundur?", "Bireysel geliştiriciler, yazılım şirketleri, teknik ekipler, güvenlik uzmanları ve projelerini yayından önce düzenli bir güvenlik kontrolünden geçirmek isteyen kurumlar için uygundur.")
                },
                Faqs: new[]
                {
                    new SeoFaq("NSX Security Auditor Pro kaynak kodu çalıştırır mı?", "Hayır. Proje dosyaları statik olarak incelenir; kaynak kod çalıştırılmaz ve tarama sırasında proje dosyaları değiştirilmez."),
                    new SeoFaq("Hangi proje türleri analiz edilebilir?", "Kaynak kod projeleri, web uygulamaları, API'ler, masaüstü uygulamaları, bağımlılık dosyaları ve publish çıktıları analiz edilebilir."),
                    new SeoFaq("API anahtarı ve parola gibi bilgiler bulunabilir mi?", "Evet. Kaynak koda gömülmüş olabilecek API anahtarları, tokenlar, parolalar ve hassas bilgi kalıpları taranabilir."),
                    new SeoFaq("Yapay zekâ kullanmak zorunlu mu?", "Hayır. Temel güvenlik taraması yerel analiz motoruyla yapılır. AI ikinci görüşü isteğe bağlıdır ve desteklenen servislerden biriyle ayrıca yapılandırılabilir."),
                    new SeoFaq("Hangi rapor türleri alınabilir?", "Teknik bulgular ve yönetici özeti için HTML ve PDF; güvenlik araçlarıyla entegrasyon için SARIF; bileşen envanteri için SBOM raporları hazırlanabilir.")
                }),
            new(
                "nsx-okul-plan-pro-ders-programi",
                "NSX Okul Plan Pro | Ders Programı ve Nöbet Planlama Yazılımı",
                "NSX Okul Plan Pro ile öğretmen, sınıf, derslik, ders programı, atama ve nöbet planlama süreçlerini tek merkezden profesyonelce yönetin.",
                "NSX Okul Plan Pro ile Ders ve Nöbet Planlamasını Tek Merkezde Yönetin",
                "Okullarda haftalık ders programı hazırlanırken öğretmenlerin ders yükleri, sınıflar, derslikler, ders saatleri ve nöbet görevleri birbiriyle uyumlu biçimde planlanmalıdır. Bu bilgilerin farklı tablolar veya dağınık belgeler üzerinden takip edilmesi, planlama süresini uzatabilir ve değişikliklerin yönetilmesini zorlaştırabilir. NSX Okul Plan Pro, okulun temel planlama verilerini tek merkezde toplayan profesyonel bir Windows masaüstü çözümüdür.\n\nÖğretmen, sınıf, derslik ve ders tanımları düzenli kartlar halinde yönetilir. Hazırlanan bilgiler kullanılarak haftalık ders programı oluşturulur; öğretmen ve sınıf bazındaki atamalar aynı çalışma düzeni içinde takip edilir. Böylece idare, programın genel görünümünü daha anlaşılır biçimde kontrol edebilir ve gerekli düzenlemeleri merkezi bir yapı üzerinden yapabilir.\n\nNöbet planlama bölümü, öğretmen nöbet görevlerinin okulun çalışma düzenine göre kaydedilmesini ve izlenmesini kolaylaştırır. Günlük program, yaklaşan görevler ve planlama özetleri ana ekran üzerinden takip edilebilir. Ders ve nöbet bilgilerinin aynı sistemde bulunması, okul yönetiminin günlük koordinasyonunu daha düzenli hale getirir.\n\nPDF ve yazdırma desteğiyle hazırlanan programlar dijital olarak arşivlenebilir veya fiziksel çıktı halinde paylaşılabilir. Raporlama ekranları ise tanımlı öğretmen, sınıf, derslik, ders ve görev bilgilerini daha kontrollü değerlendirmeye yardımcı olur. NSX Okul Plan Pro; okul yöneticileri, müdür yardımcıları ve ders programı hazırlayan yetkili personel için sade, hızlı ve kurumsal bir planlama deneyimi sunar.",
                new[] { "NSX Okul Plan Pro", "okul ders programı", "ders programı hazırlama programı", "öğretmen ders programı", "sınıf ders programı", "nöbet planlama programı", "öğretmen nöbet çizelgesi", "derslik planlama", "okul yönetim yazılımı", "Windows okul programı" },
                false,
                "NSX Okul Plan Pro'yu İncele",
                "/urun/nsx-okul-plan-pro",
                "NSX Okul Plan Pro",
                Sections: new[]
                {
                    new SeoSection("Öğretmen kayıtlarını düzenli yönetin", "Öğretmen bilgileri tek merkezde tanımlanarak ders programı ve nöbet planlama süreçlerinde kullanılabilir. Böylece planlamanın temel kayıtları dağınık dosyalardan bağımsız hale gelir."),
                    new SeoSection("Sınıf, derslik ve ders tanımları", "Sınıflar, kullanılacak derslikler ve ders bilgileri ayrı kayıtlar halinde yönetilir. Planlama sırasında ihtiyaç duyulan okul yapısı daha anlaşılır ve kontrollü biçimde hazırlanır."),
                    new SeoSection("Haftalık ders programı", "Ders saatleri haftanın günlerine ve ders sıralarına göre planlanabilir. Haftalık görünüm, sınıf ve öğretmen programlarının bütününü tek ekranda değerlendirmeyi kolaylaştırır."),
                    new SeoSection("Atama süreçlerini tek merkezde takip edin", "Öğretmen, sınıf, ders ve derslik ilişkileri atama düzeni içinde yönetilir. Yapılan değişikliklerin aynı merkezden yürütülmesi planlama kontrolünü güçlendirir."),
                    new SeoSection("Öğretmen nöbet planlaması", "Nöbet görevleri gün, görev yeri ve öğretmen bilgileriyle planlanabilir. Günlük nöbet düzeninin kayıt altında tutulması okul içi koordinasyonu kolaylaştırır."),
                    new SeoSection("PDF, yazdırma ve paylaşım", "Hazırlanan ders ve nöbet programları uygun çıktı seçenekleriyle PDF olarak kaydedilebilir veya yazdırılabilir. Böylece idare, öğretmenler ve ilgili birimlerle paylaşım kolaylaşır."),
                    new SeoSection("Raporlama ve genel kontrol", "Programdaki öğretmen, sınıf, derslik, ders ve görev kayıtları raporlar üzerinden değerlendirilebilir. Yönetim, planlama verilerini daha düzenli inceleyebilir ve arşivleyebilir."),
                    new SeoSection("Kimler için uygundur?", "İlkokul, ortaokul, lise, kurs merkezi ve benzeri eğitim kurumlarında ders programı ile öğretmen nöbet çizelgesi hazırlayan okul yöneticileri ve yetkili personel için uygundur.")
                },
                Faqs: new[]
                {
                    new SeoFaq("NSX Okul Plan Pro ne işe yarar?", "Öğretmen, sınıf, derslik ve ders kayıtlarını yönetmeye; haftalık ders programı, atama ve öğretmen nöbet planlarını tek merkezde hazırlamaya yardımcı olur."),
                    new SeoFaq("Öğretmen ve sınıf bazında ders programı hazırlanabilir mi?", "Evet. Tanımlanan öğretmen, sınıf, ders ve derslik bilgileri kullanılarak haftalık program düzenli biçimde oluşturulabilir ve takip edilebilir."),
                    new SeoFaq("Öğretmen nöbet çizelgesi hazırlanabilir mi?", "Evet. Nöbet görevleri öğretmen, gün ve görev düzeniyle planlanabilir; günlük nöbet bilgileri aynı sistem üzerinden izlenebilir."),
                    new SeoFaq("Ders programı PDF olarak alınabilir mi?", "Programdaki PDF ve yazdırma seçenekleriyle hazırlanan planlar dijital olarak kaydedilebilir veya fiziksel çıktı halinde paylaşılabilir."),
                    new SeoFaq("NSX Okul Plan Pro hangi kurumlar için uygundur?", "Ders ve nöbet planlaması yapan okullar, kurs merkezleri ve benzeri eğitim kurumları için uygundur.")
                }),
            new(
                "oyun-hizlandirma-fps-artirma-programi",
                "Oyun Hızlandırma ve FPS Artırma Programı | NSX Turbo",
                "Oyun hızlandırma ve FPS artırma programı ile bilgisayar kaynaklarını daha verimli kullanarak oyun performansını iyileştirin.",
                "Oyun Hızlandırma ve FPS Artırma Programı",
                "Oyun hızlandırma ve FPS artırma programı; arka plan yüklerini azaltma, sistem performansını optimize etme ve oyun sırasında daha stabil deneyim sağlama hedefiyle kullanılır. NSX Turbo, oyun ve günlük kullanım performansını daha pratik yönetmek isteyen kullanıcılar için hazırlanır.",
                new[] { "oyun hızlandırma programı", "fps artırma programı", "nsx turbo" },
                false,
                "NSX Turbo Ürünlerini İncele",
                "/store?q=turbo",
                "turbo")
        };

        private static readonly HashSet<string> FreeVeresiyePageSlugs = new(StringComparer.OrdinalIgnoreCase)
        {
            "ucretsiz-veresiye-programi",
            "veresiye-programi-nedir",
            "ucretsiz-veresiye-programi-nedir",
            "ucretsiz-cari-takip-programi",
            "esnaflar-icin-veresiye-takip-programi",
            "borc-alacak-takip-programi-ucretsiz",
            "bakkal-veresiye-defteri-nasil-tutulur",
            "cari-hesap-takibi-nasil-yapilir",
            "excel-yerine-veresiye-takip-programi",
            "veresiye-defteri-programi",
            "ucretsiz-cari-hesap-programi",
            "veresiye-cari-takip-programi-pro"
        };

        private static bool IsFreeVeresiyePageSlug(string? slug)
            => !string.IsNullOrWhiteSpace(slug) && FreeVeresiyePageSlugs.Contains(slug);

        private static readonly string[] FeaturedSeoBlogSlugs =
        {
            "nsx-cari-takip-pro-bulut-programi",
            "nsx-veri-kurtarma-pro-programi",
            "ucretsiz-veresiye-programi-nedir",
            "nsx-barkodlu-satis-cari-stok-takip-pro",
            "nsx-kasa-defteri-pro-programi",
            "nsx-sigorta-acente-pro-programi",
            "nsx-okul-plan-pro-ders-programi",
            "nsx-security-auditor-pro-kod-guvenlik-analizi",
            "nsx-cari-takip-pro-programi",
            "nsx-teknik-servis-pro-programi",
            "dugun-salonu-yonetim-programi",
            "nsx-servispro-live-teknik-servis-programi",
            "oto-galeri-yonetim-programi",
            "oto-tamir-servis-yonetim-programi",
            "klinik-randevu-takip-programi",
            "veresiye-cari-takip-programi-pro"
        };

        [HttpGet("blog")]
        public async Task<IActionResult> Index()
        {
            var orderedPages = Pages
                .Where(x => !x.IsLanding)
                .OrderBy(x =>
                {
                    var featuredIndex = Array.IndexOf(FeaturedSeoBlogSlugs, x.Slug);
                    return featuredIndex >= 0 ? featuredIndex : int.MaxValue;
                })
                .ThenBy(x => Array.IndexOf(FeaturedSeoBlogSlugs, x.Slug) >= 0 ? 0 : (IsVeresiyeRelated(x) ? 0 : 1))
                .ThenBy(x => x.Slug)
                .ToList();
            var resolvedBlogPages = await ResolvePageImagesAsync(orderedPages);
            var blogPages = await LocalizePagesAsync(resolvedBlogPages, HttpContext.RequestAborted);

            var cancellationToken = HttpContext.RequestAborted;
            var language = await _localization.GetCurrentLanguageAsync(cancellationToken);
            var metaTitle = await _localization.TranslateAsync("NSX Yazılım Blog | İşletme ve Yazılım Rehberleri", cancellationToken: cancellationToken);
            var metaDescription = await _localization.TranslateAsync("NSX yazılımları için kullanım rehberleri: bulut cari takip, veri kurtarma, okul planlama, kasa, veresiye, stok ve servis yönetimini adım adım keşfedin.", cancellationToken: cancellationToken);
            var ogImageAlt = await _localization.TranslateAsync("NSX Yazılım işletme ve yazılım rehberleri", cancellationToken: cancellationToken);
            var blogSchemaName = await _localization.TranslateAsync("NSX Yazılım Blog", cancellationToken: cancellationToken);
            var blogSchemaDescription = await _localization.TranslateAsync("Bulut ve mobil cari takip, veri kurtarma, okul planlama, kasa defteri, veresiye, stok, teknik servis, otomotiv, klinik ve organizasyon yazılımları için kullanım rehberleri.", cancellationToken: cancellationToken);
            var guideListName = await _localization.TranslateAsync("NSX Yazılım Rehberleri", cancellationToken: cancellationToken);
            var homeName = await _localization.TranslateAsync("Ana Sayfa", cancellationToken: cancellationToken);
            var extraKeywords = new[]
            {
                await _localization.TranslateAsync("yazılım rehberleri", cancellationToken: cancellationToken),
                await _localization.TranslateAsync("işletme yazılımı rehberi", cancellationToken: cancellationToken),
                await _localization.TranslateAsync("program kullanım rehberi", cancellationToken: cancellationToken)
            };

            ViewData["Title"] = "Blog";
            ViewData["MetaTitle"] = metaTitle;
            ViewData["MetaDescription"] = metaDescription;
            ViewData["MetaKeywords"] = string.Join(", ", blogPages
                .SelectMany(x => x.Keywords ?? Array.Empty<string>())
                .Concat(extraKeywords)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24));
            ViewData["Canonical"] = SeoTextHelper.AbsoluteUrl("/blog");
            ViewData["OgImage"] = blogPages.FirstOrDefault()?.ImageUrl ?? GetBlogImageUrl("ucretsiz-veresiye-programi-nedir");
            ViewData["OgImageAlt"] = ogImageAlt;
            ViewData["OgImageWidth"] = 1581;
            ViewData["OgImageHeight"] = 995;
            ViewData["JsonLd"] = BuildBlogIndexJsonLd(
                blogPages,
                language.Code,
                blogSchemaName,
                blogSchemaDescription,
                ogImageAlt,
                guideListName,
                homeName,
                Request.PathBase.Value ?? string.Empty);

            return View(blogPages);
        }

        [HttpGet("blog/{slug}")]
        public async Task<IActionResult> Detail(string slug)
        {
            return await DetailCoreAsync(slug, true);
        }

        [HttpGet("ucretsiz-veresiye-programi")]
        [HttpGet("ucretsiz-teknik-servis-programi")]
        [HttpGet("ucretsiz-bilgisayar-hizlandirma-programi")]
        [HttpGet("oto-tamir-programi")]
        [HttpGet("oto-galeri-programi")]
        [HttpGet("servis-live-programi")]
        [HttpGet("klinik-programi")]
        [HttpGet("randevu-programi")]
        public async Task<IActionResult> Landing()
        {
            var slug = (Request.Path.Value ?? string.Empty).Trim('/').ToLowerInvariant();
            return await DetailCoreAsync(slug, false);
        }

        private async Task<IActionResult> DetailCoreAsync(string slug, bool fromBlog)
        {
            slug = (slug ?? string.Empty).Trim().ToLowerInvariant();

            // The old keyword landing duplicated the product page title, intent and body.
            // Consolidate both legacy URL forms directly into the commercial product page.
            if (string.Equals(slug, "ucretsiz-veresiye-programi", StringComparison.OrdinalIgnoreCase))
                return RedirectPermanent($"{Request.PathBase}/urun/nsx-veresiye-takip-pro-free");

            var page = Pages.FirstOrDefault(x => string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase));
            if (page == null)
                return NotFound();

            if (fromBlog && page.IsLanding)
                return RedirectPermanent($"{Request.PathBase}/{page.Slug}");

            var canonicalPath = page.IsLanding ? $"/{page.Slug}" : $"/blog/{page.Slug}";
            var relatedPages = GetRelatedPages(page);
            var resolvedPages = await ResolvePageImagesAsync(new[] { page }.Concat(relatedPages));
            var localizedPages = await LocalizePagesAsync(resolvedPages, HttpContext.RequestAborted);
            page = localizedPages[0];

            var language = await _localization.GetCurrentLanguageAsync(HttpContext.RequestAborted);
            var dateCulture = new System.Globalization.CultureInfo(language.Code);
            ViewBag.PublishedDateText = page.PublishedAt.ToString("dd MMMM yyyy", dateCulture);

            ViewData["Title"] = page.H1;
            ViewData["MetaTitle"] = page.Title;
            ViewData["MetaDescription"] = page.Description;
            ViewData["MetaKeywords"] = string.Join(", ", page.Keywords.Concat(new[] { "NSX Yazılım" }).Distinct(StringComparer.OrdinalIgnoreCase).Take(16));
            ViewData["Canonical"] = SeoTextHelper.AbsoluteUrl(canonicalPath);
            ViewData["OgImage"] = page.ImageUrl;
            ViewData["OgImageAlt"] = page.ImageAlt;
            ViewData["OgImageWidth"] = page.ImageWidth;
            ViewData["OgImageHeight"] = page.ImageHeight;
            ViewData["OgType"] = page.IsLanding ? "website" : "article";
            ViewData["JsonLd"] = await BuildDetailJsonLdAsync(
                page,
                canonicalPath,
                language.Code,
                HttpContext.RequestAborted);
            ViewBag.RelatedPages = localizedPages.Skip(1).ToList();

            await PrepareRelatedProductViewBagAsync(page);

            return View("Detail", page);
        }

        private async Task<List<SeoPage>> LocalizePagesAsync(
            IEnumerable<SeoPage> pages,
            CancellationToken cancellationToken)
        {
            var output = new List<SeoPage>();
            foreach (var page in pages)
                output.Add(await LocalizePageAsync(page, cancellationToken));

            return output;
        }

        private async Task<SeoPage> LocalizePageAsync(SeoPage page, CancellationToken cancellationToken)
        {
            var title = await _localization.TranslateAsync(page.Title, cancellationToken: cancellationToken);
            var description = await _localization.TranslateAsync(page.Description, cancellationToken: cancellationToken);
            var h1 = await _localization.TranslateAsync(page.H1, cancellationToken: cancellationToken);
            var body = await _localization.TranslateAsync(page.Body, cancellationToken: cancellationToken);
            var language = await _localization.GetCurrentLanguageAsync(cancellationToken);
            string imageAlt;
            if (language.Code.StartsWith("tr", StringComparison.OrdinalIgnoreCase))
            {
                imageAlt = page.ImageAlt;
            }
            else if (IsVeresiyeSlug(page.Slug))
            {
                imageAlt = await _localization.TranslateAsync(
                    "Ücretsiz veresiye programı NSX Veresiye Takip Pro",
                    cancellationToken: cancellationToken);
            }
            else
            {
                var imageLabel = await _localization.TranslateAsync(
                    "NSX Yazılım ürün görseli",
                    cancellationToken: cancellationToken);
                imageAlt = $"{h1} — {imageLabel}";
            }

            var ctaText = string.IsNullOrWhiteSpace(page.CtaText)
                ? page.CtaText
                : await _localization.TranslateAsync(page.CtaText, cancellationToken: cancellationToken);

            var keywords = new string[page.Keywords.Length];
            for (var i = 0; i < page.Keywords.Length; i++)
                keywords[i] = await _localization.TranslateAsync(page.Keywords[i], cancellationToken: cancellationToken);

            SeoSection[]? sections = null;
            if (page.Sections is { Length: > 0 })
            {
                sections = new SeoSection[page.Sections.Length];
                for (var i = 0; i < page.Sections.Length; i++)
                {
                    var source = page.Sections[i];
                    sections[i] = new SeoSection(
                        await _localization.TranslateAsync(source.Heading, cancellationToken: cancellationToken),
                        await _localization.TranslateAsync(source.Content, cancellationToken: cancellationToken));
                }
            }

            SeoFaq[]? faqs = null;
            if (page.Faqs is { Length: > 0 })
            {
                faqs = new SeoFaq[page.Faqs.Length];
                for (var i = 0; i < page.Faqs.Length; i++)
                {
                    var source = page.Faqs[i];
                    faqs[i] = new SeoFaq(
                        await _localization.TranslateAsync(source.Question, cancellationToken: cancellationToken),
                        await _localization.TranslateAsync(source.Answer, cancellationToken: cancellationToken));
                }
            }

            return page with
            {
                Title = title,
                Description = description,
                H1 = h1,
                Body = body,
                Keywords = keywords,
                CtaText = ctaText,
                Sections = sections,
                Faqs = faqs,
                ResolvedImageAlt = imageAlt
            };
        }

        private async Task PrepareRelatedProductViewBagAsync(SeoPage page)
        {
            try
            {
                var product = await FindRelatedProductAsync(page);
                if (product != null)
                {
                    var isFreeProduct = product.YearlyPrice <= 0 && product.LifetimePrice <= 0;
                    ViewBag.RelatedProductName = product.Name;
                    ViewBag.RelatedProductUrl = ProductPublicUrl(product);
                    ViewBag.RelatedProductPrimaryText = await _localization.TranslateAsync(
                        isFreeProduct ? "Hemen Ücretsiz İndir" : "Demo İndir",
                        cancellationToken: HttpContext.RequestAborted);
                    ViewBag.RelatedProductPrimaryUrl = isFreeProduct
                        ? $"/store/downloadfree?productId={product.Id}"
                        : $"/store/downloaddemo?productId={product.Id}";
                    return;
                }

                // Local veritabanında ürün yoksa yanlış ürüne düşmesin; sayfaya tanımlı doğru ürün linki gösterilsin.
                await SetFallbackRelatedProductViewBagAsync(page);
            }
            catch
            {
                // Veritabanı geçici kapalı olsa bile SEO sayfası açılmaya devam etsin.
                await SetFallbackRelatedProductViewBagAsync(page);
            }
        }

        private async Task SetFallbackRelatedProductViewBagAsync(SeoPage page)
        {
            if (string.IsNullOrWhiteSpace(page.CtaUrl))
                return;

            ViewBag.RelatedProductName = page.H1;
            ViewBag.RelatedProductUrl = page.CtaUrl;
            ViewBag.RelatedProductPrimaryText = string.IsNullOrWhiteSpace(page.CtaText)
                ? await _localization.TranslateAsync("Ürün Sayfasına Git", cancellationToken: HttpContext.RequestAborted)
                : page.CtaText;
            ViewBag.RelatedProductPrimaryUrl = page.CtaUrl;
        }

        private async Task<List<SeoPage>> ResolvePageImagesAsync(IEnumerable<SeoPage> pages)
        {
            var pageList = pages.ToList();
            if (pageList.Count == 0)
                return pageList;

            try
            {
                var products = await _context.Products
                    .AsNoTracking()
                    .Include(x => x.Images)
                    .Where(x => !x.IsDeleted && x.IsActive)
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync();

                return pageList.Select(page =>
                {
                    var product = FindRelatedProduct(page, products);
                    var image = product?.Images
                        .Where(x => !string.IsNullOrWhiteSpace(x.ImagePath))
                        .OrderByDescending(x => x.IsMain)
                        .ThenBy(x => x.SortOrder)
                        .ThenBy(x => x.Id)
                        .FirstOrDefault();

                    if (image == null)
                        return page;

                    var absoluteUrl = SeoTextHelper.AbsoluteUrl(image.ImagePath);
                    absoluteUrl += (absoluteUrl.Contains('?') ? "&" : "?") + "v=" + image.Id;
                    return page with { ResolvedImageUrl = absoluteUrl };
                }).ToList();
            }
            catch
            {
                // DB geçici olarak erişilemezse rehberler eski yedek görsellerle açılmaya devam eder.
                return pageList;
            }
        }

        private async Task<Product?> FindRelatedProductAsync(SeoPage page)
        {
            var searchText = (page.ProductSearch ?? page.H1 ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(searchText))
                return null;

            var products = await _context.Products
                .AsNoTracking()
                .Where(x => !x.IsDeleted && x.IsActive)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            return FindRelatedProduct(page, products);
        }

        private static Product? FindRelatedProduct(SeoPage page, IReadOnlyList<Product> products)
        {
            var searchText = (page.ProductSearch ?? page.H1 ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(searchText))
                return null;

            var exactSlug = GetProductSlugFromUrl(page.CtaUrl);
            if (!string.IsNullOrWhiteSpace(exactSlug))
            {
                var exactProduct = products.FirstOrDefault(x => string.Equals(x.Slug, exactSlug, StringComparison.OrdinalIgnoreCase));
                if (exactProduct != null)
                    return exactProduct;

                // Sayfa belli bir ürün URL'sine bağlıysa, local veritabanında o ürün yokken
                // benzer kelimelerden dolayı başka ürüne (ör. Klinik) düşmesin.
                return null;
            }

            var bestMatch = products
                .Select(x => new { Product = x, Score = ProductScore(x, searchText, page.Keywords) })
                .Where(x => x.Score >= 24)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Product.CreatedAt)
                .FirstOrDefault();

            return bestMatch?.Product;
        }

        private static int ProductScore(Product product, string searchText, string[] keywords)
        {
            var haystack = $"{product.Name} {product.Slug} {product.MetaTitle} {product.MetaDescription} {product.Description}".ToLowerInvariant();
            var search = (searchText ?? string.Empty).ToLowerInvariant();
            var score = 0;

            if (!string.IsNullOrWhiteSpace(search) && haystack.Contains(search))
                score += 60;

            foreach (var token in search.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 1))
            {
                if (haystack.Contains(token))
                    score += 12;
            }

            foreach (var keyword in (keywords ?? Array.Empty<string>()).Take(6).Select(x => (x ?? string.Empty).Trim().ToLowerInvariant()).Where(x => x.Length > 2))
            {
                if (haystack.Contains(keyword))
                    score += 8;
            }

            return score;
        }

        private static string ProductPublicUrl(Product product)
        {
            var slug = (product.Slug ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(slug)
                ? $"/urun/{slug.ToLowerInvariant()}"
                : $"/store/detail/{product.Id}";
        }

        private static string? GetProductSlugFromUrl(string? url)
        {
            var value = (url ?? string.Empty).Trim();
            const string productPrefix = "/urun/";

            if (!value.StartsWith(productPrefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var slug = value[productPrefix.Length..].Trim('/').Split('?', '#')[0].Trim();
            return string.IsNullOrWhiteSpace(slug) ? null : slug;
        }

        private static IReadOnlyList<SeoPage> GetRelatedPages(SeoPage page)
        {
            if (IsVeresiyeRelated(page))
            {
                return Pages
                    .Where(x => x.Slug != page.Slug
                        && !string.Equals(x.Slug, "ucretsiz-veresiye-programi", StringComparison.OrdinalIgnoreCase)
                        && IsVeresiyeRelated(x))
                    .OrderByDescending(x => x.IsLanding)
                    .ThenBy(x => x.Slug)
                    .Take(5)
                    .ToList();
            }

            return Pages
                .Where(x => x.Slug != page.Slug && x.IsLanding)
                .Take(4)
                .ToList();
        }

        private static bool IsVeresiyeSlug(string slug)
        {
            return IsFreeVeresiyePageSlug(slug)
                || string.Equals(slug, "veresiye-cari-takip-programi-pro", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVeresiyeRelated(SeoPage page)
        {
            return page != null && IsFreeVeresiyePageSlug(page.Slug);
        }

        private static readonly JsonSerializerOptions JsonLdOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        private static string BuildBlogIndexJsonLd(
            IReadOnlyList<SeoPage> blogPages,
            string languageCode,
            string blogName,
            string blogDescription,
            string imageAlt,
            string guideListName,
            string homeName,
            string pathPrefix)
        {
            var itemListElements = blogPages
                .Select((page, index) =>
                {
                    var pagePath = page.IsLanding ? $"/{page.Slug}" : $"/blog/{page.Slug}";
                    return new Dictionary<string, object?>
                    {
                        ["@type"] = "ListItem",
                        ["position"] = index + 1,
                        ["name"] = page.H1,
                        ["url"] = SeoTextHelper.AbsoluteUrl(pathPrefix + pagePath)
                    };
                })
                .ToList();

            var data = new Dictionary<string, object?>
            {
                ["@context"] = "https://schema.org",
                ["@graph"] = new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["@type"] = "Blog",
                        ["name"] = blogName,
                        ["description"] = blogDescription,
                        ["url"] = SeoTextHelper.AbsoluteUrl($"{pathPrefix}/blog"),
                        ["image"] = ImageNode(GetBlogImageUrl("ucretsiz-veresiye-programi-nedir"), imageAlt, 1581, 995),
                        ["inLanguage"] = languageCode,
                        ["publisher"] = PublisherNode()
                    },
                    new()
                    {
                        ["@type"] = "ItemList",
                        ["name"] = guideListName,
                        ["itemListElement"] = itemListElements
                    },
                    BuildBreadcrumb("Blog", "/blog", true, homeName, pathPrefix)
                }
            };

            return JsonLdScript(data);
        }

        private async Task<string> BuildDetailJsonLdAsync(
            SeoPage page,
            string canonicalPath,
            string languageCode,
            CancellationToken cancellationToken)
        {
            var pathPrefix = Request.PathBase.Value ?? string.Empty;
            var absoluteUrl = SeoTextHelper.AbsoluteUrl(pathPrefix + canonicalPath);
            var graph = new List<Dictionary<string, object?>>();
            var homeName = await _localization.TranslateAsync("Ana Sayfa", cancellationToken: cancellationToken);

            if (page.IsLanding)
            {
                graph.Add(new Dictionary<string, object?>
                {
                    ["@type"] = "WebPage",
                    ["name"] = page.H1,
                    ["url"] = absoluteUrl,
                    ["description"] = page.Description,
                    ["primaryImageOfPage"] = ImageNode(page.ImageUrl, page.ImageAlt, page.ImageWidth, page.ImageHeight),
                    ["inLanguage"] = languageCode,
                    ["publisher"] = PublisherNode(),
                    ["about"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "SoftwareApplication",
                        ["name"] = page.H1,
                        ["applicationCategory"] = "BusinessApplication",
                        ["operatingSystem"] = "Windows"
                    }
                });
            }
            else
            {
                graph.Add(new Dictionary<string, object?>
                {
                    ["@type"] = "BlogPosting",
                    ["headline"] = page.H1,
                    ["description"] = page.Description,
                    ["url"] = absoluteUrl,
                    ["mainEntityOfPage"] = absoluteUrl,
                    ["inLanguage"] = languageCode,
                    ["image"] = ImageNode(page.ImageUrl, page.ImageAlt, page.ImageWidth, page.ImageHeight),
                    ["datePublished"] = page.PublishedAt.ToString("O"),
                    ["dateModified"] = page.ModifiedAt.ToString("O"),
                    ["author"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "Organization",
                        ["name"] = "NSX Yazılım",
                        ["url"] = SeoTextHelper.Domain
                    },
                    ["publisher"] = PublisherNode(),
                    ["keywords"] = string.Join(", ", page.Keywords ?? Array.Empty<string>())
                });
            }

            graph.Add(BuildBreadcrumb(page.H1, canonicalPath, page.IsLanding, homeName, pathPrefix));

            if (page.Faqs?.Length > 0)
            {
                graph.Add(new Dictionary<string, object?>
                {
                    ["@type"] = "FAQPage",
                    ["mainEntity"] = page.Faqs.Select(x => FaqNode(x.Question, x.Answer)).ToList()
                });
            }
            else if (IsVeresiyeRelated(page))
            {
                graph.Add(new Dictionary<string, object?>
                {
                    ["@type"] = "FAQPage",
                    ["mainEntity"] = new List<Dictionary<string, object?>>
                    {
                        FaqNode(
                            await _localization.TranslateAsync("NSX Veresiye Takip Pro ücretsiz mi?", cancellationToken: cancellationToken),
                            await _localization.TranslateAsync("Evet. Ücretsiz kullanım için sunulur ve güncel v1.0.5 sürümünde masaüstü cari takibe ek olarak Bulut ve Mobil Yönetim özellikleri bulunur.", cancellationToken: cancellationToken)),
                        FaqNode(
                            await _localization.TranslateAsync("Bakkal ve marketler için uygun mu?", cancellationToken: cancellationToken),
                            await _localization.TranslateAsync("Evet. Bakkal, market, bayi ve küçük işletmeler müşteri carilerini masaüstünden takip edebilir; QR bağlantısıyla mobil panelden de müşteri, borç, tahsilat ve açık bakiye bilgilerine erişebilir.", cancellationToken: cancellationToken)),
                        FaqNode(
                            await _localization.TranslateAsync("Cari hesap takibi telefondan yapılır mı?", cancellationToken: cancellationToken),
                            await _localization.TranslateAsync("Evet. Mobil panelde müşteri aranıp seçildiğinde cari hareketleri açılır; yeni borç veya tahsilat aynı ekrandan kaydedilebilir.", cancellationToken: cancellationToken)),
                        FaqNode(
                            await _localization.TranslateAsync("Masaüstü ve mobil veriler nasıl güncel kalır?", cancellationToken: cancellationToken),
                            await _localization.TranslateAsync("Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.", cancellationToken: cancellationToken))
                    }
                });
            }

            if (string.Equals(page.Slug, "ucretsiz-veresiye-programi", StringComparison.OrdinalIgnoreCase))
            {
                graph.Add(new Dictionary<string, object?>
                {
                    ["@type"] = "VideoObject",
                    ["name"] = await _localization.TranslateAsync("Ücretsiz Veresiye Programı Kurulum ve Kullanım | NSX Veresiye Defteri", cancellationToken: cancellationToken),
                    ["description"] = await _localization.TranslateAsync("NSX Ücretsiz Veresiye Takip Pro kurulum ve temel kullanım videosu. Güncel v1.0.5 sürümünde ayrıca QR ile Bulut ve Mobil Yönetim, canlı senkronizasyon, mobil müşteri arama, cari hareket, borç ve tahsilat özellikleri bulunur.", cancellationToken: cancellationToken),
                    ["thumbnailUrl"] = "https://img.youtube.com/vi/ypsXQY6iOl4/maxresdefault.jpg",
                    ["uploadDate"] = "2026-07-04",
                    ["duration"] = "PT6M33S",
                    ["embedUrl"] = "https://www.youtube.com/embed/ypsXQY6iOl4",
                    ["contentUrl"] = "https://www.youtube.com/watch?v=ypsXQY6iOl4",
                    ["inLanguage"] = languageCode,
                    ["publisher"] = PublisherNode()
                });
            }

            var data = new Dictionary<string, object?>
            {
                ["@context"] = "https://schema.org",
                ["@graph"] = graph
            };

            return JsonLdScript(data);
        }

        private static Dictionary<string, object?> PublisherNode()
        {
            return new Dictionary<string, object?>
            {
                ["@type"] = "Organization",
                ["name"] = "NSX Yazılım",
                ["url"] = SeoTextHelper.Domain,
                ["logo"] = SeoTextHelper.AbsoluteUrl("/nsx-assets/logo-brand.webp")
            };
        }

        private static Dictionary<string, object?> ImageNode(string url, string alt, int width, int height)
        {
            return new Dictionary<string, object?>
            {
                ["@type"] = "ImageObject",
                ["url"] = url,
                ["contentUrl"] = url,
                ["caption"] = alt,
                ["width"] = width,
                ["height"] = height
            };
        }

        private static string GetBlogImageAlt(string slug, string h1)
        {
            return IsVeresiyeSlug(slug)
                ? "Ücretsiz veresiye programı NSX Veresiye Takip Pro"
                : $"{h1} için NSX Yazılım ürün görseli";
        }

        private static string GetBlogImageUrl(string slug)
        {
            var imagePath = slug switch
            {
                // Kritik ürünler: logoya düşmeden doğrudan gerçek ürün görselleri kullanılır.
                "nsx-cari-takip-pro-bulut-programi" => "/uploads/products/images/e050d33520254e0294c285d117e599bb.webp",
                "nsx-veri-kurtarma-pro-programi" => "/uploads/products/images/6ae59fa05cf74a2d838c6b45e20861e7.webp",
                "nsx-barkodlu-satis-cari-stok-takip-pro" => "/uploads/products/images/558e3890d43b4521be3ca61cddd9fa8e.webp",
                "nsx-security-auditor-pro-kod-guvenlik-analizi" => "/uploads/products/images/d93b6ddeb9084da9ae908d1c97867e90.webp",
                "nsx-cari-takip-pro-programi" => "/uploads/products/images/fb16cbb7d5a044dd9799b8df7a2034f5.webp",
                "nsx-teknik-servis-pro-programi" => "/uploads/products/images/bf09a85908c74894a33e726e401a7f7d.webp",
                "nsx-okul-plan-pro-ders-programi" => "/uploads/products/images/9c94799890654d01a36fc43962d6f95a.webp",

                // Ücretsiz veresiye ve veresiye/cari rehberleri.
                "ucretsiz-veresiye-programi" or
                "veresiye-programi-nedir" or
                "ucretsiz-veresiye-programi-nedir" or
                "ucretsiz-cari-takip-programi" or
                "esnaflar-icin-veresiye-takip-programi" or
                "borc-alacak-takip-programi-ucretsiz" or
                "bakkal-veresiye-defteri-nasil-tutulur" or
                "cari-hesap-takibi-nasil-yapilir" or
                "excel-yerine-veresiye-takip-programi" or
                "veresiye-defteri-programi" or
                "ucretsiz-cari-hesap-programi" => "/uploads/products/images/faea5340818d44669793617cb9c160e9.webp",

                "veresiye-cari-takip-programi-pro" => "/uploads/products/images/faea5340818d44669793617cb9c160e9.webp",
                "nsx-komisyonlu-cari-takip-pro" => "/uploads/products/images/72459ff43fa04b7f82a5fe30ecd8acce.webp",

                // Servis, otomotiv, klinik, organizasyon ve performans ürünleri.
                "ucretsiz-teknik-servis-programi" or "teknik-servis-takip-programi" => "/uploads/products/images/415d6a20d9654d198e7601523c1d1389.webp",
                "oto-tamir-programi" or "oto-tamir-servis-yonetim-programi" => "/uploads/products/images/98a1dc7897e24f6ca41ebb1a0ce4ea65.webp",
                "oto-galeri-programi" or "oto-galeri-yonetim-programi" => "/uploads/products/images/9f5bac23e3e14bd4a786a368a04001a5.webp",
                "servis-live-programi" or "nsx-servispro-live-teknik-servis-programi" => "/uploads/products/images/9b1c5c8989054c57af25e69d3e06945c.webp",
                "klinik-programi" or "randevu-programi" or "klinik-randevu-takip-programi" => "/uploads/products/images/d92cf46621124c17bd7d9e474d9f43c8.webp",
                "dugun-salonu-yonetim-programi" => "/uploads/products/images/db533085b8a842a7aa54796251eb98b4.webp",
                "ucretsiz-bilgisayar-hizlandirma-programi" or
                "bilgisayar-hizlandirma-programi" or
                "oyun-hizlandirma-fps-artirma-programi" => "/uploads/products/images/3934667da2f34c98a713967931e5b158.webp",

                // Bilinmeyen yeni bir blog kaydı oluşursa marka logosu yerine genel ürün kutusu gösterilir.
                _ => "/assets/img/01_veresiye_kutu.webp"
            };

            return SeoTextHelper.AbsoluteUrl(imagePath);
        }

        private static (int Width, int Height) GetBlogImageSize(string slug)
        {
            return slug switch
            {
                "nsx-cari-takip-pro-bulut-programi" or "nsx-veri-kurtarma-pro-programi" => (1580, 996),
                "ucretsiz-teknik-servis-programi" or "teknik-servis-takip-programi" or
                "ucretsiz-bilgisayar-hizlandirma-programi" or "bilgisayar-hizlandirma-programi" or "oyun-hizlandirma-fps-artirma-programi" => (1580, 996),
                "nsx-komisyonlu-cari-takip-pro" => (1000, 600),
                _ => (1581, 995)
            };
        }

        private static Dictionary<string, object?> BuildBreadcrumb(
            string currentName,
            string currentPath,
            bool isLanding,
            string homeName,
            string pathPrefix = "")
        {
            var items = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["@type"] = "ListItem",
                    ["position"] = 1,
                    ["name"] = homeName,
                    ["item"] = SeoTextHelper.AbsoluteUrl(string.IsNullOrWhiteSpace(pathPrefix) ? "/" : pathPrefix)
                }
            };

            if (!isLanding)
            {
                items.Add(new Dictionary<string, object?>
                {
                    ["@type"] = "ListItem",
                    ["position"] = 2,
                    ["name"] = "Blog",
                    ["item"] = SeoTextHelper.AbsoluteUrl($"{pathPrefix}/blog")
                });
            }

            items.Add(new Dictionary<string, object?>
            {
                ["@type"] = "ListItem",
                ["position"] = isLanding ? 2 : 3,
                ["name"] = currentName,
                ["item"] = SeoTextHelper.AbsoluteUrl(pathPrefix + currentPath)
            });

            return new Dictionary<string, object?>
            {
                ["@type"] = "BreadcrumbList",
                ["itemListElement"] = items
            };
        }

        private static Dictionary<string, object?> FaqNode(string question, string answer)
        {
            return new Dictionary<string, object?>
            {
                ["@type"] = "Question",
                ["name"] = question,
                ["acceptedAnswer"] = new Dictionary<string, object?>
                {
                    ["@type"] = "Answer",
                    ["text"] = answer
                }
            };
        }

        private static string JsonLdScript(object jsonLdData)
        {
            var json = JsonSerializer.Serialize(jsonLdData, JsonLdOptions);
            return $"<script type=\"application/ld+json\">{json}</script>";
        }
    }
}
