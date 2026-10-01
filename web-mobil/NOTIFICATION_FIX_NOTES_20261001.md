# Web → mobil bildirim düzeltmesi — 1 Ekim 2026

Sunucuda inbox oluşturulmadan gönderilen veri yenileme olayı ve mobilin tahsilat/borç için sessiz yenilemesi bildirim gecikmesine yol açabiliyordu. Bildirimler artık inbox kaydı yazıldıktan sonra cihaza özel `notificationAvailable` SignalR olayıyla taşınır. Zamanlanmış hatırlatma worker'ı da aynı iletim yolunu kullanır; güncel zamanlama kuralları aşağıda açıklanmıştır.

Her yeni tahsilat ve borç ayrı bildirim üretir. Mobilde Android yerel bildirim kanalı `trigger.channelId` ile seçilir; hatırlatmalar `nsx-reminders`, işlemler `nsx-cari` kullanır. Push ve SignalR yerel bildirimi aynı kimlikle tekilleştirilir. Eşzamanlı gelen kayıtlar sırayla saklanır. Başarısız token kaydı aktif uygulamada yeniden denenir; uygulamaya dönüşte token kaydı yenilenir. Yeni kurulumlarda borç bildirimleri de varsayılan açıktır.

## Doğrulama

- `dotnet build web/NSYazilim.Web.csproj --no-restore`: başarılı; mevcut `Views/Admin/Users.cshtml:161` null uyarısı var.
- Mobil `node scripts/test-notifications.cjs`: başarılı. Üç kategori, eşzamanlı kayıtlar, push önce/sonra geliş, tekrar olaylar, yerel bildirimin başarısızlığı sonrası tekrar deneme, Android kanal seçimi, sessiz ilk yükleme, sorgulama yedeği ve token kaydı test edildi. Ağ ve native telefon adaptörleri testte taklit edildi.
- Mobil `node node_modules/eslint/bin/eslint.js src/notifications src/realtime`: başarılı.
- Genel TypeScript kontrolü: bildirim değişikliklerinde hata yok. Mevcut 7 hata: müşteri ekranlarında `Customer.version` (3), davet/QR/empty-state ekranlarında `StyleSheet.absoluteFillObject` (4).
- Gerçek telefon, canlı veritabanı ve Expo teslimatı bu ortamda doğrulanmadı.

## Kullanıma alma

Web sunucusuna güncel backend'i yayımlayın ve mobil uygulamanın güncel sürümünü dağıtın. Eski mobil sürüm yeni anlık olayı dinlemez. EAS projectId mevcut yapılandırmada tanımlı; kapalı uygulama teslimatı için o projede ilgili Android FCM/iOS APNs kimlik bilgilerinin çalışır olması gerekir.

Mevcut cihazlarda Bildirimleri Aç, Hatırlatmalar, Tahsilatlar ve Borçlar açık olmalıdır; önceden kaydedilmiş kapalı tercihler korunur. Telefonun uygulama bildirim izni de açık olmalıdır.

Canlı kontrol: masaüstünde borç ve tahsilat oluşturun; mobil açıkken uygulama kutusunu ve telefon bildirimini kontrol edin. Ardından ekran kilitliyken ve uygulama kapalıyken aynı işlemleri tekrarlayın. Vadesi gelen hatırlatmada aynı kontrolleri yapın. Aynı kaydın yeniden senkronizasyonu ikinci işlem bildirimi üretmemelidir.

