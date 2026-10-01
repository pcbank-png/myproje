using NSYazilim.Web.Controllers;

namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Curated local English translation memory for BlogController.Pages.
    /// No network provider is used: source strings are paired with the built-in
    /// English guide catalog and stored through the existing localization engine.
    /// </summary>
    public static class LocalizationEnglishGuideSeedData
    {
        public sealed record EnglishSection(string Heading, string Content);
        public sealed record EnglishFaq(string Question, string Answer);
        public sealed record EnglishPage(
            string Slug,
            string Title,
            string Description,
            string H1,
            string Body,
            string[] Keywords,
            string? CtaText = null,
            EnglishSection[]? Sections = null,
            EnglishFaq[]? Faqs = null);

        private static IReadOnlyList<LocalizationSeedItem> BuildItems()
        {
            var sourceBySlug = BlogController.Pages.ToDictionary(x => x.Slug, StringComparer.OrdinalIgnoreCase);
            var output = new Dictionary<string, LocalizationSeedItem>(StringComparer.Ordinal);

            foreach (var english in EnglishPages)
            {
                if (!sourceBySlug.TryGetValue(english.Slug, out var source))
                    continue;

                var area = "/" + source.Slug;
                Add(output, source.Title, english.Title, area);
                Add(output, source.Description, english.Description, area);
                Add(output, source.H1, english.H1, area);
                Add(output, source.Body, english.Body, area);

                PairArray(output, source.Keywords, english.Keywords, area);
                if (!string.IsNullOrWhiteSpace(source.CtaText))
                    Add(output, source.CtaText, english.CtaText, area);

                PairSections(output, source.Sections, english.Sections, area);
                PairFaqs(output, source.Faqs, english.Faqs, area);
            }

            AddGuideShellItems(output);
            return output.Values.ToArray();
        }

        private static void AddGuideShellItems(IDictionary<string, LocalizationSeedItem> output)
        {
            const string detail = "/Blog/Detail";
            const string index = "/Blog";

            // Detail shell. These strings are intentionally part of the Guide catalog so
            // every guide page uses the same reviewed local English memory.
            Add(output, "NSX Yazılım Çözümü", "NSX Software Solution", detail);
            Add(output, "NSX Yazılım Rehberi", "NSX Software Guide", detail);
            Add(output, "Yazı bilgileri", "Article information", detail);
            Add(output, "Hızlı Ürün Erişimi", "Quick Product Access", detail);
            Add(output, "Bu sayfadan direkt indirme işlemine başlayabilir veya ürün detay sayfasına gidebilirsiniz.", "You can start the download directly from this page or open the product details page.", detail);
            Add(output, "Hemen İncele", "Explore Now", detail);
            Add(output, "Detay Sayfasına Git", "View Product Details", detail);
            Add(output, "Ürün Sayfasına Git", "Open Product Page", detail);
            Add(output, "Demo İndir", "Download Demo", detail);
            Add(output, "Hemen Ücretsiz İndir", "Download Free Now", detail);
            Add(output, "Ürünleri İncele", "Explore Products", detail);
            Add(output, "Kurulum Videosu + Güncel Sürüm", "Installation Video + Current Version", detail);
            Add(output, "NSX Veresiye Takip Pro Kurulum ve Kullanım Videosu", "NSX Veresiye Takip Pro Installation & User Guide Video", detail);
            Add(output, "Videoda masaüstü programının kurulum ve temel kullanım adımlarını izleyebilirsiniz. Güncel v1.0.5 sürümünde bunlara ek olarak QR ile Bulut ve Mobil Yönetim, canlı senkronizasyon, mobil müşteri arama, cari hareket, borç ve tahsilat özellikleri bulunur.", "The video covers the desktop application's installation and core usage steps. The current v1.0.5 release also includes QR-based Cloud & Mobile Management, live synchronization, mobile customer search, account activity, debt and collection features.", detail);
            Add(output, "Programı Ücretsiz İndir", "Download Free", detail);
            Add(output, "v1.0.5 güncel sürüm notu:", "v1.0.5 current release note:", detail);
            Add(output, "Video temel masaüstü akışını anlatır. Yeni Bulut ve Mobil sistemde Cloud Bağlantısı ekranından QR oluşturabilir, telefondan müşteri arayabilir, cari hareketleri görebilir, borç ve tahsilat kaydedebilirsiniz.", "The video explains the core desktop workflow. In the new Cloud & Mobile system, you can generate a QR code from the Cloud Connection screen, search customers from your phone, view account activity, and record debt and collection transactions.", detail);
            Add(output, "NSX Veresiye Takip Pro’yu ücretsiz indirin; v1.0.5 Bulut ve Mobil Yönetim özelliklerini kullanmaya başlayın.", "Download NSX Veresiye Takip Pro for free and start using the v1.0.5 Cloud & Mobile Management features.", detail);
            Add(output, "Merak Edilenler", "Frequently Asked", detail);
            Add(output, "Sıkça Sorulan Sorular", "Frequently Asked Questions", detail);
            Add(output, "NSX Veresiye Takip Pro ücretsiz mi?", "Is NSX Veresiye Takip Pro free?", detail);
            Add(output, "Evet. Program ücretsiz kullanım için sunulur; güncel v1.0.5 sürümünde masaüstü cari takibe ek olarak Bulut ve Mobil Yönetim özellikleri bulunur.", "Yes. The software is available free of charge, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking.", detail);
            Add(output, "Bakkal ve marketler için uygun mu?", "Is it suitable for grocery stores and markets?", detail);
            Add(output, "Evet. Bakkal, market, bayi ve küçük işletmeler müşteri cari hesaplarını masaüstünden ve QR ile bağlanan mobil panelden takip edebilir.", "Yes. Grocery stores, markets, dealers and small businesses can track customer accounts from the desktop application and the QR-connected mobile panel.", detail);
            Add(output, "Cari hesap telefondan yönetilebilir mi?", "Can customer accounts be managed from a phone?", detail);
            Add(output, "Evet. Mobil panelde müşteri aranır; cari hareketleri açılır ve borç veya tahsilat aynı müşteri hesabından kaydedilebilir.", "Yes. You can search for a customer in the mobile panel, open account activity, and record debt or collection transactions under the same customer account.", detail);
            Add(output, "Masaüstü ve mobil veriler nasıl güncel kalır?", "How do desktop and mobile data stay in sync?", detail);
            Add(output, "Canlı senkronizasyon, masaüstü ve mobil tarafta yapılan cari değişikliklerin iki tarafta güncel tutulmasına yardımcı olur.", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.", detail);
            Add(output, "Kimler için uygundur?", "Who is it for?", detail);
            Add(output, "Bakkal, market, bayi, esnaf ve müşteri bazlı borç-tahsilat takibi yapan küçük ve orta ölçekli işletmeler için uygundur. Masaüstünde çalışmaya devam ederken gerektiğinde cep telefonundan cari hesaplara erişmek isteyen işletmelere sade bir Bulut ve Mobil kullanım sunar.", "It is suitable for small and medium-sized businesses such as grocery stores, markets, dealers and tradespeople that track customer-based debt and collections. It provides simple Cloud & Mobile access while preserving the desktop workflow.", detail);
            Add(output, "NSX Veresiye v1.0.5 yaklaşımı", "The NSX Veresiye v1.0.5 Approach", detail);
            Add(output, "Masaüstü hızını koruyup mobil tarafta yalnızca günlük ihtiyaçlara odaklanır: toplam borç, toplam tahsilat, açık bakiye, müşteri arama, cari hareketler, borç ve tahsilat. Mevcut veriler korunarak Bulut ve Mobil erişim mevcut programa eklenir.", "It preserves desktop speed while keeping the mobile experience focused on everyday needs: total debt, total collections, outstanding balance, customer search, account activity, debt and collections. Cloud & Mobile access is added while preserving existing data.", detail);
            Add(output, "İşini daha düzenli takip etmek, lisanslı veya ücretsiz yazılım kullanmak ve süreçlerini dijitalleştirmek isteyen işletmeler için uygundur.", "It is suitable for businesses that want to manage their work more systematically, use licensed or free software, and digitize their processes.", detail);
            Add(output, "NSX Yazılım yaklaşımı", "The NSX Software Approach", detail);
            Add(output, "NSX Yazılım; hızlı kullanım, sade arayüz, güvenli dijital teslimat, güncelleme desteği ve destek odaklı çözümler geliştirmeyi hedefler.", "NSX Software focuses on fast workflows, clear interfaces, secure digital delivery, update support and support-oriented solutions.", detail);
            Add(output, "NSX Veresiye Programı Ürün Sayfası", "NSX Veresiye Product Page", detail);
            Add(output, "İletişime Geç", "Contact Us", detail);
            Add(output, "Rehber Bilgisi", "Guide Information", detail);
            Add(output, "İş süreçlerinizi daha düzenli yönetmenize yardımcı olacak sade ve uygulanabilir bilgiler.", "Clear, practical information to help you manage your business processes more systematically.", detail);
            Add(output, "Yayın", "Published", detail);
            Add(output, "Güncelleme", "Update", detail);
            Add(output, "Yayıncı", "Publisher", detail);
            Add(output, "Tüm rehberleri görüntüle", "View All Guides", detail);
            Add(output, "Konu Başlıkları", "Topics", detail);
            Add(output, "İlgili Rehberler", "Related Guides", detail);
            Add(output, "Bu konuyla ilgili diğer sayfalar", "Other Pages Related to This Topic", detail);
            Add(output, "Tüm rehberler", "All Guides", detail);
            Add(output, "NSX Rehber", "NSX Guides", detail);

            // Guide index shell.
            Add(output, "NSX Bilgi Merkezi", "NSX Knowledge Center", index);
            Add(output, "İşinizi büyüten", "Built to Grow Your Business", index);
            Add(output, "uygulanabilir rehberler.", "practical guides.", index);
            Add(output, "Yazılım seçimi, günlük operasyonlar ve dijital iş süreçleri için sade, güvenilir ve doğrudan uygulanabilir içerikler.", "Clear, reliable and actionable content for software selection, daily operations and digital business processes.", index);
            Add(output, "güncel rehber", "current guides", index);
            Add(output, "uzmanlık alanı", "areas of expertise", index);
            Add(output, "saha deneyimi", "field experience", index);
            Add(output, "Öne çıkan rehber", "Featured Guide", index);
            Add(output, "Rehberi incele", "Explore Guide", index);
            Add(output, "Rehber arşivi", "Guide Archive", index);
            Add(output, "İhtiyacınıza göre keşfedin", "Explore by Your Needs", index);
            Add(output, "Tüm Rehberler", "All Guides", index);
            Add(output, "Okumaya devam et", "Continue Reading", index);
            Add(output, "Yeni rehberler hazırlanıyor.", "New guides are being prepared.", index);
            Add(output, "Bu sırada NSX yazılımlarını inceleyebilirsiniz.", "In the meantime, you can explore NSX software.", index);
            Add(output, "Yazılımları keşfet", "Explore Software", index);
            Add(output, "Doğru çözümü birlikte seçelim", "Let's Choose the Right Solution Together", index);
            Add(output, "İşletmeniz için hangi yazılımın uygun olduğundan emin değil misiniz?", "Not sure which software is right for your business?", index);
            Add(output, "İhtiyacınızı anlatın; size uygun NSX ürününü ve kullanım yolunu birlikte netleştirelim.", "Tell us what you need and we'll help you identify the right NSX product and the best way to use it.", index);
            Add(output, "Uzman desteği alın", "Get Expert Support", index);
            Add(output, "Tüm yazılımlar", "All Software", index);

            // Guide index categories. The previous captured package had literal or weak
            // translations such as "Cari and Veresiye" and "Finance and Case".
            Add(output, "Cari ve Veresiye", "Customer Accounts & Receivables", index);
            Add(output, "Otomotiv", "Automotive", index);
            Add(output, "Servis Yönetimi", "Service Management", index);
            Add(output, "İşletme Yönetimi", "Business Management", index);
            Add(output, "Satış ve Stok", "Sales & Inventory", index);
            Add(output, "Finans ve Kasa", "Finance & Cash Management", index);
            Add(output, "Yazılım Güvenliği", "Software Security", index);
            Add(output, "Teknoloji", "Technology", index);
            Add(output, "Dijital Dönüşüm", "Digital Transformation", index);

            // Blog meta / structured data.
            Add(output, "NSX Yazılım Blog | İşletme ve Yazılım Rehberleri", "NSX Software Blog | Business & Software Guides", index);
            Add(output, "NSX Okul Plan Pro, Kasa Defteri Pro, Security Auditor Pro, ücretsiz veresiye ve barkodlu satış başta olmak üzere eğitim, kasa, cari hesap, teknik servis, otomotiv ve işletme yazılımları için kapsamlı NSX rehberleri.", "Comprehensive NSX guides for school planning, cash management, security auditing, free receivables tracking, barcode sales, customer accounts, technical service, automotive and business software.", index);
            Add(output, "yazılım rehberleri", "software guides", index);
            Add(output, "işletme yazılımı rehberi", "business software guide", index);
            Add(output, "program kullanım rehberi", "software user guide", index);
            Add(output, "NSX Yazılım işletme ve yazılım rehberleri", "NSX Software business and software guides", index);
            Add(output, "NSX Yazılım Blog", "NSX Software Blog", index);
            Add(output, "Kasa defteri, gelir-gider, ücretsiz veresiye, barkodlu satış, cari ve stok takip, teknik servis, oto tamir, oto galeri, klinik, organizasyon ve performans programları için rehber içerikler.", "Guides for cash books, income and expense tracking, free receivables, barcode sales, customer accounts and inventory, technical service, auto repair, car dealerships, clinics, organization management and performance software.", index);
            Add(output, "NSX Yazılım Rehberleri", "NSX Software Guides", index);
            Add(output, "Ana Sayfa", "Home", index);

            // Image alt and structured-data-only guide strings.
            Add(output, "Ücretsiz veresiye programı NSX Veresiye Takip Pro", "NSX Veresiye Takip Pro free receivables software", detail);
            Add(output, "NSX Yazılım ürün görseli", "NSX Software product image", detail);
            Add(output, "Cari hesap takibi telefondan yapılır mı?", "Can customer accounts be tracked from a phone?", detail);
            Add(output, "Evet. Ücretsiz kullanım için sunulur ve güncel v1.0.5 sürümünde masaüstü cari takibe ek olarak Bulut ve Mobil Yönetim özellikleri bulunur.", "Yes. It is available free of charge, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking.", detail);
            Add(output, "Evet. Bakkal, market, bayi ve küçük işletmeler müşteri carilerini masaüstünden takip edebilir; QR bağlantısıyla mobil panelden de müşteri, borç, tahsilat ve açık bakiye bilgilerine erişebilir.", "Yes. Grocery stores, markets, dealers and small businesses can track customer accounts on desktop and access customer, debt, collection and outstanding-balance information from the QR-connected mobile panel.", detail);
            Add(output, "Evet. Mobil panelde müşteri aranıp seçildiğinde cari hareketleri açılır; yeni borç veya tahsilat aynı ekrandan kaydedilebilir.", "Yes. After searching for and selecting a customer in the mobile panel, you can open account activity and record a new debt or collection transaction from the same screen.", detail);
            Add(output, "Ücretsiz Veresiye Programı Kurulum ve Kullanım | NSX Veresiye Defteri", "Free Receivables Software Installation & User Guide | NSX Veresiye Takip Pro", detail);
            Add(output, "NSX Ücretsiz Veresiye Takip Pro kurulum ve temel kullanım videosu. Güncel v1.0.5 sürümünde ayrıca QR ile Bulut ve Mobil Yönetim, canlı senkronizasyon, mobil müşteri arama, cari hareket, borç ve tahsilat özellikleri bulunur.", "Installation and core usage video for NSX Veresiye Takip Pro. The current v1.0.5 release also includes QR-based Cloud & Mobile Management, live synchronization, mobile customer search, account activity, debt and collection features.", detail);
        }

        private static void PairArray(
            IDictionary<string, LocalizationSeedItem> output,
            IReadOnlyList<string>? source,
            IReadOnlyList<string>? english,
            string area)
        {
            if (source == null || english == null)
                return;

            var count = Math.Min(source.Count, english.Count);
            for (var i = 0; i < count; i++)
                Add(output, source[i], english[i], area);
        }

        private static void PairSections(
            IDictionary<string, LocalizationSeedItem> output,
            IReadOnlyList<BlogController.SeoSection>? source,
            IReadOnlyList<EnglishSection>? english,
            string area)
        {
            if (source == null || english == null)
                return;

            var count = Math.Min(source.Count, english.Count);
            for (var i = 0; i < count; i++)
            {
                Add(output, source[i].Heading, english[i].Heading, area);
                Add(output, source[i].Content, english[i].Content, area);
            }
        }

        private static void PairFaqs(
            IDictionary<string, LocalizationSeedItem> output,
            IReadOnlyList<BlogController.SeoFaq>? source,
            IReadOnlyList<EnglishFaq>? english,
            string area)
        {
            if (source == null || english == null)
                return;

            var count = Math.Min(source.Count, english.Count);
            for (var i = 0; i < count; i++)
            {
                Add(output, source[i].Question, english[i].Question, area);
                Add(output, source[i].Answer, english[i].Answer, area);
            }
        }

        private static void Add(
            IDictionary<string, LocalizationSeedItem> output,
            string? source,
            string? english,
            string area)
        {
            source = LocalizationTextKey.Normalize(source);
            english = LocalizationTextKey.Normalize(english);
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(english))
                return;

            var key = LocalizationTextKey.Create(source);
            if (!output.ContainsKey(key))
                output[key] = new LocalizationSeedItem(source, english, area);
        }

        public static readonly IReadOnlyList<EnglishPage> EnglishPages = new EnglishPage[]
        {
            new(
                "ucretsiz-veresiye-programi",
                "Free Accounts Receivable Software | Cloud & Mobile Customer Account Tracking",
                "Manage customer accounts, balances owed and collections for free from desktop and mobile with NSX Veresiye Takip Pro and its Cloud system.",
                "Free Accounts Receivable Software",
                "NSX Veresiye Takip Pro is free receivables tracking software designed for tradespeople and businesses that need to keep customer accounts, amounts owed, collections, outstanding balances and transaction history organized. The desktop application is built for fast day-to-day use, while the Cloud & Mobile Management System introduced in v1.0.5 brings essential customer account operations to your phone.\n\nScan the secure QR code generated on the Cloud Connection screen to connect your phone to the business mobile panel. The mobile dashboard displays Total Debt, Total Collections and Outstanding Balance summaries. A live customer search is available below; selecting a customer opens the account activity so previous debt and collection entries can be reviewed on one screen.\n\nNew debt or collection transactions can be recorded directly from the selected customer's account. Changes made on desktop or mobile are transferred to the other side through live synchronization. This allows the business owner to check a customer's balance and record the required account transaction from a mobile device even when away from the workplace.\n\nYou can upgrade to the current version while preserving existing company, customer, debt and collection records. The familiar desktop workflow remains in place, while Cloud & Mobile access makes receivables tracking more accessible and practical.",
                new[]
                {
                    "free accounts receivable software", "cloud receivables software", "mobile receivables software", "receivables tracking from phone", "customer account tracking software", "debt and receivables tracking software", "collection tracking software", "QR customer account tracking", "live synchronization", "NSX Veresiye Takip Pro"
                },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("v1.0.5 Cloud & Mobile Management", "The current version adds the Cloud & Mobile Management System to the desktop application. From an authorized phone connected by QR, you can search customers, view account activity, and record debt and collection transactions."),
                    new EnglishSection("Simple mobile workflow", "The mobile panel focuses only on everyday account tasks: business summaries, customer search, customer account activity, debt entry and collections. Unnecessary menus do not get in the way."),
                    new EnglishSection("Live synchronization", "Changes made on desktop and mobile are synchronized. Total debt, total collections and outstanding balance remain current based on the same customer account activity."),
                    new EnglishSection("Your existing data is preserved", "The update is designed to add new Cloud & Mobile features on top of existing customer and account records while preserving the current workflow.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "ucretsiz-teknik-servis-programi",
                "Free Repair Shop Management Software | Service & Device Tracking",
                "Use free repair shop management software to organize customers, device intake, service status, job history and delivery workflows.",
                "Free Repair Shop Management Software",
                "Free repair shop management software is used by computer, phone, electronics and similar service businesses to track device intake, fault reports, customer details, repair status, service history, delivery and payments. The NSX service tracking solution is designed for businesses that want to manage repair volume more systematically without losing records. The current stage of each device, work performed for the customer and previous records can all be monitored from one place.",
                new[] { "free repair shop management software", "repair shop management software", "service management software", "device tracking software" },
                "Explore Repair Shop Software"),
            new(
                "oto-tamir-programi",
                "Auto Repair Shop Software | Vehicle Intake, Service & Customer Tracking",
                "Manage vehicle intake, customer details, repair jobs, payments and delivery workflows more systematically with auto repair shop software.",
                "Auto Repair Shop Software",
                "Auto repair shop software is designed for garages that want to manage vehicle intake, customer details, license plate and vehicle records, repair jobs, payment tracking and delivery workflows more systematically. NSX Software solutions aim to simplify daily workflows in auto repair businesses, prevent records from being lost and make service status easier to track.",
                new[] { "auto repair shop software", "auto repair shop software", "automotive service software", "vehicle service tracking software" },
                "Explore Auto Repair Software"),
            new(
                "oto-galeri-programi",
                "Car Dealership Software | Vehicle Inventory, Customer & Sales Tracking",
                "Track vehicle inventory, customers, sales, payments and dealership operations from one place with car dealership software.",
                "Car Dealership Software",
                "Car dealership software is designed to organize vehicle inventory, customer interactions, sales processes, payment status and other dealership tracking needs. The NSX Software approach helps dealerships manage vehicle information, customer records and sales workflows in a clear, easy-to-use system.",
                new[] { "car dealership software", "dealership management software", "vehicle tracking software", "vehicle sales tracking software" },
                "Explore Car Dealership Software"),
            new(
                "servis-live-programi",
                "Service Live Software | Real-Time Service & Work Order Tracking",
                "Track service operations in real time and manage customers, work orders, status and delivery workflows more efficiently with Service Live software.",
                "Service Live Software",
                "Service Live software is designed to help service businesses manage work orders, status tracking, customer updates, delivery workflows and day-to-day service operations in a more immediate and organized way. NSX Software solutions aim to make service records faster to access and easier to follow across the business.",
                new[] { "service live software", "auto service live software", "real-time service tracking software", "service management software" },
                "Explore Service Live Software"),
            new(
                "ucretsiz-bilgisayar-hizlandirma-programi",
                "Free PC Optimization Software | Windows & Gaming Performance",
                "Explore free PC optimization software for Windows maintenance, gaming performance, FPS improvements and system optimization.",
                "Free PC Optimization Software",
                "Free PC optimization software is designed for users who want smoother Windows performance, lower unnecessary system load, better gaming performance and practical maintenance focused on improving FPS. NSX Turbo aims to provide PC optimization, game optimization, startup load reduction and system tuning in a simple, streamlined experience.",
                new[] { "free PC optimization software", "free computer optimization software", "game booster software", "FPS optimization software", "NSX Turbo" },
                "Explore NSX Turbo Products"),
            new(
                "klinik-programi",
                "Clinic Management Software | Patient, Doctor & Clinic Tracking",
                "Manage patient records, doctors, appointments, customer accounts and clinic workflows from one place with clinic management software.",
                "Clinic Management Software",
                "Clinic management software is designed for businesses that want to organize patient records, doctor information, appointments, examination notes, account transactions and daily clinic workflows. The NSX Clinic approach brings patient search, doctor search, appointment tracking, payments and transaction history together in a clear, fast and easy-to-use panel. It provides structured tracking for clinics, private practices, beauty centers and other appointment-based businesses.",
                new[] { "clinic management software", "patient management software", "doctor management software", "clinic tracking software" },
                "Contact Us for Clinic Solutions"),
            new(
                "randevu-programi",
                "Appointment Scheduling Software | Clinics & Businesses",
                "Manage patients, customers, doctors, staff and daily appointment schedules more systematically with appointment scheduling software.",
                "Appointment Scheduling Software",
                "Appointment scheduling software is designed for businesses that need to manage doctor, staff, customer or patient appointments on a daily, weekly and monthly basis. Whether used for clinic appointments, beauty center scheduling or service appointments, it helps reduce conflicts and keep planning organized. NSX Software solutions aim to make appointment management easier with clear screens and fast search.",
                new[] { "appointment scheduling software", "clinic appointment software", "patient appointment software", "appointment tracking software" },
                "Contact Us for Appointment Solutions"),
            new(
                "nsx-komisyonlu-cari-takip-pro",
                "NSX Komisyonlu Cari Takip Pro | Client-Server Account & Commission Tracking",
                "Centrally manage customer accounts, collections, commissions and reporting with the client-server architecture of NSX Komisyonlu Cari Takip Pro.",
                "NSX Komisyonlu Cari Takip Pro",
                "NSX Komisyonlu Cari Takip Pro is professional client-server customer account software developed for businesses that need to manage accounts, collections, commissions and reporting from one central system. Dealers, intermediaries, commission-based businesses, wholesalers and companies with field sales teams can track customer balances, debt and receivables, commission records and collection activity more systematically.\n\nWith its Server & Client architecture, the Server application is installed on the main computer and Client applications on other computers connect to the same system and work with shared data. This avoids maintaining separate records on each computer and keeps information centrally managed. Multi-user support allows team members to work in the same system in an orderly way.\n\nYou can create customer account cards, track balances, record collections, manage commission information and review the overall business position through reports. Collections by customer, total balances, commission summaries and historical account activity are easier to see. It is a strong alternative for businesses that want to replace manual ledgers or scattered Excel files with a single system for account and commission management.\n\nKey capabilities include customer account tracking, commission management, collection records, Server & Client operation, multi-user support, centralized data management, detailed reporting, automatic backup support, a user-friendly interface and Windows compatibility. Visit the NSX Komisyonlu Cari Takip Pro product page to review licensing options and start the demo download.",
                new[] { "NSX Komisyonlu Cari Takip Pro", "commission account tracking software", "commission tracking software", "client-server customer account software", "customer account tracking software", "collection tracking software", "commission management", "multi-user account tracking", "dealer account tracking software", "commission collection tracking software" },
                "NSX Komisyonlu Cari Takip Pro Product Page"),
            new(
                "veresiye-programi-nedir",
                "What Is Receivables Tracking Software? | Cloud & Mobile Customer Account Guide",
                "What is receivables tracking software? Learn how cloud and mobile customer accounts, QR connection, live synchronization, debt, collections and outstanding balances are managed.",
                "What Is Receivables Tracking Software?",
                "Receivables tracking software is a digital customer account system that helps businesses keep amounts owed, collections, account activity and remaining balances organized for each customer. Instead of relying on paper ledgers or scattered Excel files, each customer has an individual account so historical transactions can be found more quickly.\n\nNSX Veresiye Takip Pro v1.0.5 adds the Cloud & Mobile Management System to its desktop workflow. A secure QR code is generated from the Cloud Connection screen and used to connect a mobile phone. The mobile panel shows Total Debt, Total Collections and Outstanding Balance summaries; customers can be searched by name, and tapping a customer opens the account activity.\n\nPrevious debt and collection records are displayed on the same customer account screen. New debt or collection transactions can also be recorded there. Live synchronization keeps the desktop and mobile sides working with the same customer account data.\n\nThis is especially useful for grocery stores, markets, dealers, small businesses and tradespeople who sell on account and need to check customer balances while away from the workplace. It combines the speed of desktop software with the convenience of mobile access.",
                new[] { "what is receivables tracking software", "free accounts receivable software", "cloud receivables software", "mobile receivables software", "receivables tracking from phone", "customer account tracking software", "debt and receivables tracking software", "collection tracking software", "QR customer account tracking", "live synchronization" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("What does cloud receivables software provide?", "It allows the business to view customer accounts and perform essential transactions not only from the main computer but also from an authorized phone."),
                    new EnglishSection("Customer accounts and collections from your phone", "Search for a customer, open the account, review previous activity, and record a debt or collection transaction from the same screen."),
                    new EnglishSection("Mobile connection by QR code", "The QR code generated on the Cloud Connection screen links the mobile session to the relevant business Cloud account."),
                    new EnglishSection("Why is live synchronization important?", "Customer account changes are kept current on both sides to prevent different balances from appearing on desktop and mobile.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "ucretsiz-veresiye-programi-nedir",
                "What Is Free Receivables Software? | Cloud & Mobile Customer Accounts",
                "What is free receivables software? Learn how to manage customer accounts, debt and collections from desktop and phone with NSX Veresiye Takip Pro.",
                "What Is Free Receivables Software?",
                "Free receivables software lets businesses track customer debt, collections, payment history and outstanding balances digitally without requiring a paid license. The goal is to keep transactions that can easily be lost in a paper ledger organized in customer-based account records.\n\nNSX Veresiye Takip Pro extends its free-use model in v1.0.5 with Cloud & Mobile Management. While customer and account transactions continue on desktop, a phone can connect by QR through the Cloud Connection screen. The mobile side displays total debt, total collections and outstanding balance for the business.\n\nType a name in customer search to find the relevant account quickly. Tapping the customer opens previous debt and collection activity; a new debt or collection can be recorded from the same account screen. Live synchronization transfers changes back to the desktop.\n\nThis turns free receivables tracking from a record kept on one computer into a modern system that lets the business owner check daily customer accounts from a mobile phone whenever needed.",
                new[] { "what is free receivables software", "free accounts receivable software", "cloud receivables software", "mobile customer account tracking", "customer debt tracking software", "debt tracking from phone", "customer account tracking software" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("Cloud & Mobile use in the free software", "The current NSX Veresiye release includes QR-connected mobile management. Essential customer account transactions can also be handled from a phone."),
                    new EnglishSection("What is available in the mobile panel?", "Total Debt, Total Collections, Outstanding Balance, customer search, account activity, debt entry and collection transactions are all available."),
                    new EnglishSection("Who is it for?", "It provides a simple workflow for grocery stores, markets, dealers, small businesses and tradespeople that sell to customers on account.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "ucretsiz-cari-takip-programi",
                "Free Customer Account Tracking Software | Cloud & Mobile",
                "Track customer debt, collections and outstanding balances live from desktop and the mobile Cloud panel with free customer account tracking software.",
                "Free Customer Account Tracking Software",
                "Free customer account tracking software makes it easier to see current outstanding balances by recording each customer's debt and collection activity in an organized way. As the number of customers grows, paper ledgers or Excel can make it harder to find previous transactions and maintain the correct balance.\n\nWith NSX Veresiye Takip Pro v1.0.5, customer account tracking extends beyond the desktop to the Cloud & Mobile system. After QR connection, the simple mobile home screen displays Total Debt, Total Collections and Outstanding Balance. Customer search lets you find the relevant account within seconds.\n\nSelecting a customer opens the account activity. Debts and collections are shown chronologically, and a new debt or collection can be added directly to the selected customer account. Live synchronization transfers mobile transactions to desktop and desktop changes back to mobile.",
                new[] { "free customer account tracking software", "free customer account software", "cloud customer account tracking", "mobile customer account tracking", "customer account tracking software", "debt and receivables tracking software", "collection tracking software" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("See account activity on one screen", "A customer's debt and collection history is tracked on the same account activity screen instead of being split across separate lists."),
                    new EnglishSection("Search customers from your phone", "Live search narrows results as you type the customer name, eliminating manual searching through long lists."),
                    new EnglishSection("Track outstanding balances", "The remaining outstanding balance is calculated from total debt and total collections using the same logic on desktop and mobile.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "esnaflar-icin-veresiye-takip-programi",
                "Receivables Tracking for Small Businesses | Free Cloud & Mobile Solution",
                "A free receivables solution for small businesses: manage customer accounts, debt, collections and outstanding balances from desktop and mobile.",
                "Receivables Tracking Software for Small Businesses",
                "For a small business selling on account, the most important need is to see quickly how much a customer still owes and when the last payment was made. When records are kept in a paper ledger, customer search and balance checks take more time as the customer base grows.\n\nNSX Veresiye Takip Pro provides a simple way for grocery stores, markets, dealers and small businesses to manage customer accounts. With the v1.0.5 Cloud & Mobile Management System, the business owner can access customer accounts from a phone even when away from the shop.\n\nThe mobile panel includes Total Debt, Total Collections and Outstanding Balance summaries. Search and select a customer to view account activity, then record debt or collections directly under that same customer account. Desktop and mobile work together through live synchronization.",
                new[] { "receivables tracking software for small businesses", "small business receivables software", "grocery store receivables software", "cloud receivables", "mobile receivables", "free accounts receivable software", "customer account tracking" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("Check customer accounts away from the shop", "The business owner can use an authorized phone to find a customer, review account activity and record a debt or collection when necessary."),
                    new EnglishSection("A simple screen for small businesses", "Mobile use focuses only on summaries, customer search and customer account transactions, without unnecessary menus complicating the daily workflow."),
                    new EnglishSection("The desktop workflow remains unchanged", "Internet or mobile access does not replace everyday desktop operation; Cloud access adds another management channel to the existing software.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "borc-alacak-takip-programi-ucretsiz",
                "Free Debt & Receivables Tracking Software | Cloud & Mobile",
                "Manage customer accounts, debt, collections and outstanding balances from desktop and mobile through the Cloud with free debt and receivables tracking software.",
                "Free Debt & Receivables Tracking Software",
                "Debt and receivables tracking software is used to keep customer debt, collections, previous account activity and remaining outstanding balances organized. For businesses with many customers, it is important to record every charge and payment under the correct customer, find previous transactions easily and see the current balance quickly.\n\nNSX Veresiye Takip Pro v1.0.5 meets free debt, receivables and customer account tracking needs on desktop while extending them to mobile through the Cloud & Mobile Management System. Use the secure QR connection in the desktop application to sign in to the mobile panel. Search customers by name; selecting a customer opens account activity and displays debt, collections and outstanding balance on one screen.\n\nThere is no need to move between separate, complex screens to record a new debt or collection. Once the customer account is open, either transaction can be entered directly under that account. The business owner can therefore check a customer's current position and record the required transaction from a phone even when away from the workplace.\n\nLive synchronization helps keep changes current between the desktop application and mobile panel. Total debt, total collections and outstanding balance on the dashboard provide a quick view of the business's overall account position. Company data is separated in the Cloud architecture so each business works only with its own records.\n\nVisit the product page to explore NSX Veresiye Takip Pro for free, use the desktop application and take advantage of its Cloud & Mobile Management features.",
                new[] { "free debt and receivables tracking software", "debt and receivables tracking software", "free accounts receivable software", "cloud customer account tracking software", "mobile customer account tracking", "debt and receivables tracking from phone", "collection tracking software", "customer debt tracking software", "outstanding balance tracking", "QR mobile connection" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("How do you track debt and receivables?", "Keep a separate customer account for each customer and record debt and collection transactions chronologically. The current outstanding balance is tracked by subtracting total collections from total debt. A digital account system makes it easier to search historical transactions and understand the customer's real position."),
                    new EnglishSection("Manage debt and collections from mobile", "In the NSX Veresiye Takip Pro mobile panel, search for and select the customer. Once account activity is open, review previous debt and collections and add a new transaction directly to the customer's account. This simple flow is designed for fast phone use."),
                    new EnglishSection("Total debt, total collections and outstanding balance", "The mobile home screen summarizes the business's total debt, total collections and outstanding balance. This provides a quick view of the overall account position before opening individual customer cards."),
                    new EnglishSection("How do desktop and Cloud work together?", "Customer account transactions made on desktop are synchronized to the Cloud, while mobile transactions are synchronized back to the desktop. The business can continue working on the computer and access the same account system from a phone when needed."),
                    new EnglishSection("Digital tracking instead of paper ledgers and Excel", "It can become difficult to find payment history and the correct balance in paper ledgers or scattered Excel files. A customer-based account activity screen keeps debt and collection records together for a more organized and auditable workflow.")
                },
                new[]
                {
                    new EnglishFaq("Can debt and receivables tracking software be used for free?", "NSX Veresiye Takip Pro is available from its product page with a free-use option and supports essential customer account, debt and collection transactions."),
                    new EnglishFaq("Can I enter debt and collections from my phone?", "Yes. After connecting to the mobile system by QR, open the customer account and record a debt or collection transaction."),
                    new EnglishFaq("Can I see a customer's previous account activity on mobile?", "Yes. Previous debt and collection transactions are displayed under account activity for the synchronized customer account."),
                    new EnglishFaq("How is the outstanding balance calculated?", "The outstanding balance is the remaining account amount after total collections are subtracted from the customer's or business's total debt."),
                    new EnglishFaq("Can data from different businesses get mixed together?", "The Cloud system is designed with company/tenant separation. Each business works through its own connection and with its own records.")
                }),
            new(
                "bakkal-veresiye-defteri-nasil-tutulur",
                "How to Keep a Grocery Store Credit Ledger | Cloud & Mobile Tracking",
                "Digitize your grocery store credit ledger and track customer debt, collections and outstanding balances from desktop and mobile through the Cloud.",
                "How to Keep a Grocery Store Credit Ledger",
                "When keeping a grocery store credit ledger, debt, collections and remaining balance transactions should be recorded separately and chronologically for each customer. As the number of customers grows, a paper ledger can make it difficult to find old records, calculate total debt and verify payments.\n\nNSX Veresiye Takip Pro creates digital customer accounts for grocery stores and markets. A customer's previous debt and collections are kept on one account activity screen. This makes it easy to see both the customer's current balance and the business's total debt, total collections and outstanding balance.\n\nWith the v1.0.5 Cloud & Mobile Management feature, customers can also be searched from a mobile phone in addition to the shop computer. Selecting a customer opens account activity, and new debt or collections can be recorded from the same screen. The transaction is transferred to the desktop through live synchronization.\n\nThis approach preserves the simplicity of a traditional credit ledger while adding digital advantages such as search, transaction history and mobile access.",
                new[] { "grocery store credit ledger", "grocery store receivables software", "mobile credit ledger", "cloud receivables software", "customer debt tracking", "collection tracking", "free accounts receivable software" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("A separate account for every customer", "Debt and collection transactions are kept by customer, preventing records from different customers from being mixed together."),
                    new EnglishSection("Find customers quickly from your phone", "Mobile results are filtered as the customer name is typed, allowing quick access to the relevant customer account."),
                    new EnglishSection("Record daily collections immediately", "A payment received or confirmed away from the shop can be recorded as a collection from the mobile account screen and synchronized with the desktop.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "cari-hesap-takibi-nasil-yapilir",
                "How to Track Customer Accounts | Cloud & Mobile Debt & Collection Management",
                "How do you track customer accounts? Learn how to monitor customer debt, collections, transaction history and outstanding balances from desktop and mobile.",
                "How to Track Customer Accounts",
                "Customer account tracking means recording a customer's debt and collection transactions chronologically and monitoring the remaining outstanding balance. Good account management keeps a separate record for every customer, records each charge and payment with the correct date, and reviews balances regularly.\n\nNSX Veresiye Takip Pro brings this process together on a customer-based account activity screen. Previous debt and collections for the customer are displayed in one place. The outstanding balance is calculated from debt and collection activity, so there is no need to compare separate ledgers or files to understand the current position.\n\nWith v1.0.5, customer account tracking is also available through the Cloud & Mobile system. After QR connection, you can search for customers on a phone, view account activity, and record a new debt or collection directly under the relevant customer. Desktop and mobile stay current through live synchronization.",
                new[] { "customer account tracking", "customer account tracking software", "mobile customer accounts", "cloud customer account tracking software", "debt and receivables tracking software", "customer balance tracking", "collection tracking" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("What information is kept in a customer account?", "Customer details, debt transactions, collections, transaction dates and the remaining outstanding balance are the core customer account records."),
                    new EnglishSection("Manage customer accounts from your phone", "Use an authorized mobile connection to find the customer, review previous account activity and record a new transaction."),
                    new EnglishSection("Consistent balances on desktop and mobile", "Because both sides use the same customer account activity, total debt, collections and outstanding balance remain current using the same calculation logic.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "excel-yerine-veresiye-takip-programi",
                "Use Cloud & Mobile Receivables Software Instead of Excel",
                "Replace Excel-based receivables tracking with a structured system for customer accounts, debt and collections on desktop and mobile through the Cloud.",
                "Benefits of Using Receivables Software Instead of Excel",
                "Tracking receivables in Excel may seem practical at first, but as the number of customers grows, issues such as mixed rows, incorrect formulas, multiple file versions and difficulty finding older transactions can arise. Managing customer accounts from a phone also requires separate file-sharing methods.\n\nNSX Veresiye Takip Pro keeps customers in separate account cards and records debt and collection transactions through a ready-made workflow. Users do not need to write formulas; they select the customer, review account activity and add the transaction to the correct account.\n\nThe v1.0.5 Cloud & Mobile Management System provides a major accessibility advantage over Excel. From a phone connected by QR, you can search customers, view account history and enter debt or collections. Live synchronization also updates the desktop records.\n\nThe goal is therefore not simply to move an Excel sheet to another screen, but to turn customer account management into a searchable, customer-based and mobile-accessible workflow.",
                new[] { "Excel receivables tracking", "receivables software instead of Excel", "cloud receivables software", "mobile receivables software", "customer account tracking software", "free accounts receivable software", "customer account tracking from phone" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("Built-in customer account logic instead of formulas", "Debt and collection transactions are stored in a structured customer account model, so users do not need to maintain spreadsheet formulas."),
                    new EnglishSection("One customer, one transaction history", "When a customer is selected, the relevant debt and collections appear together on the same account screen."),
                    new EnglishSection("The advantage of mobile access", "Searching customers and managing account transactions from a phone reduces the need to send files around and keep track of the latest Excel copy.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "veresiye-defteri-programi",
                "Digital Credit Ledger Software | Cloud & Mobile Debt & Collection Tracking",
                "Keep customer debt, collections and outstanding balances in digital customer accounts and manage them from your phone through the Cloud.",
                "Digital Credit Ledger Software",
                "Digital credit ledger software turns a traditional debt ledger into structured customer accounts. Because each customer's debt and collection history is kept separately, previous transactions are easier to find and outstanding balances are easier to verify.\n\nNSX Veresiye Takip Pro provides customer search, debt entry, collections, account activity and reporting on desktop. v1.0.5 adds the Cloud & Mobile Management System to this structure.\n\nThe phone connects by QR from the desktop Cloud Connection screen. The mobile panel displays business totals; when a customer is searched and selected, the account activity opens. New debt and collections are entered under the same customer account and transferred to desktop through live synchronization.\n\nThis makes the credit ledger accessible both from the workplace computer and, when needed, from a mobile phone.",
                new[] { "digital credit ledger software", "digital customer credit ledger", "cloud credit ledger", "mobile receivables tracking", "debt and receivables tracking software", "collection tracking software", "free accounts receivable software" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("Move from a paper ledger to digital customer accounts", "All debt and collection activity for a customer is brought together in one account, making search and historical review faster."),
                    new EnglishSection("Mobile credit ledger", "A phone authorized by QR can access customer accounts through the simple mobile panel."),
                    new EnglishSection("Instant business summary", "Total Debt, Total Collections and Outstanding Balance are displayed as a summary on the mobile home screen.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "ucretsiz-cari-hesap-programi",
                "Free Customer Account Software | Cloud, Mobile & Collection Tracking",
                "Track debt, collections, customer account activity and outstanding balances live from desktop and mobile with free customer account software.",
                "Free Customer Account Software",
                "Free customer account software is a practical solution for businesses that need to track debt, collections and remaining balances by customer. Keeping account activity under one customer account makes it easier to review payment history and the current position.\n\nNSX Veresiye Takip Pro v1.0.5 adds Cloud & Mobile Management to desktop customer account tracking. The business's Total Debt, Total Collections and Outstanding Balance are displayed on the mobile home screen.\n\nType a name in customer search to find the relevant account. Tapping the customer opens account activity, where a debt or collection transaction can be recorded from the same screen. Live synchronization keeps desktop and mobile records current.",
                new[] { "free customer account software", "free customer account tracking software", "cloud customer accounts", "mobile customer accounts", "receivables tracking software", "collection tracking software", "outstanding balance" },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("Mobile access in free customer account software", "In the current release, customer accounts can be viewed from an authorized phone and essential account transactions can be performed."),
                    new EnglishSection("Debt and collections on the same account screen", "While reviewing the customer's previous activity, a new transaction can also be recorded under the same account."),
                    new EnglishSection("Live totals", "Total debt, total collections and outstanding balance provide a quick view of the business's overall account position.")
                },
                new[]
                {
                    new EnglishFaq("Is NSX Veresiye Takip Pro free?", "Yes. NSX Veresiye Takip Pro is available for free use, and the current v1.0.5 release includes Cloud & Mobile Management in addition to desktop customer account tracking."),
                    new EnglishFaq("Can I view customer accounts from my phone?", "Yes. Use the QR code generated on the desktop Cloud Connection screen to connect to the mobile panel, search customers and view their account activity."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. After selecting a customer, you can record a new debt or collection transaction directly from the same customer account screen."),
                    new EnglishFaq("Do desktop and mobile records stay in sync?", "Live synchronization helps keep customer account changes made on desktop and mobile up to date on both sides.")
                }),
            new(
                "teknik-servis-takip-programi",
                "Repair Shop Management Software | Device, Customer & Service Tracking",
                "Professionally manage device intake, customer records, service status and delivery workflows with repair shop management software.",
                "Repair Shop Management Software",
                "Repair shop management software brings device intake, fault reporting, customer details, repair stages, delivery and payment tracking together in one place. It provides an organized workflow for computer, phone, electronics and similar service businesses.",
                new[] { "repair shop management software", "service management software", "device tracking software" },
                "Explore Repair Shop Software"),
            new(
                "bilgisayar-hizlandirma-programi",
                "PC Optimization Software | Windows Performance Tuning",
                "Reduce unnecessary load, improve system performance and make Windows feel more responsive with PC optimization software.",
                "PC Optimization Software",
                "PC optimization software helps reduce startup load, unnecessary file buildup, settings that can slow the system and other performance issues. Solutions such as NSX Turbo are designed to provide a simpler, faster maintenance experience.",
                new[] { "PC optimization software", "PC performance optimization", "Windows optimization" },
                "Explore NSX Turbo Products"),
            new(
                "nsx-servispro-live-teknik-servis-programi",
                "NSX ServisPro Live | QR-Enabled Repair Shop Management Software",
                "Manage service records, SMS notifications, QR live tracking, customer approvals and customer account transactions from one place with NSX ServisPro Live.",
                "NSX ServisPro Live: QR-Enabled Repair Shop Management Software",
                "Customers of repair businesses now expect more than fast repairs. They want to know the current status of their device, approve pricing and follow the delivery process easily. NSX ServisPro Live is professional repair shop management software that records the entire workflow from service intake to delivery and strengthens customer communication with QR-based live tracking and SMS notifications.\n\nThe software brings everyday operations such as customer and device records, fault descriptions, service statuses, price approval, payment activity, service history, reporting and user permissions together in one place. This reduces phone traffic, keeps staff working from the same record and provides customers with a more transparent service experience.",
                new[] { "NSX ServisPro Live", "QR repair shop management software", "service management software", "repair shop software with SMS notifications", "customer live service tracking", "repair service automation", "device service tracking" },
                "Explore NSX ServisPro Live",
                new[]
                {
                    new EnglishSection("Why is digital tracking important for repair businesses?", "Paper forms and scattered messages can lead to forgotten device statuses and inconsistent information being given to customers. A digital service record keeps device intake, faults, work performed, pricing, payments and delivery details under one record."),
                    new EnglishSection("Live service tracking with QR codes", "A QR code or secure tracking link created for the service record allows the customer to view the current device status. Where supported by the workflow, the customer can approve or reject pricing or send information to the service team. This helps reduce repetitive phone calls to the service desk."),
                    new EnglishSection("SMS notifications and customer communication", "Customers can receive SMS updates about pending approvals, service status or devices ready for collection. Ready-made templates improve communication consistency and prevent staff from repeatedly typing the same messages."),
                    new EnglishSection("Customer accounts, payments and reporting", "Service charges, collections and remaining balances can be tracked together with the customer record. Daily, monthly and period-based reports make service volume and the financial position of the business easier to understand."),
                    new EnglishSection("Who is it for?", "It is suitable for businesses that service computers, phones, tablets, electronics, home appliances, small appliances and similar products. It is particularly useful for service centers that accept many devices and want to keep customers informed consistently."),
                    new EnglishSection("How NSX ServisPro Live helps the business", "Structured records, transparent customer communication and centralized tracking make service workflows more controlled. Staff workload is reduced while customer satisfaction and the professional image of the business are strengthened.")
                },
                new[]
                {
                    new EnglishFaq("What does NSX ServisPro Live do?", "It helps manage repair records, device statuses, customer communication, QR live tracking, SMS notifications and payment workflows from one place."),
                    new EnglishFaq("Can customers track their device from a phone?", "Yes. Customers can view the service status in a mobile browser through the generated secure QR code or tracking link."),
                    new EnglishFaq("Does it support SMS notifications?", "Yes. SMS management can be used for updates such as service status, approvals and delivery."),
                    new EnglishFaq("What types of repair businesses is it suitable for?", "It is suitable for businesses that service computers, phones, tablets, electronics, home appliances and similar devices.")
                }),
            new(
                "dugun-salonu-yonetim-programi",
                "Wedding Venue Management Software | Reservations, Contracts & Accounts",
                "Professionally manage reservations, customers, contracts, collections, customer accounts, cash flow and event operations with wedding venue management software.",
                "Wedding Venue Management Software: From Reservation to Collection in One System",
                "Wedding venue businesses often manage multiple inquiries for the same date, different halls, deposits, contracts and event details at the same time. NSX Düğün Salonu Pro was developed to reduce record complexity by bringing reservations, customers, contracts, customer accounts, collections, cash management, SMS and event workflows together in one place.\n\nWeddings, engagements, henna nights, circumcision celebrations, meetings and private events can be recorded with date and venue information. The customer agreement, deposit received, remaining balance, additional services and contract information can all be followed from the same customer card. With the QR-enabled web panel, an authorized business owner can continue customer and reservation operations even while away from the office.",
                new[] { "wedding venue management software", "wedding venue reservation software", "event management software", "venue management software", "contract tracking", "collection tracking", "NSX Düğün Salonu Pro" },
                "Explore NSX Düğün Salonu Pro",
                new[]
                {
                    new EnglishSection("Prevent reservation conflicts", "Calendar- and venue-based reservation management reduces the risk of creating multiple events for the same hall and date. Upcoming events can be viewed from one screen."),
                    new EnglishSection("Customer and event details", "Customer contact information, event type, guest count, venue, menu, additional services and special notes are linked to the reservation so commitments made during discussions are not lost."),
                    new EnglishSection("Contract and document management", "Professional contracts can be prepared using reservation details and current customer account information. Contract history is stored on the customer card for quick access when needed."),
                    new EnglishSection("Deposits, collections and customer accounts", "Deposits received, later collections, additional charges and remaining balances are tracked by customer. Cash transactions make it easier to monitor the business's daily financial position."),
                    new EnglishSection("Remote access through the QR-enabled web panel", "The business owner can securely create customers, add reservations and review existing records from a mobile browser through a QR session. Changes are synchronized when the desktop application reconnects."),
                    new EnglishSection("SMS Information System", "Reservation, payment and event updates can be sent using ready-made templates, helping the business communicate with customers on time and in a professional manner."),
                    new EnglishSection("What businesses is it suitable for?", "It is suitable for wedding venues, outdoor wedding locations, event spaces, event planning companies, meeting venues and other reservation-based event businesses.")
                },
                new[]
                {
                    new EnglishFaq("What does wedding venue management software track?", "It tracks reservations, customers, event details, contracts, deposits and collections, customer account balances and cash transactions."),
                    new EnglishFaq("Can multiple halls be managed on the same day?", "Yes. Venue- and date-based records allow reservations for different halls in the business to be planned separately."),
                    new EnglishFaq("Can reservations be entered while away from the office?", "Yes. An authorized business owner can manage customers and reservations from a mobile device through the secure QR-enabled web panel."),
                    new EnglishFaq("Can customer contracts be prepared?", "Yes. Contracts can be created from reservation and customer account information and linked to the customer record.")
                }),
            new(
                "oto-galeri-yonetim-programi",
                "Car Dealership Software | Vehicle Inventory, Sales & Customer Accounts",
                "Manage vehicle inventory, purchases and sales, customer accounts, deposits, expenses and profit-and-loss reporting with car dealership software.",
                "Manage Vehicle Inventory and Sales with Car Dealership Software",
                "In a car dealership, it becomes difficult to understand true profitability when vehicle cost, selling price, deposits, customer balances, notary documents and vehicle expenses are not tracked accurately. NSX Oto Galeri Pro brings vehicle inventory, customer records, purchase and sales transactions, customer accounts and reports together in one place.\n\nFor each vehicle, you can record make, model, license plate, mileage, purchase price, selling price and status. Registration documents, inspection reports, notary documents and vehicle photos can be attached to the relevant vehicle card. When a sale is completed, customer, payment and contract information are managed within the same workflow.",
                new[] { "car dealership software", "vehicle inventory tracking software", "vehicle purchase and sales software", "dealership customer account tracking", "car dealership sales contracts", "vehicle profit and loss report", "NSX Oto Galeri Pro" },
                "Explore NSX Oto Galeri Pro",
                new[]
                {
                    new EnglishSection("Vehicle inventory management", "Vehicles for sale, reserved vehicles and sold vehicles are tracked by status. Vehicle details and images are kept together on one card."),
                    new EnglishSection("Purchase, sale and cost calculation", "The vehicle purchase price, selling price and additional vehicle expenses can be recorded to calculate true cost and analyze profit or loss."),
                    new EnglishSection("Deposit and reservation tracking", "Deposits and reservation information for a vehicle are recorded, reducing the risk of accidentally selling the same vehicle to different customers."),
                    new EnglishSection("Customer accounts", "Customer balances, collections and remaining amounts are linked to the sales transaction. Payment history can be reviewed from the customer card."),
                    new EnglishSection("Contracts and document archive", "Sales contracts, registration documents, inspection reports, notary documents and other files can be attached to the vehicle or customer record to create an organized archive."),
                    new EnglishSection("Reporting and business control", "Inventory value, sales, expenses, collections and profitability reports help dealership management make better-informed decisions.")
                },
                new[]
                {
                    new EnglishFaq("What records does car dealership software keep?", "It can keep vehicle inventory, purchase and sale transactions, customer records, deposits, customer account activity, expenses, documents and reports."),
                    new EnglishFaq("Are vehicle expenses included in profit calculations?", "Yes. Vehicle-related expenses can be recorded so purchase cost and sales results can be analyzed more realistically."),
                    new EnglishFaq("Can vehicle documents be stored?", "Yes. Registration documents, inspection reports, notary documents, photos and other files can be archived under the relevant vehicle record."),
                    new EnglishFaq("Can a vehicle with a deposit be marked as reserved?", "Yes. Deposit and reservation information can be tracked together with the vehicle status.")
                }),
            new(
                "oto-tamir-servis-yonetim-programi",
                "Auto Repair Shop Software | Vehicle Intake & Service Tracking",
                "Professionally manage vehicle intake, work orders, customers, service history, collections, expenses and service reports with auto repair shop software.",
                "Auto Repair Shop Software: Complete Tracking from Intake to Delivery",
                "Auto repair shops and independent garages need to track vehicle intake, planned work, replaced parts, customer approvals, payments and delivery information systematically. NSX Oto Tamir Servis Pro helps manage the entire process under one record from the moment a vehicle enters the workshop until it is delivered.\n\nCustomer and vehicle information, service complaints, labor performed, parts used, work orders, collections and previous transactions are stored in the same system. Multi-user support and activity logs make staff responsibilities and internal operations more controlled.",
                new[] { "auto repair shop software", "auto service tracking software", "vehicle intake software", "repair work order software", "vehicle maintenance tracking", "auto repair customer account tracking", "NSX Oto Tamir Servis Pro" },
                "Explore NSX Oto Tamir Servis Pro",
                new[]
                {
                    new EnglishSection("Standardize vehicle intake", "Customer, license plate, vehicle, mileage, complaint and received accessories are recorded to reduce the risk of missing information during intake."),
                    new EnglishSection("Work orders and service history", "Labor, parts used, descriptions and charges are added to the work order. When the vehicle returns, previous work can be reviewed quickly."),
                    new EnglishSection("Customer- and vehicle-based tracking", "Multiple vehicles for the same customer can be managed on separate cards. Each vehicle's service history, documents and photos remain under its own record."),
                    new EnglishSection("Customer accounts and collections", "Service charges, payments received and remaining balances are posted to the customer's account. Daily and period-based collections can be reported."),
                    new EnglishSection("Multi-user operation and activity security", "Staff can work with separate user accounts. Activity records make it possible to see which user made a change and when."),
                    new EnglishSection("Reports and backups", "Daily, weekly, monthly and annual service reports can be generated. Regular backups help protect business data.")
                },
                new[]
                {
                    new EnglishFaq("What does auto repair shop software do?", "It organizes vehicle intake, repair operations, customer and vehicle history, work orders, collections and reporting."),
                    new EnglishFaq("Can vehicle history be viewed?", "Yes. Previous service records, work performed and attached documents can be reviewed from the vehicle card."),
                    new EnglishFaq("Can multiple staff members use it?", "Yes. Multi-user support allows staff to work from separate accounts while activity can be tracked."),
                    new EnglishFaq("Can work orders be printed?", "Yes. Service records can be saved as PDF or printed using the available output formats.")
                }),
            new(
                "klinik-randevu-takip-programi",
                "Clinic Appointment Software | Patient, Doctor & Payment Tracking",
                "Manage patient records, doctors, appointments, treatment history, customer accounts and payments with professional clinic appointment software.",
                "Organize Patient Workflows with Clinic & Appointment Management Software",
                "Clinics and appointment-based businesses need to manage patient registration, doctor schedules, treatment notes, payments and follow-up appointments at the same time. NSX Clinic & Appointment Management Software brings patients, doctors, appointments, customer accounts and collection workflows together in one place to make daily operations more organized.\n\nThe patient record provides access to contact details, appointment history, completed procedures, notes and payment activity. Daily, weekly and monthly scheduling views make it easier to identify available time slots and manage busy periods.\n\nOne of NSX Clinic's standout capabilities is its Patient Monitor and kiosk-supported queue management system. A kiosk screen at reception or in the waiting area can support patient check-in and queue management, while a display mounted outside the examination room can visually show the next patient. Voice calling can announce the patient's name or queue number, making in-clinic guidance clearer, more orderly and more professional.",
                new[] { "clinic management software", "clinic appointment software", "patient management software", "doctor appointment system", "medical practice software", "beauty salon appointment software", "NSX Clinic" },
                "Explore Clinic Software",
                new[]
                {
                    new EnglishSection("Patient Monitor, kiosk and door-side queue system", "NSX Clinic professionalizes in-clinic queue management with a kiosk screen for the waiting area, a Patient Monitor that can be mounted outside the examination room, and audio-visual calling support. The next patient or queue number can be displayed while a voice announcement is made at the same time. This is especially useful in busy clinics because it reduces reception workload, helps patients reach the correct room and creates a more orderly waiting process."),
                    new EnglishSection("Patient records and treatment history", "Patient contact details, previous appointments, completed procedures, notes and payment activity can be reviewed from the same patient record."),
                    new EnglishSection("Doctor and staff scheduling", "Appointments can be planned by doctor or staff member, making work schedules easier to organize and helping teams see busy periods and availability."),
                    new EnglishSection("Daily, weekly and monthly appointment views", "Multiple calendar views make it easier to track upcoming appointments and manage cancellations or schedule changes."),
                    new EnglishSection("Customer accounts and collections", "Patient charges, payments and remaining balances are recorded. Collection history and period-based revenue can also be reported."),
                    new EnglishSection("Who is it for?", "Clinics, medical practices, dietitians, psychologists, consulting centers, beauty centers and similar appointment-based service businesses can use the software."),
                    new EnglishSection("Organized records and better service quality", "Keeping appointment and patient information in one system helps reduce missed appointments, scattered notes and payment confusion.")
                },
                new[]
                {
                    new EnglishFaq("What does the Patient Monitor and kiosk system do?", "The kiosk helps with patient check-in and queue management. The door-side Patient Monitor visually shows the next patient, while voice calling can announce the patient name or queue number."),
                    new EnglishFaq("What businesses can use clinic appointment software?", "It is suitable for clinics, medical practices, dietitians, psychologists, beauty centers and other appointment-based service businesses."),
                    new EnglishFaq("Can patient history be stored?", "Yes. Appointment history, completed procedures, notes and payment activity can be stored in the patient record."),
                    new EnglishFaq("Can appointments be scheduled by doctor?", "Yes. Appointments can be planned by date and time for a selected doctor or staff member."),
                    new EnglishFaq("Can payments and balances be tracked?", "Yes. Patient-level charges, payments and remaining balances can be tracked.")
                }),
            new(
                "veresiye-cari-takip-programi-pro",
                "NSX Veresiye Takip Pro v1.0.5 | Cloud & Mobile Customer Account Tracking",
                "Manage customer accounts, amounts owed and collections live from desktop and mobile with the Cloud system in NSX Veresiye Takip Pro v1.0.5.",
                "Manage Customer Accounts Anywhere with NSX Veresiye Takip Pro v1.0.5",
                "NSX Veresiye Takip Pro v1.0.5 combines customer accounts, amounts owed, collections, outstanding balances and transaction history in a simple desktop application, while its new Cloud & Mobile Management System brings everyday customer account operations to your phone.\n\nGenerate a secure QR code from the desktop Cloud Connection screen to connect your phone to the business Cloud account. The mobile dashboard displays Total Debt, Total Collections and Outstanding Balance. Use customer search to find an account quickly, then open the customer to review previous debt and collection activity.\n\nNew debt and collection transactions can be recorded directly from the selected customer's account. Live synchronization transfers mobile transactions to desktop and desktop account changes to the mobile system. This means a business owner can check a customer's current position even when away from the workplace.\n\nThe update is designed to preserve existing customer and account records. NSX Veresiye Takip Pro combines the speed of desktop operation with the convenience of Cloud & Mobile access.",
                new[]
                {
                    "NSX Veresiye Takip Pro", "receivables tracking software", "cloud receivables software", "mobile customer account tracking", "customer account tracking software", "customer debt tracking software", "collection tracking software", "QR customer account tracking", "live synchronization"
                },
                "Explore NSX Veresiye Takip Pro for Free",
                new[]
                {
                    new EnglishSection("Cloud & Mobile Management", "From a phone authorized by QR code, you can view business summaries, search customers, review customer account activity, and record debt or collection transactions."),
                    new EnglishSection("Customer account activity on one screen", "Previous debt and collection transactions are kept in one customer account activity list. New transactions are recorded from the same selected customer account."),
                    new EnglishSection("Live synchronization", "Desktop and mobile stay current with the same customer account activity, while total debt, total collections and outstanding balance use the same calculation logic."),
                    new EnglishSection("Upgrade while preserving existing data", "The v1.0.5 update adds Cloud & Mobile capabilities while preserving existing company, customer, debt and collection records.")
                },
                new[]
                {
                    new EnglishFaq("Does NSX Veresiye Takip Pro v1.0.5 include Cloud access?", "Yes. You can connect to the mobile panel by QR code from the Cloud Connection screen and manage essential customer account operations from your phone."),
                    new EnglishFaq("Can I view customer account activity on mobile?", "Yes. Selecting a customer displays previous debt and collection activity on the same customer account screen."),
                    new EnglishFaq("Can I record debt and collections from my phone?", "Yes. A new debt or collection transaction can be recorded directly to the selected customer account."),
                    new EnglishFaq("Does the update delete existing records?", "No. The update is designed to preserve existing company, customer and customer account records.")
                }),
            new(
                "nsx-barkodlu-satis-cari-stok-takip-pro",
                "NSX Barkodlu Satış, Cari ve Stok Takip Pro | Barcode POS Software",
                "Manage barcode sales, inventory, customer accounts, cash registers, suppliers and reporting from one place with NSX Barkodlu Satış, Cari ve Stok Takip Pro.",
                "Complete Control from Sales to Inventory with NSX Barcode Sales Pro",
                "Retail businesses can lose time and create accounting errors when product, barcode, inventory, customer account, supplier, cash register and sales records are maintained separately. NSX Barkodlu Satış, Cari ve Stok Takip Pro brings everyday business workflows together in one application, from the fast checkout screen and inventory movements to customer accounts, collections and reports.\n\nBarcode scanner support lets products be added to a sale quickly, while cash, card and customer-credit sales can all be recorded. Stock receipts and issues, critical stock levels, purchase and selling prices, supplier transactions and customer balances are managed within the same structure. Support for thermal receipts, standard printers and barcode labels also simplifies post-sale document workflows.",
                new[]
                {
                    "NSX Barkodlu Satış, Cari ve Stok Takip Pro", "barcode POS software", "barcode customer account software", "barcode inventory software", "inventory management software", "fast checkout software", "retail POS software", "store sales software", "barcode label software", "customer credit sales software"
                },
                "Explore NSX Barcode Sales Pro",
                new[]
                {
                    new EnglishSection("Fast barcode checkout", "Add sale items quickly with a barcode scanner or product search. Quantity, discount, VAT and total amount are calculated instantly."),
                    new EnglishSection("Inventory and product management", "Product records, purchase and selling prices, stock receipts and issues, critical stock levels and barcode data are tracked in an organized structure."),
                    new EnglishSection("Customer accounts and credit sales", "Select a customer to make an on-account sale, then track amounts owed, collections, payment history and the current balance from the customer record."),
                    new EnglishSection("Cash register, suppliers and reports", "Cash register activity, supplier purchases, sales history, profit and loss, and period-based reports are managed from one place."),
                    new EnglishSection("Receipts and barcode labels", "Print retail sales receipts using a thermal or standard printer and create professional barcode labels for products."),
                    new EnglishSection("Who is it for?", "Suitable for supermarkets, grocery stores, kiosks, retail stores, stationery shops, spare-parts businesses, wholesalers and other retail operations.")
                },
                new[]
                {
                    new EnglishFaq("Are barcode scanners supported?", "Yes. Standard barcode scanners can be used to add products and complete sales quickly."),
                    new EnglishFaq("Can I track credit sales and customer accounts?", "Yes. Customer-based credit sales, amounts owed, collections, payment history and balances can be tracked."),
                    new EnglishFaq("Does inventory update after a sale?", "Yes. When a sale is completed, stock movements for the relevant products are recorded and the current stock quantity is recalculated."),
                    new EnglishFaq("Can I print thermal receipts and barcode labels?", "Yes. With a compatible printer selected, you can print retail sales receipts and product barcode labels.")
                }),
            new(
                "nsx-kasa-defteri-pro-programi",
                "NSX Kasa Defteri Pro | Cash Book, Income, Expense & Customer Account Guide",
                "Manage daily income and expenses, collections, payments, multiple cash registers, foreign-currency cash accounts, customer accounts, transfers, reports and backups with NSX Kasa Defteri Pro.",
                "Manage Income, Expenses and Cash Registers with NSX Kasa Defteri Pro",
                "NSX Kasa Defteri Pro is professional cash book software designed to keep a business's daily cash inflows and outflows, cash balances, collections and payments organized in one place. Instead of paper ledgers, scattered notes or separate Excel files, every financial movement is recorded against the relevant cash account so the business's current cash position is easier to understand.\n\nWhen starting, define the cash accounts used by the business and enter an opening or carried-forward balance when necessary. Record income and collections as cash inflows and expenses or payments as cash outflows against the correct account. Posting every movement on the day it occurs helps make end-of-day reconciliation more reliable.\n\nBusinesses using multiple cash accounts can maintain separate structures for TRY, USD, EUR or other required currencies. Each cash account can be monitored independently, while transfers between accounts remain traceable through transfer transactions. This makes both physical cash locations and multiple currencies easier to manage from one screen.\n\nCustomer account support allows collections and payments to be linked to the relevant customer or company. Reports bring together cash movements, income and expense balance, and current balances for a clearer financial overview. Regular backups help protect cash and account records. NSX Kasa Defteri Pro is a practical Windows desktop solution for tradespeople and businesses that want simpler daily financial tracking without sacrificing record discipline.",
                new[]
                {
                    "NSX Kasa Defteri Pro", "cash book software", "income and expense tracking software", "cash register tracking software", "collection tracking software", "payment tracking software", "multi-cash-account software", "foreign currency cash tracking", "customer account tracking software", "cash transfer tracking", "business income expense software", "Windows cash book software"
                },
                "Explore NSX Kasa Defteri Pro",
                new[]
                {
                    new EnglishSection("Set up cash accounts and opening balances", "Create a separate record for every cash account used by the business. When starting, enter the current amount as an opening or carried-forward balance so the software matches the real cash position."),
                    new EnglishSection("Record income and collections the same day", "Post sales income, collections and other incoming amounts to the relevant cash account. Consistent same-day posting keeps the current cash balance accurate."),
                    new EnglishSection("Post expenses and payments to the correct account", "Record business expenses, supplier payments and other cash outflows against the cash account used for the transaction so every outflow remains traceable."),
                    new EnglishSection("Track TRY and foreign-currency cash accounts separately", "Create separate cash accounts for TRY, USD, EUR or other currencies used by the business. Each account is tracked by its own currency and movement history."),
                    new EnglishSection("Keep transfers between cash accounts traceable", "When money moves from one cash account to another, record it as a transfer. This provides a clearer audit trail than manually adjusting balances."),
                    new EnglishSection("Link collections and payments to customer accounts", "Use customer or company account records to associate collections and payments with the relevant account. Reviewing customer history together with cash activity makes reconciliation easier."),
                    new EnglishSection("Perform end-of-day cash and income-expense checks", "At the end of the day, review cash balances, inflows, outflows and the income-expense position. Reports make it easier to verify records and understand the business's financial status."),
                    new EnglishSection("Protect records with regular backups", "Cash and customer account activity is critical financial data. Use the backup feature regularly and keep current backups in a secure location.")
                },
                new[]
                {
                    new EnglishFaq("What is NSX Kasa Defteri Pro used for?", "It helps businesses record income, expenses, collections, payments and cash balances while managing multiple cash accounts, customer accounts and reports from one place."),
                    new EnglishFaq("Can I use more than one cash account?", "Yes. Different cash accounts can be defined separately and each balance can be tracked from its own transaction history."),
                    new EnglishFaq("Can I create cash accounts in currencies other than TRY?", "Yes. You can create separate cash accounts for USD, EUR or other currencies in addition to TRY."),
                    new EnglishFaq("Can money be transferred between cash accounts?", "Yes. Money moved from one cash account to another can be recorded as a transfer so both sides remain traceable."),
                    new EnglishFaq("Can I use customer accounts as well?", "Yes. Customer and company account records can be used to associate collections and payments with the relevant account."),
                    new EnglishFaq("How should I perform end-of-day cash reconciliation?", "Record all inflows and outflows on the same day, then compare cash balances and income-expense activity against the reports at day end."),
                    new EnglishFaq("How can I keep cash records safer?", "Take regular backups and store current backup copies in a secure location to help protect financial records against data loss.")
                }),
            new(
                "nsx-sigorta-acente-pro-programi",
                "NSX Sigorta Acente Pro | Policy, Customer & Renewal Tracking Software",
                "Manage customers, insurance policies, quotes, collections, cash accounts, commissions and policy renewals from one place with NSX Sigorta Acente Pro.",
                "Centralize Policy and Insurance Agency Management with NSX Sigorta Acente Pro",
                "Daily follow-up becomes difficult when an insurance agency keeps customer information, policies, quotes, collections, commissions and renewal dates in separate files. NSX Sigorta Acente Pro is designed to manage customer, policy, insurer, quote, customer account, collection, cash register and reporting workflows from one place.\n\nSupported policy PDFs can be imported so available customer and policy information can be converted into records more quickly. Renewal dates can be tracked from policy start and end dates, agency commission rates can be defined for insurers, and agency earnings can be calculated automatically for new policies. With the Cash Book, income-expense management and professional reports, the agency can monitor not only collections but also its real financial position more clearly.",
                new[]
                {
                    "NSX Sigorta Acente Pro", "insurance agency software", "insurance broker software", "insurance policy tracking software", "policy renewal software", "insurance customer management software", "agency commission tracking software", "insurance quote software"
                },
                "Explore NSX Sigorta Acente Pro",
                new[]
                {
                    new EnglishSection("Fast policy import from PDF", "Supported policy PDFs can be imported, allowing available details such as customer, policy, insurer, policy number, dates and premium to be transferred to the record screen more quickly."),
                    new EnglishSection("Customer and policy management", "Individual and corporate customers can be linked with motor third-party liability, comprehensive motor, home, workplace, health and other policy types and managed from one place."),
                    new EnglishSection("Policy renewal tracking", "Upcoming policies can be identified from start and end dates. Configurable reminder periods make it easier to contact customers at the right time for renewal."),
                    new EnglishSection("Insurer and commission management", "Insurers can be recorded, agency commission rates can be defined by company, and policy earnings can be calculated automatically using the relevant rate."),
                    new EnglishSection("Quotes, customer accounts and collections", "Customer quotes, policy amounts, payments received, remaining balances and account activity can be tracked in a connected workflow."),
                    new EnglishSection("Cash book, income-expense and net profit", "Cash inflows and outflows, operating expenses, agency earnings, net profit and net profit margin can be monitored separately for a clearer view of real financial performance."),
                    new EnglishSection("Professional reports", "Customer account finance, insurer performance, income-expense and period-based activity reports can be reviewed, while suitable reports can be archived or shared as PDF files.")
                },
                new[]
                {
                    new EnglishFaq("Can policy PDFs be imported into the software?", "Yes. Supported policy PDFs can be analyzed to help convert available customer and policy details into records more quickly."),
                    new EnglishFaq("Can policy renewal dates be tracked?", "Yes. Upcoming renewals can be tracked from policy start and end dates, with a configurable reminder period."),
                    new EnglishFaq("Are insurer commissions calculated automatically?", "The agency commission rate defined for an insurer can be applied to new policy records to help calculate agency earnings."),
                    new EnglishFaq("Does it include cash book and income-expense tracking?", "Yes. Cash movements, income, expenses, agency earnings and net profit information can be monitored in dedicated areas.")
                }),
            new(
                "nsx-cari-takip-pro-programi",
                "NSX Cari Takip Pro | Debt, Receivables, Collections & Balance",
                "Manage customer and company accounts, debt and receivable activity, collections, payments, reminders and reports with NSX Cari Takip Pro.",
                "Organize Customer and Company Accounts with NSX Cari Takip Pro",
                "NSX Cari Takip Pro is professional customer account software for businesses that want to keep customer and company balances organized. Debt, receivables, collections, payments, transaction history and net balance are tracked by account record.\n\nSearch, sorting, reminders, reporting, statements and backup tools make everyday financial activity easier to review. Critical information such as the latest transaction date and current balance is visible in the customer list, helping the business see when each account was last updated.",
                new[] { "NSX Cari Takip Pro", "customer account tracking software", "accounts receivable software", "debt and receivables tracking software", "collection tracking software", "customer balance tracking software", "customer account management software" },
                "Explore NSX Cari Takip Pro"),
            new(
                "nsx-teknik-servis-pro-programi",
                "NSX Teknik Servis Pro | Device Intake & Service Tracking Software",
                "Manage customers, device intake, fault reports, service status, payments, delivery, service history and reporting with NSX Teknik Servis Pro.",
                "Manage Repair Records from One Place with NSX Teknik Servis Pro",
                "NSX Teknik Servis Pro is designed to organize the full workflow from customer and device intake through delivery for computer, phone, electronics and similar repair businesses. Fault descriptions, device information, service status, work performed, charges, payments and delivery details are kept in the same service record.\n\nCustomer and device history makes previous work easy to review. Service receipts, status tracking, reporting and reminder features help reduce lost records in busy repair-shop operations.",
                new[] { "NSX Teknik Servis Pro", "repair shop software", "repair shop management software", "device intake software", "repair work order software", "customer device tracking software" },
                "Explore NSX Teknik Servis Pro"),
            new(
                "nsx-security-auditor-pro-kod-guvenlik-analizi",
                "NSX Security Auditor Pro | AI-Assisted Code Security Analysis",
                "Analyze source code, APIs, web and desktop applications, publish outputs, exposed secrets and dependency vulnerabilities before release with NSX Security Auditor Pro.",
                "Audit Your Code Before Release with NSX Security Auditor Pro",
                "Waiting until after release to perform security checks can allow embedded API keys, risky code patterns, insecure data flows and vulnerable dependencies to reach production. NSX Security Auditor Pro is a professional security analysis platform that reviews source code without executing it, making these risks visible during development.\n\nSource code, web projects, APIs, desktop applications, dependency files and publish outputs can be scanned from one place. Every finding includes severity, relevant code context, confidence, a technical explanation and an actionable remediation recommendation. Developers receive more than a list of warnings: they get an organized plan showing which risk to address, why it matters and how to remediate it.\n\nAn optional AI second opinion can be configured with a local model, Google Gemini, OpenAI or OpenAI-compatible services. Approved, masked finding information can be reassessed to help distinguish false positives and clarify remediation steps. Project files are not modified and source code is not executed.",
                new[]
                {
                    "NSX Security Auditor Pro", "code security analysis", "AI code analysis", "source code security scanner", "API security analysis", "publish security audit", "secret detection", "dependency vulnerability scanning", "SARIF report", "SBOM report"
                },
                "Explore NSX Security Auditor Pro",
                new[]
                {
                    new EnglishSection("Source code and project analysis", "Risky code patterns in web, API, desktop and other project types are reviewed without executing the source code. Findings are shown with file and code context."),
                    new EnglishSection("Secret and credential detection", "API keys, tokens, passwords and other sensitive information accidentally embedded in source code are scanned so exposures can be removed before release."),
                    new EnglishSection("Dependency and CVE auditing", "NuGet, npm, PyPI, Maven, Composer, Go Modules and Cargo dependencies are checked for known vulnerabilities so risky packages can be prioritized."),
                    new EnglishSection("AI-assisted second opinion", "Optional AI analysis reassesses masked findings to help identify likely false positives and produce developer-focused remediation suggestions."),
                    new EnglishSection("Professional security reports", "In addition to HTML and PDF management reports, SARIF and SBOM outputs can be generated so findings can move into development, audit and documentation workflows."),
                    new EnglishSection("Who is it for?", "Suitable for individual developers, software companies, technical teams, security professionals and organizations that want a structured security review before releasing their projects.")
                },
                new[]
                {
                    new EnglishFaq("Does NSX Security Auditor Pro execute source code?", "No. Project files are analyzed statically; source code is not executed and project files are not modified during scanning."),
                    new EnglishFaq("What types of projects can be analyzed?", "Source code projects, web applications, APIs, desktop applications, dependency files and publish outputs can be analyzed."),
                    new EnglishFaq("Can it detect API keys and passwords?", "Yes. It can scan for API keys, tokens, passwords and other sensitive-information patterns that may be embedded in source code."),
                    new EnglishFaq("Is AI required?", "No. Core security scanning is performed by the local analysis engine. AI second opinion is optional and can be configured separately with a supported service."),
                    new EnglishFaq("Which report formats are available?", "HTML and PDF are available for technical findings and management summaries, SARIF for security-tool integration, and SBOM for component inventory.")
                }),
            new(
                "nsx-okul-plan-pro-ders-programi",
                "NSX School Planner Pro | Timetable & Teacher Duty Roster Software",
                "Manage teachers, classes, classrooms, timetables, assignments and teacher duty rosters professionally from one place with NSX School Planner Pro.",
                "Manage Timetables and Teacher Duty Rosters with NSX School Planner Pro",
                "Preparing a weekly school timetable requires teacher workloads, classes, classrooms, lesson periods and duty assignments to work together without conflicts. Managing this information across separate spreadsheets or scattered documents can make planning slower and changes harder to control. NSX School Planner Pro is a professional Windows desktop solution that brings the school's core planning data into one system.\n\nTeacher, class, classroom and subject records are managed in organized screens. These records are used to create the weekly timetable, while teacher- and class-based assignments remain part of the same planning workflow. Administrators can review the overall schedule more clearly and make required adjustments from a centralized structure.\n\nThe duty-planning module makes it easier to record and monitor teacher duty assignments according to the school's operating model. Daily schedules, upcoming duties and planning summaries can be reviewed from the main screen. Keeping timetable and duty information together improves day-to-day coordination for school administration.\n\nPDF and print support allows completed schedules to be archived digitally or distributed as physical copies. Reporting screens help administrators review defined teachers, classes, classrooms, subjects and duty records in a more controlled way. NSX School Planner Pro provides a clean, fast and professional planning experience for school administrators, assistant principals and authorized staff responsible for timetabling.",
                new[]
                {
                    "NSX School Planner Pro", "school timetable software", "timetable scheduling software", "teacher timetable software", "class timetable software", "teacher duty roster software", "teacher duty schedule", "classroom scheduling", "school management software", "Windows school scheduling software"
                },
                "Explore NSX School Planner Pro",
                new[]
                {
                    new EnglishSection("Manage teacher records in one place", "Define teacher information centrally so it can be used across timetable and duty-planning workflows, keeping core planning data independent of scattered files."),
                    new EnglishSection("Classes, classrooms and subjects", "Manage classes, available classrooms and subject information as separate structured records so the school setup required for planning remains clear and controlled."),
                    new EnglishSection("Weekly timetables", "Plan lesson periods by day of the week and period number. The weekly view makes it easier to evaluate class and teacher schedules as a whole."),
                    new EnglishSection("Centralized assignment workflows", "Manage relationships between teachers, classes, subjects and classrooms within the assignment structure. Handling changes from the same center strengthens planning control."),
                    new EnglishSection("Teacher duty roster planning", "Plan duties by day, duty location and teacher. Keeping daily duty assignments recorded in the system makes school coordination easier."),
                    new EnglishSection("PDF, printing and sharing", "Save completed timetable and duty schedules as PDF or print them using the available output options for easy distribution to administrators, teachers and relevant departments."),
                    new EnglishSection("Reporting and overall control", "Review teacher, class, classroom, subject and duty records through reports so planning data can be evaluated and archived more systematically."),
                    new EnglishSection("Who is it for?", "Suitable for administrators and authorized staff who prepare timetables and teacher duty rosters in primary schools, middle schools, high schools, training centers and similar educational institutions.")
                },
                new[]
                {
                    new EnglishFaq("What does NSX School Planner Pro do?", "It helps manage teachers, classes, classrooms and subjects while preparing weekly timetables, assignments and teacher duty rosters from one place."),
                    new EnglishFaq("Can I create teacher- and class-based timetables?", "Yes. Weekly schedules can be built and tracked using the defined teacher, class, subject and classroom information."),
                    new EnglishFaq("Can I prepare teacher duty rosters?", "Yes. Duty assignments can be planned by teacher, day and duty arrangement, and daily duty information can be monitored in the same system."),
                    new EnglishFaq("Can timetables be exported to PDF?", "Yes. Available PDF and print options can be used to save schedules digitally or distribute physical copies."),
                    new EnglishFaq("What institutions can use NSX School Planner Pro?", "It is suitable for schools, training centers and similar educational institutions that prepare timetables and teacher duty rosters.")
                }),
            new(
                "oyun-hizlandirma-fps-artirma-programi",
                "Game Booster & FPS Optimization Software | NSX Turbo",
                "Improve gaming performance by using PC resources more efficiently with game booster and FPS optimization software.",
                "Game Booster & FPS Optimization Software",
                "Game booster and FPS optimization software is designed to reduce unnecessary background load, optimize system performance and provide a more stable gaming experience. NSX Turbo is built for users who want a practical way to manage gaming and everyday PC performance.",
                new[] { "game booster software", "FPS optimization software", "NSX Turbo" },
                "Explore NSX Turbo Products"),
        };

        public static readonly IReadOnlyList<LocalizationSeedItem> Items = BuildItems();
    }
}
