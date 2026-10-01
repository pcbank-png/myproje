namespace NSYazilim.Web.Services
{
    public static class LocalizationEnglishSharedSeedData
    {
        private static LocalizationSeedItem E(string source, string english, string area)
            => new(source, english, area);

        public static readonly IReadOnlyList<LocalizationSeedItem> Items = new LocalizationSeedItem[]
        {
            E("Üye Girişi", "Sign In", "/Shared"),
            E("Blog / Rehberler", "Blog / Guides", "/Shared"),
            E("Teklif Al", "Get a Quote", "/Shared/Client"),
            E("ANA SAYFA", "HOME", "/Shared/Client"),
            E("Lisanslar", "Licenses", "/Shared/Client"),
            E("Kategoriler", "Categories", "/Shared/Client"),
            E("Bilgiler", "Information", "/Shared/Client"),
            E("Canlı Destek", "Live Support", "/Shared/Client"),
            E("Bilgilendirme", "Information", "/Shared/Client"),
            E("AKTİF KAMPANYA", "ACTIVE CAMPAIGN", "/Shared/Campaign"),
            E("Aktif kampanya", "Active campaign", "/Shared/Campaign"),
            E("Ürünleri İncele", "Browse Products", "/Shared/Campaign"),

            E("Banka Havalesi ve EFT ile dijital lisans satın alma", "Purchase a digital license by bank transfer", "/Shared/Client"),
            E("Bildirim gönderin, ödeme onayından sonra lisansınız hesabınıza tanımlansın", "Submit your notification and your license will be assigned to your account after payment approval", "/Shared/Client"),
            E("Bizi Takip Edin", "Follow Us", "/Shared/Client"),
            E("Canlı destek bağlantısı hazırlanıyor...", "Preparing live support connection...", "/Shared/Client"),
            E("Canlı destek penceresini aç", "Open live support window", "/Shared/Client"),
            E("Canlı destek penceresini kapat", "Close live support window", "/Shared/Client"),
            E("Copyright © 2026 Nevzat SÜRÜCÜ - NSX Yazılım", "Copyright © 2026 Nevzat SÜRÜCÜ - NSX Software", "/Shared/Client"),
            E("Destek Talebi Oluştur", "Create Support Request", "/Shared/Client"),
            E("Gönder", "Send", "/Shared/Client"),
            E("Havale/EFT ile Satın Alma", "Purchase by Bank Transfer", "/Shared/Client"),
            E("Kayıt Ol", "Register", "/Shared/Client"),
            E("Mail / Telefon", "Email / Phone", "/Shared/Client"),
            E("Menüyü Aç", "Open Menu", "/Shared/Client"),
            E("Mesajınız", "Your message", "/Shared/Client"),
            E("Mesajınızı yazın...", "Write your message...", "/Shared/Client"),
            E("Müşteri Hizmetleri", "Customer Service", "/Shared/Client"),
            E("NSX Yazılım Destek Merkezi", "NSX Software Support Center", "/Shared/Client"),
            E("NSX Yazılım Facebook hesabı", "NSX Software Facebook account", "/Shared/Client"),
            E("NSX Yazılım Instagram hesabı", "NSX Software Instagram account", "/Shared/Client"),
            E("NSX Yazılım TikTok hesabı", "NSX Software TikTok account", "/Shared/Client"),
            E("NSX Yazılım WhatsApp destek hattı", "NSX Software WhatsApp support line", "/Shared/Client"),
            E("NSX Yazılım X hesabı", "NSX Software X account", "/Shared/Client"),
            E("NSX Yazılım YouTube kanalı", "NSX Software YouTube channel", "/Shared/Client"),
            E("NSX Yazılım; işletmelerin satış, servis ve performans süreçleri için modern, güvenilir ve kullanıcı dostu dijital çözümler geliştirir.", "NSX Software develops modern, reliable and user-friendly digital solutions for business sales, service and performance workflows.", "/Shared/Client"),
            E("NSX'e Hoş Geldiniz...", "Welcome to NSX...", "/Shared/Client"),
            E("Oto Galeri Programı", "Auto Dealership Software", "/Shared/Client"),
            E("Oto Tamir Programı", "Auto Repair Software", "/Shared/Client"),
            E("Servis Live Programı", "Live Service Software", "/Shared/Client"),
            E("Siparişlerim", "My Orders", "/Shared/Client"),
            E("Size nasıl ulaşalım?", "How can we reach you?", "/Shared/Client"),
            E("Size nasıl yardımcı olabiliriz?", "How can we help you?", "/Shared/Client"),
            E("WhatsApp Destek", "WhatsApp Support", "/Shared/Client"),
            E("Yeni Ürünler", "New Products", "/Shared/Client"),
            E("Yenilikler, ürün ipuçları ve duyurular için sosyal hesaplarımızdayız.", "Follow our social channels for updates, product tips and announcements.", "/Shared/Client"),
            E("Yukarı Çık", "Back to Top", "/Shared/Client"),
            E("© @DateTime.Now.Year NSX Yazılım. Tüm hakları saklıdır.", "© @DateTime.Now.Year NSX Software. All rights reserved.", "/Shared/Client"),
            E("Çok Satanlar", "Best Sellers", "/Shared/Client"),
            E("Ücretsiz PC Hızlandırma", "Free PC Performance Tool", "/Shared/Client"),
            E("Ücretsiz Teknik Servis Programı", "Free Technical Service Software", "/Shared/Client"),
            E("Ücretsiz Veresiye Programı", "Free Receivables Software", "/Shared/Client"),
            E("Ürün, lisans veya yazılım ara...", "Search products, licenses or software...", "/Shared/Client"),
            E("İletişim", "Contact", "/Shared/Client"),
            E("İşlem tamamlandı.", "Operation completed.", "/Shared/Client"),
            E("Şifremi Unuttum", "Forgot Password", "/Shared/Client"),
            E("☰ KATEGORİLER", "☰ CATEGORIES", "/Shared/Client"),

            E("KURUMSAL MENÜ", "CORPORATE MENU", "/Shared/Corporate"),
            E("WhatsApp destek", "WhatsApp support", "/Shared/Corporate"),
            E("Yardıma mı ihtiyacınız var?", "Need help?", "/Shared/Corporate"),
            E("İletişim ve Destek", "Contact & Support", "/Shared/Corporate"),

            E("YAZILIM", "SOFTWARE", "/Shared/InstallGuide"),
            E("🔒 Yalnızca", "🔒 Only", "/Shared/InstallGuide")
        };
    }
}
