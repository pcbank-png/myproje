# GitHub kaynak kopyası — 2 Ekim 2026

Bu proje `pcbank-png/myproje` deposundaki `web-mobil` klasörüne eklenmek üzere hazırlanmıştır. Depodaki Veri Kurtarma projesi korunur.

Kopya web/backend, Expo mobil, kaynak varlıklar, testler ve düzenleme raporunu kapsar. node_modules, bin/obj, Expo önbelleği, üretilmiş paketler, eski ZIP/backup dosyaları, yerel .env, özel anahtarlar, App_Data veritabanları ve kullanıcı yüklemeleri dahil değildir.

GitHub herkese açıktır. Yönetici, SMTP, banka ve lisans yapılandırmasının yerel değerleri Windows User Secrets'a taşındı; kaynak yapılandırmasında boş bırakıldı. Lisans denetleyicilerindeki gömülü anahtarlar kaldırıldı. Eski varsayılan anahtarın Production V2'de reddedilmesi parmak izi ile korunuyor. Anahtarlar boşken lisans API erişimi verilmez.

Yeni bilgisayarda web geliştirme için kendi bağlantınızı ve ayarlarınızı `dotnet user-secrets set` ile `web/NSYazilim.Web.csproj` üzerinden tanımlayın. Sunucuda environment veya sunucunun secret yönetimini kullanın:

- `ConnectionStrings__DefaultConnection`
- `AdminSeed__FullName`, `AdminSeed__Email`, `AdminSeed__Password`
- `EmailSettings__Host`, `EmailSettings__Port`, `EmailSettings__UserName`, `EmailSettings__Password`, `EmailSettings__FromEmail`, `EmailSettings__ToEmail`, `EmailSettings__FromName`
- `BankTransfer__BankName`, `BankTransfer__AccountHolder`, `BankTransfer__Iban`, `BankTransfer__Branch`, `BankTransfer__DescriptionPrefix`
- `LicenseApi__ApiKey` ve eski ücretsiz istemciler kullanılıyorsa `LicenseApi__LegacyFreeVeresiyeApiKey`
- Uzaktan destek anahtarları ve diğer yayın koşulları: [düzenleme raporu](RELEASE_FIXES_20261002.md).

Mobil için `frontend/.env.example` dosyasını `.env` olarak kopyalayıp Expo proje kimliğini tamamlayın; `npm ci` kullanın. Production APNs/FCM ve sunucu sırları kaynak depoda bulunmaz. Şifre/anahtar değişimi veya canlı sunucu ayarı bu kopyalama işleminde yapılmadı.