Expo kaynakları: [Bildirim API'si ve Android kanalları](https://docs.expo.dev/versions/v54.0.0/sdk/notifications/), [push yapılandırması](https://docs.expo.dev/push-notifications/push-notifications-setup/).

## Telefon bildiriminden detay açma

Tüm kategorilerde telefon bildirimine dokunma, uygulamayı ilgili bildirim detayına yönlendirir. Açık/arka plandaki uygulama ve kapalı uygulamayı başlatan son bildirim yanıtı aynı hook ile işlenir. Oturum ve navigator hazır olmadan yönlendirme yapılmaz; giriş gerekiyorsa hedef girişten sonra açılır. Bildirim metni yönlendirmeden önce yerelde saklanır, böylece ağ olmadan da görüntülenir. İşlenen OS yanıtı temizlenir ve tekrarlı event ikinci yönlendirme yapmaz.

`node scripts/test-notifications.cjs` ek yönlendirme senaryoları geçti: tüm kategoriler, kapalı başlangıç, giriş bekleme, yönlendirmeden önce kayıt, tekrarlı yanıt, peş peşe tıklama ve messageId içermeyen yerel bildirim. İlgili dosyaların ESLint kontrolü temiz. Gerçek cihazda açık, kilitli ve kapalı uygulamadan bildirim tıklaması ayrıca doğrulanmalıdır. Bu özellik için güncel mobil sürüm dağıtılmalıdır; sunucu payload'ında yeni değişiklik gerekmez.

Kaynak: [Expo başlangıç ve bildirim tıklaması API'si](https://docs.expo.dev/versions/v56.0.0/sdk/notifications/).

## Hatırlatma saatinden önce bildirim gelmesi

Hatırlatma senkronizasyonundaki iki dakikalık erken gönderim yolu kaldırıldı. Zamanlayıcının bir sonraki tarama aralığını önceden bildirmesi de kaldırıldı; veri katmanı artık yalnız zamanı gelmiş kayıtları döndürür. Kayıt oluşturma/güncelleme yalnız veriyi senkronize eder; hatırlatma bildiriminin tek kaynağı zamanlayıcıdır.

`dueAtUtc`/`dueAt`, genel kayıt tarihi ve saatine göre önceliklidir. Oluşturulma/güncellenme alanlarından hatırlatma zamanı tahmin edilmez. Türkiye saati ve UTC dönüşümü korunur.

Varsayılan tarama aralığı 5 saniyedir. Açık eski dakika/saat ayarları korunur; canlı sunucuda böyle bir ayar varsa `Caritakip:ReminderIntervalSeconds=5` eklenerek önceliklendirilebilir. Bildirim saatinden önce gönderilmez; saatinden sonraki teslim süresi tarama ve ağ/push gecikmesine bağlıdır. Sunucu kesintisinde geçmiş hatırlatmalar mevcut 72 saatlik geri kazanım penceresine göre işlenir.

`dotnet run --project tests/ReminderTiming/ReminderTiming.csproj`: başarılı. İki dakika/bir dakika/bir tick erken gönderimin engellenmesi, tam saat sınırı, geri kazanım aralığı, tarih alanı önceliği, UTC/offset dönüşümü, ayrı gün+saat, denetim tarihi reddi, tarama ayarları ve hatırlatma kaydı girildiğinde iletim yapılmaması test edildi. Test projesinde mevcut EF paketlerinin sürüm farklarından assembly uyarıları çıktı; kontroller geçti. Web derlemesi başarılı.

Bu zamanlama düzeltmesi için backend'i yayımlayıp yeniden başlatmak gerekir. Daha önce erken gönderilmiş telefon bildirimleri ve eski tekilleştirme kayıtları bu kod değişikliğiyle geriye dönük temizlenmez. Canlı denemede yeni bir hatırlatma oluşturun; kayıt anında ve saatten önce bildirim olmamalı, saatinden sonraki taramada gelmeli. Gerçek telefon ve canlı teslimat bu ortamda doğrulanmadı.

## iPhone / Expo Go: hatırlatma detayına geçerken kapanma

Kullanıcı sorunu hatırlatma OS bildirimine dokunurken bildirdi; borç ve tahsilat tıklamalarında hata olmadığını belirtti. Silme işlemi ilk şüpheydi, son netleştirmeye göre silme davranışına yeni değişiklik uygulanmadı.

Bildirim açılışı `AppState=active` olana ve `InteractionManager` mevcut etkileşimleri tamamlayana kadar bekletilir. Arka plana geçişte bekleyen yönlendirme iptal edilir; öne dönüşte tekrar hazırlanır. Yalnız varsayılan bildirim açma eylemi yönlendirir; dismiss eylemi detay açmaz. Native teslim tarihi geçersizse güvenli güncel tarih kullanılır; `toISOString()` hatasının açılış effect'ini çökertmesi önlenir. İçeriği saklama ve yönlendirmedeki JavaScript hataları artık kontrollü biçimde `[NSX Notification]` ile loglanır.

`node scripts/test-notifications.cjs` geçti: mevcut kategori ve tıklama kontrollerine ek olarak iOS inactive→active, geçiş bitmeden yönlendirmeme, bozuk teslim tarihi, dismiss eylemi ve arka plan iptali/öne dönüş test edildi. Adaptörler taklit edildi; iPhone'daki gerçek native kapanma tekrar üretilemedi. İlgili ESLint kontrolü temiz; genel TypeScript'te önceki 7 ekran hatası devam ediyor. Kapanmanın kesin kök nedeni için Expo terminali veya iPhone crash kaydı gerekli.

Mobil güncellemeden sonra yeni bir zamanlanmış hatırlatmayı iPhone kilitliyken bekleyip bildirimden açın. Detay açılmalı, uygulama arka plana düşmemeli ve oturum korunmalı. Ayrıca borç/tahsilat tıklamasını ve uygulama içinden detay açmayı kontrol edin. Android cihaz testi henüz yapılmadı.

## Expo Go kapanmasının sürüm incelemesi

Paylaşılan Metro logunda native kapanma nedeni yok; Expo Go bildirim desteği uyarısı var. Projede Expo SDK 57 kullanılırken `expo-notifications=0.32.17` bulunuyordu. `expo install --check` SDK 57 için `~57.0.21` beklediğini doğruladı. `expo install --fix --npm` ile altı paket hizalandı: Expo `~57.0.26`, camera `~57.0.6`, constants `~57.0.20`, linking `~57.0.11`, notifications `~57.0.21`, router `~57.0.24`. Windows kurulumunda aynı guard'ı çalıştırmak için preinstall komutu `node ./scripts/cmd-guard.js --preinstall` olarak taşındı. `package-lock.json` güncellendi.

Expo uyum kontrolü artık başarılı. Bildirim testleri ve bildirim/realtime lint kontrolü başarılı; genel TypeScript kontrolünde önceki yedi ekran hatası sürüyor. SignalR `connected` olayı için listener eklendi; logdaki “No client method” uyarısı giderildi.

Geliştirme modunda kişisel metin veya token içermeyen teşhis aşamaları loglanır: `tap_received`, `tap_prepared`, `waiting_for_foreground`, `navigation_started`, `navigation_requested`, `detail_loading`, `detail_loaded`. Metro bağlantısı `tap_received` öncesinde düşüyorsa JS detay yönlendirmesine ulaşılmamıştır; yine de kesin native neden için iPhone crash kaydı gerekir.

Yeniden test: güncel frontend'de paketleri kurun, eski Metro'yu Ctrl+C ile kapatın, `npx expo start --go -c` ile başlatın ve QR'ı yeniden açın. Yeni bir hatırlatma üretip test edin. Eski süreçteki eski bundle ile test etmeyin. Cihazdaki Expo Go da proje SDK'sıyla uyumlu olmalıdır. Sunucu tarafında bu sürüm hizalaması için değişiklik yoktur.

Paket uyumsuzluğu giderilmiştir; kapanmanın bu uyumsuzluktan kaynaklandığı cihazda henüz doğrulanmamıştır. Gerçek push testinin uygulamaya ait bir development build'de tekrarlanması gerekir; Expo Go'nun native bildirim desteği sınırlıdır. Kaynaklar: [Expo sürüm uyumu](https://docs.expo.dev/troubleshooting/expo-go-version-mismatch/), [Expo Go ve development build farkları](https://docs.expo.dev/develop/development-builds/faq/).
