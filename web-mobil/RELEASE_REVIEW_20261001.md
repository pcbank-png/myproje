# Yayın öncesi inceleme — 1 Ekim 2026

**Tarihsel inceleme:** Aşağıdaki bulgular 2 Ekim'de düzenlendi. Güncel uygulama, test sonuçları ve yayın koşulları için [düzenleme raporuna](RELEASE_FIXES_20261002.md) bakın. Aşağıdaki açıklamalar inceleme anındaki durumu kaydeder.

Kapsam: ASP.NET web/backend ve Expo mobil projenin derleme, tip, lint, bağımlılık kontrolleri; oturum, para girişi, bildirim ve uzaktan yardım akışlarının kaynak incelemesi. Mobil giriş, ana ekran ve borç formu 390×844 tarayıcı önizlemesinde mock veriyle incelendi. Canlı veritabanı çalıştırılmadı: web başlangıcında şema hazırlama ve arka plan işçileri var. Web tasarımı kaynak üzerinden değerlendirildi; çalışan web ekranlarında görsel doğrulama yapılmadı. Bu çalışma tüm uçların penetrasyon testi veya gerçek cihaz kabul testi değildir.

## P1 — Yayından önce giderilmeli

### 1. Uzaktan yardım operatör uçları kimlik doğrulaması istemiyor

Kaynak: [Program.cs:1122](C:/Users/Server/Desktop/web-mobil/web/Program.cs:1122), [istemci listesi:1382](C:/Users/Server/Desktop/web-mobil/web/Program.cs:1382), [bağlantı isteği:1461](C:/Users/Server/Desktop/web-mobil/web/Program.cs:1461), [operatör oturumu:1530](C:/Users/Server/Desktop/web-mobil/web/Program.cs:1530).

`/api/uzaktan-yardim/v1/operator/*` rotalarında RequireAuthorization veya eşdeğer operatör kimliği kontrolü yok. AddAuthorization varsayılanıyla anonim isteklere engel oluşturulmuyor. Çevrimiçi müşteri/cihaz bilgileri listelenebiliyor; istemciden gelen operatör adıyla bağlantı isteği oluşturulabiliyor. Kabul edilmiş isteğin oturum rotası operatör erişim anahtarını döndürüyor. WebSocket'in anahtar kontrolü, anahtarı veren bu rotanın anonim olmasını telafi etmiyor. Müşteri onayı mevcut; onaysız doğrudan masaüstü kontrolü doğrulanmış değil. Ağda veya reverse proxy'de ek koruma bulunup bulunmadığı bu incelemede doğrulanmadı.

Çözüm: Operatör rotaları için ayrı yetki politikası ve destek rolü/operatör kimliği; istek ve oturum sahipliği kontrolü. İstemci rotalarının kendi cihaz token doğrulaması korunmalı. Anonim/yetkisiz rol/başka operatör testlerinde 401/403 dönmeli, meşru destek akışı devam etmeli.

### 2. Borç ve tahsilat tutarı yanlış kaydedilebilir

Kaynak: [hareket-new.tsx:96](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/app/hareket-new.tsx:96).

Mevcut parser tüm noktaları siliyor ve parseFloat kullanıyor. Kaynaktan çıkarılan gerçek parser çalıştırılarak sonuç doğrulandı:

| Girdi | İşlenen tutar |
| --- | ---: |
| `100,50` | 100,50 |
| `100.50` | 10.050,00 |
| `1.234,56` | 1.234,56 |
| `12abc` | 12,00 |
| `1,2,3` | 1,20 |

Noktalı ondalık giriş/yapıştırma 100 kat yanlış borç veya tahsilat oluşturabilir. Formun `amount > 0` kontrolü bunu durdurmuyor. Canlı kayıt gönderilmedi.

Çözüm: Tam girdiyi doğrulayan para parser'ı; kabul edilen ayırıcıların açık kuralları; bozuk/belirsiz girdiye hata; en çok iki ondalık ve tutar sınırı. Kaydetmeden önce normalize edilmiş tutarı görünür göster. Türkçe ve noktalı ondalık klavyelerle doğrula.

### 3. Başka hesapla girişte önceki hesabın bildirimleri kalıyor

Kaynak: [auth-context.tsx:95](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/auth/auth-context.tsx:95), [inbox.ts:17](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/notifications/inbox.ts:17), [inbox birleştirme:99](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/notifications/inbox.ts:99).

Bildirim deposu tüm hesaplar için tek `nsx.inbox.v1` anahtarını kullanıyor. Logout sadece oturumu ve firma listesini temizliyor. Yeni hesap inbox senkronizasyonu, sunucuda olmayan eski yerel bildirimleri de tutuyor. Aynı telefonda farklı hesaba geçince eski müşteri adı/tutarları yeni oturumda görülebilir. React Query önbelleği de logout'ta temizlenmiyor; şirket kimliği içeren anahtarlar bu ikinci riski azaltıyor ama hesap sınırı açık tanımlı değil.

