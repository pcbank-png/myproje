namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Curated, static UI text for the anonymous account flow.
    ///
    /// The rest of /Account stays outside machine translation because it can render
    /// customer, order and license data. These entries are safe to classify under
    /// /Public/Auth: every source value is compiled into the application and no request,
    /// form, e-mail, token or database value is ever added to this catalog.
    /// </summary>
    public static class LocalizationPublicAuthSeedData
    {
        private static readonly IReadOnlyDictionary<string, string> PublicAreaByAccountPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["/Account/Login"] = "/Public/Auth/Login",
                ["/Account/Register"] = "/Public/Auth/Register",
                ["/Account/ForgotPassword"] = "/Public/Auth/ForgotPassword",
                ["/Account/ForgotPasswordConfirmation"] = "/Public/Auth/ForgotPasswordConfirmation",
                ["/Account/ResetPassword"] = "/Public/Auth/ResetPassword"
            };

        public static readonly IReadOnlyList<LocalizationSeedItem> Items = BuildItems();

        // Non-Turkish auth pages must never leak Turkish while a newly-added language is
        // still waiting in the background translation queue. Only curated compile-time
        // UI literals are present in this map, so reviewed English is a safe temporary
        // fallback for every public authentication screen.
        public static readonly IReadOnlyDictionary<string, string> EnglishFallbackByKey = Items
            .GroupBy(x => LocalizationTextKey.Create(x.Source), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last().English, StringComparer.Ordinal);

        public static bool IsPublicAuthRequestPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalized = path.Trim();
            if (normalized.Length > 1)
                normalized = normalized.TrimEnd('/');

            return PublicAreaByAccountPath.ContainsKey(normalized);
        }

        private static IReadOnlyList<LocalizationSeedItem> BuildItems()
        {
            var items = new List<LocalizationSeedItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string source, string english, string publicArea)
            {
                if (string.IsNullOrWhiteSpace(source)
                    || string.IsNullOrWhiteSpace(english)
                    || string.IsNullOrWhiteSpace(publicArea))
                {
                    return;
                }

                var key = LocalizationTextKey.Create(source);
                if (!seen.Add(key))
                    return;

                items.Add(new LocalizationSeedItem(source, english, publicArea));
            }

            // Reuse the reviewed English catalog already shipped by the project, but only
            // for the five anonymous auth endpoints in the allowlist above.
            var existingSeeds = LocalizationEnglishAccountSeedData.Items
                .Concat(LocalizationEnglishOfflineUiSeedData.Items)
                .Concat(LocalizationCapturedContentSeedData.Items);

            foreach (var seed in existingSeeds)
            {
                if (PublicAreaByAccountPath.TryGetValue(seed.Area, out var publicArea))
                    Add(seed.Source, seed.English, publicArea);
            }

            // Shared/generic entries that are visibly used by the public auth pages but
            // were previously catalogued under the restricted /Account root.
            Add("Lisans yönetimi", "License management", "/Public/Auth/Login");
            Add("E-postanızı Kontrol Edin", "Check Your Email", "/Public/Auth/ForgotPasswordConfirmation");
            Add("Ücretsiz Üyelik", "Free Membership", "/Public/Auth/Register");
            Add("Yeni Şifre Belirle", "Set a New Password", "/Public/Auth/ResetPassword");
            Add("Yeni Şifre Belirle | NSX Yazılım", "Set a New Password | NSX Software", "/Public/Auth/ResetPassword");

            // Visible auth literals that were not present in the old English account pack.
            Add("Cep telefonu", "Mobile phone", "/Public/Auth/Register");
            Add("Cep telefonu (isteğe bağlı)", "Mobile phone (optional)", "/Public/Auth/Register");
            Add("Türkiye için 05XXXXXXXXX; diğer ülkeler için + ülke koduyla yazabilirsiniz.", "For Türkiye, enter 05XXXXXXXXX; for other countries, include the + country code.", "/Public/Auth/Register");
            Add("En az 6 karakter", "At least 6 characters", "/Public/Auth/Register");
            Add("Benzersiz olsun", "Make it unique", "/Public/Auth/ResetPassword");
            Add("Göster", "Show", "/Public/Auth/Login");
            Add("Gizle", "Hide", "/Public/Auth/Login");
            Add("Şifreyi gizle", "Hide password", "/Public/Auth/Login");

            // DataAnnotations and controller feedback rendered on the same public pages.
            // Keeping them here also lets the server translate jQuery's data-val-* values.
            Add("E-posta zorunludur.", "Email is required.", "/Public/Auth/Login");
            Add("Geçerli bir e-posta adresi girin.", "Enter a valid email address.", "/Public/Auth/Login");
            Add("Şifre zorunludur.", "Password is required.", "/Public/Auth/Login");
            Add("E-posta veya şifre hatalı.", "The email address or password is incorrect.", "/Public/Auth/Login");
            Add("Ad soyad zorunludur.", "Full name is required.", "/Public/Auth/Register");
            Add("Ad soyad en fazla 150 karakter olabilir.", "Full name can be at most 150 characters.", "/Public/Auth/Register");
            Add("E-posta en fazla 200 karakter olabilir.", "Email can be at most 200 characters.", "/Public/Auth/Register");
            Add("Cep telefonu zorunludur.", "Mobile phone is required.", "/Public/Auth/Register");
            Add("Cep telefonu 11 haneli olmalıdır. Örnek: 05XXXXXXXXX", "Mobile phone must contain 11 digits. Example: 05XXXXXXXXX", "/Public/Auth/Register");
            Add("Telefon numarası geçersiz. Türkiye için 05XXXXXXXXX, diğer ülkeler için + ülke kodu kullanın.", "The phone number is invalid. Use 05XXXXXXXXX for Türkiye or include the + country code for other countries.", "/Public/Auth/Register");
            Add("Şifre en az 6 karakter olmalıdır.", "Password must be at least 6 characters.", "/Public/Auth/Register");
            Add("Şifre tekrarı zorunludur.", "Password confirmation is required.", "/Public/Auth/Register");
            Add("Şifreler eşleşmiyor.", "Passwords do not match.", "/Public/Auth/Register");
            Add("Bu e-posta adresi zaten kayıtlı.", "This email address is already registered.", "/Public/Auth/Register");
            Add("Bu cep telefonu zaten kayıtlı.", "This mobile phone is already registered.", "/Public/Auth/Register");
            Add("Şifre sıfırlama e-postası gönderilemedi. E-posta ayarlarını kontrol edin.", "The password reset email could not be sent. Check the email settings.", "/Public/Auth/ForgotPassword");
            Add("Şifre yenileme bağlantısı geçersiz veya süresi dolmuş.", "The password reset link is invalid or has expired.", "/Public/Auth/ForgotPassword");
            Add("Eğer bu e-posta ile kayıtlı hesabınız varsa şifre yenileme bağlantısı gönderildi.", "If an account exists for this email address, a password reset link has been sent.", "/Public/Auth/ForgotPasswordConfirmation");
            Add("Demo indirmek için önce üye girişi yapmalısınız.", "Please sign in before downloading the demo.", "/Public/Auth/Login");
            Add("Ücretsiz indirmek için önce üye girişi yapmalısınız.", "Please sign in before downloading the free software.", "/Public/Auth/Login");
            Add("Yeni şifre zorunludur.", "A new password is required.", "/Public/Auth/ResetPassword");
            Add("Yeni şifre en az 6 karakter olmalıdır.", "The new password must be at least 6 characters.", "/Public/Auth/ResetPassword");
            Add("Şifreniz başarıyla yenilendi. Yeni şifrenizle giriş yapabilirsiniz.", "Your password has been reset successfully. You can now sign in with your new password.", "/Public/Auth/Login");

            // Free-product conversion flow: remove purchase language between CTA and download.
            Add("Ücretsiz İndirme İçin Giriş", "Sign In for Free Download", "/Public/Auth/Login");
            Add("NSX Veresiye Takip Pro Free indirmesini başlatmak için hesabınıza güvenle giriş yapın. Ödeme veya kart bilgisi gerekmez.", "Sign in securely to start the NSX Veresiye Takip Pro Free download. No payment or card details are required.", "/Public/Auth/Login");
            Add("ÜCRETSİZ İNDİRME", "FREE DOWNLOAD", "/Public/Auth/Login");
            Add("İndirmenize", "You are", "/Public/Auth/Login");
            Add("bir adım kaldı.", "one step away from your download.", "/Public/Auth/Login");
            Add("NSX hesabınıza giriş yapın; girişten sonra ücretsiz kurulum dosyanız otomatik hazırlanır. Ödeme, havale veya kart bilgisi gerekmez.", "Sign in to your NSX account; your free installer will be prepared automatically. No payment, bank transfer, or card details are required.", "/Public/Auth/Login");
            Add("Ücretsiz indirme avantajları", "Free download benefits", "/Public/Auth/Login");
            Add("Ödeme yok", "No payment", "/Public/Auth/Login");
            Add("Kart bilgisi yok", "No card details", "/Public/Auth/Login");
            Add("Ücretsiz lisans", "Free license", "/Public/Auth/Login");
            Add("NSX VERESİYE TAKİP PRO FREE", "NSX VERESİYE TAKİP PRO FREE", "/Public/Auth/Login");
            Add("Ücretsiz lisansınız hesabınıza otomatik bağlanır.", "Your free license is linked to your account automatically.", "/Public/Auth/Login");
            Add("Giriş yaptıktan sonra indirme ekranına geri dönersiniz. Ücretsiz lisans oluşturulur ve güvenli Windows kurulum dosyası otomatik indirilir.", "After signing in, you return to the download screen. A free license is created and the secure Windows installer downloads automatically.", "/Public/Auth/Login");
            Add("Giriş yapın", "Sign in", "/Public/Auth/Login");
            Add("Mevcut NSX hesabınızla güvenli şekilde oturum açın.", "Sign in securely with your existing NSX account.", "/Public/Auth/Login");
            Add("Herhangi bir ödeme adımı olmadan hesabınıza tanımlansın.", "It is added to your account without any payment step.", "/Public/Auth/Login");
            Add("İndirin", "Download", "/Public/Auth/Login");
            Add("Kurulum dosyanız birkaç saniye içinde otomatik başlasın.", "Your installer starts automatically within a few seconds.", "/Public/Auth/Login");
            Add("Girişten sonra ücretsiz indirmeniz otomatik devam eder.", "Your free download continues automatically after sign-in.", "/Public/Auth/Login");

            Add("Ücretsiz İndirme İçin Hesap Oluştur", "Create an Account for Free Download", "/Public/Auth/Register");
            Add("NSX Veresiye Takip Pro Free indirmesini başlatmak için ücretsiz NSX hesabınızı oluşturun. Kart bilgisi veya ödeme gerekmez.", "Create your free NSX account to start the NSX Veresiye Takip Pro Free download. No card details or payment are required.", "/Public/Auth/Register");
            Add("30 saniyede hesabınızı oluşturun,", "Create your account in 30 seconds,", "/Public/Auth/Register");
            Add("indirme başlasın.", "and start the download.", "/Public/Auth/Register");
            Add("NSX Veresiye Takip Pro Free için ücretsiz hesabınızı oluşturun. Kayıt tamamlanınca lisansınız tanımlanır ve indirme otomatik devam eder.", "Create your free account for NSX Veresiye Takip Pro Free. Once registration is complete, your license is assigned and the download continues automatically.", "/Public/Auth/Register");
            Add("Ömür boyu ücretsiz", "Free for life", "/Public/Auth/Register");
            Add("Ücretsiz indirme için yalnızca NSX hesabınız yeterli.", "An NSX account is all you need for the free download.", "/Public/Auth/Register");
            Add("Kart veya banka bilgisi istemiyoruz. Hesap, ücretsiz lisansınızı size tanımlamak ve güncel kurulum dosyasına güvenli erişim sağlamak için kullanılır.", "We do not ask for card or bank details. Your account is used to assign your free license and provide secure access to the latest installer.", "/Public/Auth/Register");
            Add("Ücretsiz hesap", "Free account", "/Public/Auth/Register");
            Add("Temel bilgilerinizi girerek NSX hesabınızı oluşturun.", "Create your NSX account with your basic information.", "/Public/Auth/Register");
            Add("Otomatik lisans", "Automatic license", "/Public/Auth/Register");
            Add("Ücretsiz lisansınız hesabınıza kendiliğinden tanımlansın.", "Your free license is assigned to your account automatically.", "/Public/Auth/Register");
            Add("Hemen indirin", "Download now", "/Public/Auth/Register");
            Add("Kayıt tamamlanınca kurulum dosyanız otomatik başlasın.", "Your installer starts automatically when registration is complete.", "/Public/Auth/Register");
            Add("Hesabınız oluşturulunca ücretsiz indirmeniz otomatik devam eder.", "Your free download continues automatically after your account is created.", "/Public/Auth/Register");

            return items;
        }
    }
}
