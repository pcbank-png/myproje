# NSX Localization Engine Pro V3

Bu proje tek site / tek veritabani uzerinde calisan, yeni dil eklendiginde ana yapinin degismedigi merkezi coklu dil altyapisina gecirilmistir.

## Temel calisma mantigi
- Varsayilan dil Turkce (`tr-TR`).
- Public header ust servis cubugunda, `Lisanslarim` baglantisinin hemen saginda bayrakli dil secici bulunur.
- Secim `NSX.Language` cookie'sinde 365 gun saklanir.
- Giris yapmis kullanicida tercih ayrica `UserLanguagePreferences` tablosuna kaydedilir.
- Giris sonrasi kullanicinin DB tercihi cihaza geri uygulanir.
- Arapca ve diger RTL diller `dir="rtl"` ile otomatik calisir.
- Yeni diller `Admin > Dil Motoru` ekranindan kod degisikligi olmadan eklenebilir.

## V3 - Tam site kapsami
V3'te yalniz ana sayfa ve urun kartlari degil, public ve kullaniciya acik sabit arayuz metinleri de merkezi dil motoruna alinmistir:
- Ana Sayfa
- Tum Yazilimlar / urun listesi
- Urun detaylari
- Hakkimizda
- Iletisim
- SSS
- Gizlilik Politikasi
- KVKK
- Iade Politikasi
- Videolar
- Rehberler / Blog arayuzu
- Giris / Kayit / Sifre islemleri
- Hesabim
- Siparisler / Siparis detayi
- Lisanslarim
- Indirmelerim
- Havale bildirim ekranlari
- Bayi panelinin sabit arayuz metinleri
- Ortak header, footer, kampanya, kurulum rehberi ve paylasilan public bilesenler
- Admin panelinin tum liste, form, rapor, dil motoru ve yonetim ekranlari

Admin paneli de kendi bayrakli dil secicisine sahiptir. Secilen dil public site ile
ayni guvenli cookie/hesap tercihini kullanir; Arapcada panel otomatik RTL duzenine
gecer. Admin HTML'i merkezi katalogdan cevrilir ancak musteri, siparis, lisans ve
odeme verileri makine ceviri servisine hicbir zaman gonderilmez. Admin icin dagitimla
birlikte gelen sabit, dort dilli UI paketi kullanilir.

English icin bu sabit arayuz alanlarinda reviewed seed paketleri bulunur; harici ceviri servisi olmasa da UI metinleri English calisir.

## Urun aciklamalari - kisa metne dusmez
V2'de yabanci dilde ceviri bulunamadiginda uzun `Product.Description` yerine kisa SEO/ozet metnine dusme ihtimali vardi. V3'te bu davranis kaldirildi.

- Urun detay govdesi her dilde TAM `Model.Description` ile render edilir.
- Urun karti ozeti gercek `MetaDescription`, SEO aciklamasi veya urunun kendi aciklamasindan uretilir; genel/kisa bir sabit cumleye dusmez.
- Uzun urun paragraflari dil middleware'i tarafindan 9000 karaktere kadar tek parca olarak ele alinabilir.
- Dinamik DB urun metni icin native ceviri veya otomatik ceviri varsa tam metin hedef dilde gorunur.
- Otomatik ceviri servisi yapilandirilmamissa metin KISALTILMAZ; kaynak metnin tamami korunur.

## Otonom kaynak toplama
Genel HTML sayfalari render edilirken cevrilebilir gorunen metinler otomatik olarak kaynak kataloga alinir. Yeni sayfa veya yeni metin eklendiginde dil motoruna manuel anahtar eklemek zorunlu degildir.

## Otonom dinamik ceviri
V3, DB'den veya editor tarafindan sonradan gelen metinler icin istege bagli makine ceviri katmani icerir. Desteklenen saglayicilar:
- Google Cloud Translation API
- DeepL API

Calisma sirasi:
1. Hedef dilde reviewed/native ceviri varsa onu kullanir.
2. Hedef dilde ceviri yoksa ve makine ceviri saglayicisi aktifse eksik public metni otomatik cevirir.
3. Sonucu `LocalizationTranslations` tablosuna `IsMachineTranslated=true` olarak cache'ler.
4. Sonraki istekte tekrar API harcamasi yapmaz.
5. Admin `Dil Motoru` ekraninda ceviri manuel duzeltilip reviewed/locked yapilabilir; makine ceviri bu metnin uzerine yazmaz.
6. Saglayici kapali veya erisilemezse sayfa kirilmaz ve kaynak metnin tamami korunur.

## Gizlilik korumasi
Makine ceviri servisine kullanici/siparis/odeme verisi gonderilmez. Asagidaki alanlarda harici saglayici otomatik olarak devre disidir:
- `/Account`
- `/Dealer`
- `/BankTransfer`