Çözüm: Depoyu tenant + cihaz/hesap kimliğine göre ayır; logout/hesap değişiminde belleği, bildirim dedupe durumunu ve ilgili query cache'i sıfırla; eski oturumun devam eden isteklerinin yeni depoya yazmasını önle. A hesabı → çıkış → B hesabı testi yapılmalı.

### 4. Mobil tip kontrolü başarısız

`tsc --noEmit`: **7 hata**. Customer tipinde version yok; müşteri düzenleme/silme ekranları version kullanıyor. RN sürümündeki StyleSheet tipleri absoluteFillObject kullanımını kabul etmiyor.

- [musteriler.tsx:232](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/app/(tabs)/musteriler.tsx:232)
- [musteri-new.tsx:66](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/app/musteri-new.tsx:66)
- [musteri detay:86](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/app/musteri/[id].tsx:86)
- [cihaz-davet.tsx:381](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/app/cihaz-davet.tsx:381)
- [qr-scanner.tsx:416](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/app/qr-scanner.tsx:416) ve 439
- [empty-state.tsx:147](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/components/empty-state.tsx:147)

Çözüm: Backend DTO, HTTP mapping ve Customer.version sözleşmesini birlikte kontrol et; sadece any ile susturma. Absolute fill kullanımlarını mevcut RN API'sine geçir. Metro export tip kontrolü çalıştırmadığından paketleme başarısı bu hataları çözmüş sayılmaz.

### 5. Bildirim başarısız teslimattan sonra kalıcı kaybolabilir

Kaynak: [CaritakipMobileChangeNotifier.cs:84](C:/Users/Server/Desktop/web-mobil/web/CaritakipCloud/Services/CaritakipMobileChangeNotifier.cs:84), [Deliverer.cs:35](C:/Users/Server/Desktop/web-mobil/web/CaritakipCloud/Services/CaritakipMobileNotificationDeliverer.cs:35).

İşlem bildiriminde dedupe kaydı inbox tesliminden önce yazılıyor. Sonraki DB/inbox yazımı hata verirse catch yalnız logluyor; aynı işlem dedupe yüzünden tekrar gönderilmiyor. Birden fazla cihazda kısmi teslim de olabilir. Push tarafında başarısızlık loglanıyor; kalıcı yeniden deneme/receipt takibi görünmüyor. Inbox'a hiç yazılmamış bildirimi mobil polling kurtaramaz. Bu hata koşulları kaynak incelemesinden çıkarımdır; canlı arıza enjekte edilmedi.

Çözüm: İşlem ve bildirim niyetini kalıcı outbox ile güvenceye al; idempotent cihaz teslimi ve retry/backoff uygula. Expo push ticket/receipt hata yönetimini ekle. Dedupe ve teslimatı tek atomik stratejide ele al; sadece dedupe sırasını değiştirmek çift bildirim riskini artırabilir.

## P2 — Güvenilirlik ve yayın yapılandırması

- **Bildirim silme/okuma hatası gizleniyor.** [inbox.ts:259](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/notifications/inbox.ts:259): sunucu silme başarısız olsa da yerelden siliniyor; sonraki senkronizasyon bildirimi geri getirir. Okundu işareti de geri dönebilir. Sunucu yanıtından sonra eski listeyi yazmak eşzamanlı gelen bildirimi silebilir. Tüm inbox mutasyonlarını sırala; sunucu hatasını bildir veya güvenli retry/tombstone kullan.
- **Kalıcı oturum yazım hataları yutuluyor.** [session.ts:61](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/auth/session.ts:61): SecureStore yazımı başarısız olsa da işlem başarılı sayılıyor. Token rotasyonu sonrası uygulama yeniden başlatıldığında eski token kalabilir. Bellek/kalıcı kayıt tutarlılığı ve kurtarma davranışı tanımlanmalı.
- **Kaynak yapılandırmada düz metin DB kimlik bilgisi var.** [appsettings.json:3](C:/Users/Server/Desktop/web-mobil/web/appsettings.json:3): root kullanıcı ve TLS kapalı bağlantı tanımlı. Değerler bu raporda tekrarlanmadı. Yayında ortam/secrets üzerinden sınırlı DB hesabı kullan; DB uzaktaysa TLS doğrulamasını etkinleştir. Dosya paylaşılmışsa ilgili parolayı değiştir.
- **Lint başarısız:** 15 hata, 4 uyarı / 13 dosya. set-state-in-effect, refs ve immutability kuralları. Hepsi kanıtlanmış çalışma zamanı hatası değildir; yerleri [JSON raporunda](C:/Users/Server/Desktop/web-mobil/mobile-audit-eslint.json). Özellikle animasyon ve form state akışları gözden geçirilmeli.
- **Mobil dependency audit:** `npm audit --omit=dev`: 17 moderate, 0 high, 0 critical. Çoğu Expo/build araç zincirindeki transitif kayıtlardan geliyor; sayım doğrudan telefonda 17 kullanılabilir açık anlamına gelmez. decode-uri-component/query-string zinciri router üzerinden ayrıca değerlendirilmelidir. Npm bazı çözümler için eski Expo ana sürümüne dönüş öneriyor; körlemesine `audit fix --force` uygulanmamalı. [Ham audit](C:/Users/Server/Desktop/web-mobil/mobile-audit-dependencies.json).
- **Production modunu build profilinde kesinleştir.** [api/index.ts:8](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/api/index.ts:8): değişken eksik/geçersizse mock'a geçiliyor. Mevcut `.env` live; EAS production profilinde açık zorunluluk görünmüyor. Production build mock ise hata vererek durmalı; canlı API URL ve proje kimliği build sırasında doğrulanmalı.

