namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Curated static UI text for the authenticated customer account area.
    ///
    /// /Account pages can render customer/order/license data, so the route itself remains
    /// machine-translation restricted. Only compile-time UI literals from this allowlist are
    /// reclassified under /Public/AccountUi and may enter the translation queue. Runtime user,
    /// order, license, e-mail, token and product values are never added to this safe catalog.
    /// </summary>
    public static class LocalizationPublicAccountSeedData
    {
        private static readonly string[] SafeAccountAreas =
        {
            "/Account/MyAccount",
            "/Account/Orders",
            "/Account/OrderDetail",
            "/Account/Licenses",
            "/Account/Downloads",
            "/Account/ChangePassword",
            "/Account/Nav"
        };

        public static readonly IReadOnlyList<LocalizationSeedItem> Items = BuildItems();

        public static readonly IReadOnlyDictionary<string, string> EnglishFallbackByKey = Items
            .GroupBy(x => LocalizationTextKey.Create(x.Source), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last().English, StringComparer.Ordinal);

        public static bool IsAccountUiRequestPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalized = path.Trim();
            if (normalized.Length > 1)
                normalized = normalized.TrimEnd('/');

            return SafeAccountAreas.Any(area =>
                normalized.Equals(area, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(area + "/", StringComparison.OrdinalIgnoreCase));
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

            foreach (var seed in LocalizationEnglishAccountSeedData.Items)
            {
                if (!SafeAccountAreas.Contains(seed.Area, StringComparer.OrdinalIgnoreCase))
                    continue;

                Add(seed.Source, seed.English, "/Public/AccountUi" + seed.Area);
            }

            // Razor splits the greeting around the dynamic customer name, therefore the
            // visible prefix must be a first-class localization resource of its own.
            Add("Merhaba,", "Hello,", "/Public/AccountUi/Account/MyAccount");

            // Authentication feedback is rendered after redirect inside My Account. These
            // values are fixed application messages, not customer data, so they are safe to
            // translate for every active language.
            Add("Giriş başarılı. Hesabına hoş geldin.", "Signed in successfully. Welcome to your account.", "/Public/AccountUi/Account/MyAccount");
            Add("Üyelik oluşturuldu. Hesabına hoş geldin.", "Your account has been created. Welcome to your account.", "/Public/AccountUi/Account/MyAccount");

            // Authenticated account action feedback that can otherwise remain Turkish when
            // a newly-added language has not seen the literal before.
            Add("Bu lisans aktif değil.", "This license is not active.", "/Public/AccountUi/Account/Licenses");
            Add("Bu lisans için offline etkinleştirme kapalı.", "Offline activation is disabled for this license.", "/Public/AccountUi/Account/Licenses");
            Add("Offline lisans dosyası için önce programdan en az bir kez online etkinleştirme yapılmalı.", "The software must be activated online at least once before creating an offline license file.", "/Public/AccountUi/Account/Licenses");
            Add("Kurtarma kodu üretilemedi. Önce programdan bir kez online lisans etkinleştirme yapılmalı.", "A recovery code could not be created. Activate the license online from the software at least once first.", "/Public/AccountUi/Account/Licenses");
            Add("Kurtarma lisans kodu e-posta adresinize gönderildi.", "The recovery license code has been sent to your email address.", "/Public/AccountUi/Account/Licenses");
            Add("Kurtarma kodu e-postası gönderilemedi. Lütfen daha sonra tekrar deneyin.", "The recovery code email could not be sent. Please try again later.", "/Public/AccountUi/Account/Licenses");
            Add("Bu ürüne ait aktif lisansınız bulunamadı.", "No active license was found for this product.", "/Public/AccountUi/Account/Downloads");
            Add("İndirme dosyası bulunamadı.", "The download file could not be found.", "/Public/AccountUi/Account/Downloads");
            Add("Dosya sunucuda bulunamadı.", "The file could not be found on the server.", "/Public/AccountUi/Account/Downloads");
            Add("Mevcut şifre hatalı.", "The current password is incorrect.", "/Public/AccountUi/Account/ChangePassword");
            Add("Yeni şifre mevcut şifreyle aynı olmamalıdır.", "The new password must be different from the current password.", "/Public/AccountUi/Account/ChangePassword");
            Add("Şifreniz başarıyla değiştirildi.", "Your password has been changed successfully.", "/Public/AccountUi/Account/ChangePassword");

            return items;
        }
    }
}