`Login`, `Register`, `ForgotPassword`, `ForgotPasswordConfirmation` ve `ResetPassword`
ekranlarinin yalnizca derleme ile gelen statik arayuz anahtarlari `/Public/Auth`
allowlist'i uzerinden tamamlanabilir. Form degerleri, e-posta, parola, token ve diger
kullanici verileri bu kapsama girmez. Diger Account ekranlari reviewed sabit arayuz
cevirileriyle calisir. Admin/API/Cloud/Hub endpointleri de HTML ceviri middleware'i disindadir.

## Makine ceviri ayari
`appsettings.json` veya environment variable ile acilir. API anahtarinin dosyaya yazilmasi zorunlu degildir; production'da environment variable tercih edilir.

Google Cloud ornegi:
```text
Localization__MachineTranslation__Enabled=true
Localization__MachineTranslation__Provider=GoogleCloud
Localization__MachineTranslation__ApiKey=YOUR_KEY
```

DeepL ornegi:
```text
Localization__MachineTranslation__Enabled=true
Localization__MachineTranslation__Provider=DeepL
Localization__MachineTranslation__ApiKey=YOUR_KEY
Localization__MachineTranslation__Endpoint=https://api-free.deepl.com/v2/translate
```

## Veritabani
Baslangicta veri kaybi olmadan su yapilar kontrol edilip gerekirse olusturulur:
- `SiteLanguages`
- `LocalizationResources`
- `LocalizationTranslations`
- `UserLanguagePreferences`

## Ilk diller
- Turkce / tr-TR / LTR
- English / en-US / LTR
- Deutsch / de-DE / LTR
- العربية / ar-SA / RTL

Yeni diller kod degisikligi gerektirmeden eklenebilir. Dil kodu sistemin esas kimligidir; bayrak yalnizca gorsel temsilidir.

## Yonetim
- `/Admin/Languages` : Diller, aktif/pasif, varsayilan dil, kapsama orani
- `/Admin/Languages/{code}/Translations` : Yakalanan metinleri ara, cevir, kaydet, incele
- Admin ust cubugundaki dil secici: panel dilini sayfadan ayrilmadan degistirir

## Fallback
Ceviri eksikligi sayfayi bos veya kirik birakmaz. Reviewed ceviri > native ceviri > otomatik public ceviri > guvenli kaynak metin zinciri uygulanir. Uzun urun aciklamasi hicbir asamada kisa bir genel cumleyle degistirilmez.

## Autonomous Translation Queue (Forward Fix)

- New admin content (products, rich product descriptions, categories, videos, advertisements and campaign texts) is indexed immediately and queued for every active target language.
- Adding a new language first indexes known dynamic database content, then queues every missing localization resource for that language with no 250-item limit.
- `LocalizationTranslationJobs` persists Pending / Processing / Completed / Failed state across app restarts.
- `LocalizationTranslationQueueWorker` focuses on one target language at a time. It keeps draining that language in safe batches; other languages remain Pending until the active language finishes.
- If the active language produces no result or all remaining work is waiting for a retry time, the worker moves to the next language. The stalled language remains durable and is retried later; it cannot block the complete queue.
- A successful batch is followed by the next batch immediately instead of the old fixed 20-second sleep. Free-provider requests use bounded parallelism inside the active language only, so language results are not mixed.
- `FreeCascade` first uses the independent Google Clients5 translation endpoint, then retains the previous Google Free and MyMemory endpoints as fallbacks. This prevents one shared-hosting-IP HTTP 429 quota from stopping the complete engine.
- Every provider batch has a hard timeout. A graceful IIS/Plesk recycle puts claimed Processing jobs back into Pending, and abandoned jobs are recovered on the next worker cycle.
- The Admin > Dil Motoru page sends an authenticated lightweight heartbeat every 30 seconds while visible. This keeps the application warm while the queue is being monitored and wakes the worker without translating inside the HTTP request.
- Throughput and provider pressure can be tuned with `QueueBatchSize`, `MaxTextsPerRequest`, `MaxCharactersPerRequest`, `MaxParallelRequests`, `WorkerStartupDelaySeconds`, `WorkerIdleDelaySeconds`, `WorkerContinuousDelayMilliseconds` and `WorkerBatchTimeoutSeconds` under `Localization:MachineTranslation`.
- Public text that was not known at deploy time is still captured by `LocalizationCaptureWorker`; captured source keys are automatically connected to the same queue.
- Translation providers use source-language auto detection so Turkish source content and English technical/product names can both receive a real target-language translation.
- Clearly mixed-language legacy EN/DE/AR seed rows are released from the old locked state and re-queued, while valid reviewed rows remain protected.
- Arabic presentation normalization continues to render NSX / Pro / Live / Free / Turbo with Arabic-script equivalents.