## Tasarım önerileri

1. **Para değerleri kesilmesin.** 390×844 mock önizlemede ana ekranın Alacak/Tahsilat/Net Bakiye kutuları `298.000,00 ...` gibi kısalıyor. [AnalysisCol:423](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/app/(tabs)/index.tsx:423). Dar ekranda alt alta veya iki sütun kullan; tam tutar erişilebilir kalsın. Bu gözlem tarayıcı önizlemesinde doğrulandı; native font ölçekleme ayrıca gerçek cihazda kontrol edilmeli.
2. **Ana ekranda son hareketler daha erken görünsün.** Büyük bakiye kartı ve 2×2 hızlı işlem kutuları nedeniyle 390×844 görünümde Son Hareketler sadece başlık olarak kalıyor. Hero kartı biraz kısalt; ana iki işlem belirgin, diğer ikisi daha kompakt olabilir. Net bakiye zaten büyük gösterildiği için alt üçlüde tekrarının alan maliyetini değerlendir.
3. **Açık temada yeşil/sarı metin kontrastını artır.** [theme.ts:36](C:/Users/Server/Desktop/web-mobil/NSX-Cari-Takip-Pro-Mobile-main/frontend/src/theme.ts:36): beyaz/yeşil 2,54:1; beyaz/sarı 2,15:1; beyaz/kırmızı 3,76:1. Bu token çiftleri normal küçük metinde 4,5:1 hedefini karşılamıyor. Koyu metin veya daha koyu renk kullan. [W3C kontrast açıklaması](https://www.w3.org/WAI/WCAG21/Understanding/contrast-minimum). Devre dışı düğmeler bu eşikten muaftır; aktif metin/düğmeler ayrıca denetlenmeli.
4. **Formda tek kaydırma alanı.** Borç formunda müşteri seçimi açıkken sayfa ve müşteri listesi ayrı kayıyor; tarih/açıklama aşağıda kalıyor. Müşteri seçimini aranabilir ayrı bottom sheet'e almak ve seçince kapatmak formu sadeleştirir.
5. **Web'de ortak stil katmanını sadeleştir.** [Admin layout:35](C:/Users/Server/Desktop/web-mobil/web/Views/Shared/_AdminLayout.cshtml:35) birden fazla global/mobile-fix/admin-unified stil katmanı yüklüyor; [PublicHeader](C:/Users/Server/Desktop/web-mobil/web/Views/Shared/_PublicHeader.cshtml) içinde ek inline override'lar var. Renk/font/spacing ve düğme stillerini tek ortak kaynakta topla. Bu bakım önerisidir; görsel çakışma çalışan web ekranında doğrulanmadı.

## Çalıştırılan kontroller

| Kontrol | Sonuç |
| --- | --- |
| Web Release build, --no-restore | Başarılı, 0 hata/0 uyarı (mevcut incremental build) |
| Web NuGet vulnerable + transitive | Kaynaklarda bilinen açık raporlanmadı |
| ReminderTiming mevcut test paketi, --no-build | Başarılı |
| Mobil test-notifications.cjs | Başarılı |
| Expo install --check | SDK bağımlılıkları uyumlu |
| Expo export iOS + Android | Başarılı; native IPA/APK derlemesi değildir |
| Mobil TypeScript | Başarısız, 7 hata |
| Mobil ESLint app + src | Başarısız, 15 hata/4 uyarı |
| npm audit --omit=dev | 17 moderate |
| Mobil mock tarayıcı görsel inceleme | Giriş, ana ekran, borç formu; 390×844 |

## Son kabul için kalanlar

P1 düzeltmelerden sonra hesap izolasyonu, yetkisiz API erişimi, para parser'ı ve transient bildirim hataları test edilmeli. Gerçek iPhone ve Android production/development build'de uygulama kapalı/arka planda/ön planda bildirim alma ve detay açma; firma değişimi; token yenileme; offline silme; çakışan müşteri düzenleme/silme denenmeli. Android cihaz testi henüz kullanıcı tarafından alınmadı. Web'de staging veritabanıyla login, ödeme/webhook, lisans ve uzaktan yardım kabul testleri; mobilde TestFlight/Android iç test native build'i tamamlanmadan yayın onayı verilmemeli.
