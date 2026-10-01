namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Curated French repairs for the small set of public/account UI strings that were
    /// observed leaking Turkish or provider placeholder markup on the live site.
    /// These are fixed application labels only; no customer or dynamic content is included.
    /// </summary>
    public static class LocalizationFrenchRepairSeedData
    {
        public static readonly IReadOnlyList<(string Source, string French, string Area)> Items =
            new (string Source, string French, string Area)[]
            {
                ("Ana Sayfa", "Accueil", "/Shared/Header"),
                ("Videolar", "Vidéos", "/Shared/Header"),
                ("Rehberler", "Guides", "/Shared/Header"),
                ("Hesabım", "Compte", "/Shared/Footer"),
                ("Lisanslarım", "Mes licences", "/Shared/Footer"),
                ("İndirmelerim", "Mes téléchargements", "/Shared/Footer"),
                ("Bizi takip edin", "Suivez-nous", "/Shared/Footer"),

                ("uygulanabilir rehberler.", "guides pratiques.", "/Blog"),
                ("saha deneyimi", "expérience terrain", "/Blog"),
                ("Teknoloji", "Technologie", "/Blog"),

                ("1. Kapsam", "1. Champ d’application", "/Home/Returns"),

                ("NSX ÜYE PANELİ", "ESPACE MEMBRE NSX", "/Account/MyAccount"),
                ("ÜYE PANELİ", "ESPACE MEMBRE", "/Account/Nav"),
                ("Merhaba,", "Bonjour,", "/Account/MyAccount"),
                ("Siparişlerinizi, lisanslarınızı ve indirme haklarınızı tek merkezden yönetin.", "Gérez vos commandes, licences et accès aux téléchargements depuis un seul espace.", "/Account/MyAccount"),
                ("Güvenli hesap", "Compte sécurisé", "/Account/MyAccount"),
                ("Lisans yönetimi", "Gestion des licences", "/Account/MyAccount"),
                ("Dijital teslimat", "Livraison numérique", "/Account/MyAccount"),
                ("HESAP E-POSTASI", "E-MAIL DU COMPTE", "/Account/MyAccount"),
                ("Güvenli müşteri hesabı", "COMPTE CLIENT SÉCURISÉ", "/Account/MyAccount"),
                ("Genel Bakış", "Vue d’ensemble", "/Account/Nav"),
                ("Siparişlerim", "Mes commandes", "/Account/Nav"),
                ("Şifre ve Güvenlik", "Mot de passe et sécurité", "/Account/Nav"),
                ("Çıkış Yap", "Se déconnecter", "/Account/Nav"),
                ("SİPARİŞLER", "COMMANDES", "/Account/MyAccount"),
                ("AKTİF LİSANSLAR", "LICENCES ACTIVES", "/Account/MyAccount"),
                ("İNDİRME HAKKI", "ACCÈS AUX TÉLÉCHARGEMENTS", "/Account/MyAccount"),
                ("toplam lisans →", "licences au total →", "/Account/MyAccount"),
                ("toplam lisans", "licences au total", "/Account/Licenses"),
                ("Tüm siparişleri görüntüle →", "Voir toutes les commandes →", "/Account/MyAccount"),
                ("Dosyalarıma git →", "Accéder à mes fichiers →", "/Account/MyAccount"),
                ("SON İŞLEMLER", "ACTIVITÉ RÉCENTE", "/Account/MyAccount"),
                ("Son siparişler", "Commandes récentes", "/Account/MyAccount"),
                ("Tümünü görüntüle", "Tout afficher", "/Account/MyAccount"),
                ("Henüz siparişiniz bulunmuyor.", "Vous n’avez encore aucune commande.", "/Account/MyAccount"),
                ("İşletmenize uygun NSX yazılımlarını inceleyerek başlayabilirsiniz.", "Commencez par découvrir les logiciels NSX adaptés à votre entreprise.", "/Account/MyAccount"),
                ("Yazılımları İncele", "Découvrir les logiciels", "/Account/MyAccount"),
                ("LİSANSLAR", "LICENCES", "/Account/MyAccount"),
                ("Son lisanslar", "Licences récentes", "/Account/MyAccount"),
                ("Lisanslarıma git", "Voir mes licences", "/Account/MyAccount"),
                ("Üyelik oluşturuldu. Hesabına hoş geldin.", "Votre compte a été créé. Bienvenue dans votre espace.", "/Account/MyAccount"),
                ("Giriş başarılı. Hesabına hoş geldin.", "Connexion réussie. Bienvenue dans votre espace.", "/Account/MyAccount")
            };
    }
}
