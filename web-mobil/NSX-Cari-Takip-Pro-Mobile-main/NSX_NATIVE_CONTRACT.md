# NSX Native Mobile — Final Auth & API Contract (v1.0)

**Durum:** Mobil client bu sözleşmeye göre **tamamen hazır**. Backend (`.NET 8 / CaritakipCloud`) tarafına hiçbir değişiklik yapılmadı. Mevcut QR web login akışı (`GET /CaritakipCloud/login/{token}` → cookie + CSRF) **aynen korunur**; bu sözleşme yalnızca native client için **yeni, ek** uç noktalar tanımlar.

**Prensipler (native):** Bearer auth · short-lived access token · rotating refresh token · SecureStore persistence · silent refresh · cookie YOK · CSRF YOK · User-Agent pinning YOK · mock↔live strict ayrım · live hatasında mock fallback YOK.

---

## 1. Eklenecek Minimum .NET Endpoint Listesi

| # | Endpoint | Auth | Amaç |
|---|---|---|---|
| 1 | `POST /api/mobile/pair/consume` | yok | QR token → native session (access+refresh) |
| 2 | `POST /api/nsx-native-token/rotate` | yok (refreshToken ile) | Rotating refresh → yeni token çifti |
| 3 | `POST /api/mobile/auth/logout` | Bearer | Refresh token server-side revoke |
| 4 | Mevcut `/api/mobile/*` read/write uçları | **Bearer** (cookie yerine/yanına) | Aşağıdaki eşleme tablosu |

> Mevcut cookie tabanlı `/api/mobile/*` uçları web panel için kalıyorsa server'ın isteğe göre kimlik şeması seçmesi gerekir: `Authorization: Bearer` header'ı varsa Bearer doğrulaması, yoksa cookie doğrulaması. Native client asla cookie göndermez.

---

## 2. Endpoint Sözleşmeleri

### 2.1 `POST /api/mobile/pair/consume`
QR payload'ından çıkarılan tek kullanımlık tokenı native oturuma çevirir. Server, mevcut `ConsumeQrAsync` doğrulamasını (token hash eşleşmesi, `consumed_at_utc IS NULL`, `expires_at_utc >= now`, entitlement kontrolü, tek kullanımlık işaretleme) aynen yeniden kullanabilir — fark: cookie set etmek yerine JSON ile token çifti döner.

**Request:**
```json
{
  "token": "4a0zHXT-ziKgP-ZuNeA_...",
  "deviceInfo": {
    "platform": "ios",
    "appVersion": "1.0.0",
    "deviceName": "opsiyonel"
  }
}
```

**Response 200:**
```json
{
  "accessToken": "<JWT veya opaque, 5-15 dk ömürlü>",
  "refreshToken": "<opaque, rotating, 30-90 gün ömürlü, device-bound>",
  "accessTokenExpiresAt": "2026-09-28T12:15:00Z",
  "refreshTokenExpiresAt": "2026-10-28T12:00:00Z",
  "tenantId": "tnt_...",
  "companyName": "Akyıldız Ticaret Ltd. Şti.",
  "pairedBy": { "deviceId": "desktop-01", "displayName": "Muhasebe - Ayşe" },
  "companies": [{ "id": "c-1", "name": "...", "taxNumber": "...", "isActive": true }],
  "defaultCompanyId": "c-1"
}
```

**Response 401** — QR token geçersiz / süresi dolmuş (TTL 60-300 sn) / zaten tüketilmiş:
```json
{ "error": "QR_INVALID", "message": "QR bağlantısı geçersiz veya süresi dolmuş." }
```

**Kurallar:**
- QR tokenı **tek kullanımlık** kalır; consume başarılıysa işaretlenir, asla tekrar kullanılamaz.
- Refresh token server'da **hash'iyle** saklanır (`ct_mobile_refresh_tokens`: tokenHash, tenantId, deviceId, issuedAtUtc, expiresAtUtc, revokedAtUtc, replacedByHash).
- Refresh token opaktır (JWT değil), cihaz bağlamı (deviceId) ile saklanır → device authorization removal = satırın revoke edilmesi.
- Audit: `QrConsumedNative` olayı (mevcut `QrConsumed`'dan ayırt edilebilir).

### 2.2 `POST /api/nsx-native-token/rotate` ✅ (production'da canlı)
Not: `/api/mobile/auth/refresh` artık kullanılmıyor (405 route çakışması nedeniyle backend bu yeni path'e taşıdı — build `nsx-20260928-222248`).
**Request:**
```json
{ "refreshToken": "<mevcut refresh token>" }
```
**Response 200** (rotation — eski refresh token anında geçersizleşir):
```json
{
  "accessToken": "<yeni>",
  "refreshToken": "<yeni>",
  "accessTokenExpiresAt": "…",
  "refreshTokenExpiresAt": "…"
}
```
**Response 401 hata kodları** (client bu kodlara göre davranır):
| error | Anlam | Client davranışı |
|---|---|---|
| `REFRESH_EXPIRED` | Refresh token süresi doldu | SecureStore temizle → login/QR |
| `REFRESH_REVOKED` | Logout / başka cihazdan çıkış / **rotation replay** (eski token ikinci kez kullanıldı) | SecureStore temizle → login/QR |
| `DEVICE_UNAUTHORIZED` | Cihaz yetkisi tenant tarafından kaldırıldı | SecureStore temizle → login/QR + bilgi mesajı |

> Rotation replay koruması zorunlu: eski bir refresh token'ın tekrar gelmesi = token sızıntısı şüphesi → o cihazın tüm token ailesi revoke edilir.

### 2.3 `POST /api/mobile/auth/logout`
**Headers:** `Authorization: Bearer <accessToken>`
**Request:** `{ "refreshToken": "<mevcut>" }`
**Response 200:** `{ "success": true }`
- Refresh token `revokedAtUtc` işaretlenir. Token zaten geçersizse yine 200 (idempotent).
- Client bunu best-effort çağırır; network yoksa bile SecureStore'u temizleyip login'e döner.

### 2.4 Diğer `/api/mobile/*` uçlarında auth hata sözleşmesi
Bearer geçersiz/süresi dolmuşsa **401** + body:
```json
{ "error": "TOKEN_EXPIRED" }
```
Client bu kodda **tek seferlik silent refresh + retry** yapar. Refresh de başarısızsa (yukarıdaki REFRESH_* kodları) kullanıcı login'e döner. Diğer hatalarda gövdede `message` alanı kullanıcıya gösterilebilir Türkçe metin taşır.

---

## 3. Token Davranış Özeti

| Token | Ömür | Saklama | Kullanım |
|---|---|---|---|
| Access | 5-15 dk | memory + SecureStore (session JSON) | Her API isteğinde `Authorization: Bearer` |
| Refresh | 30-90 gün, rotating | **sadece SecureStore** (Keychain/Keystore) | Yalnızca `/auth/refresh` ve `/auth/logout` |

- Client, access token'ın süresine **30 sn kala proaktif** yeniler; 401 `TOKEN_EXPIRED`'da reaktif yeniler (single-flight).
- App/phone restart, normal token expiry → **oturum kapanmaz**.
- Login yalnızca: logout, server-side revoke, device removal, security change veya app reinstall'de istenir.

---

## 4. Environment Değişkenleri (`/app/frontend/.env`)

| Değişken | Değer | Açıklama |
|---|---|---|
| `EXPO_PUBLIC_NSX_MODE` | `mock` *(default)* / `live` | `live` yazılınca tüm istekler gerçek API'ye gider. Tanımsız/başka değer = mock. |
| `EXPO_PUBLIC_NSX_BASE_URL` | örn. `https://staging.nsx-cloud.com` | Yalnızca live modda zorunlu. Yoksa live istekleri fail-fast hata verir. |
| `EXPO_PUBLIC_BACKEND_URL` | (mevcut, korunuyor) | Preview infra için; NSX API ile ilgisi yok. |

Mock modda **hiçbir network isteği atılmaz** (tüm veri in-memory). Live modda **asla mock'a fallback yapılmaz**.

---

## 5. Ekran → Endpoint Eşleme (Live Mod)

| Ekran / Aksiyon | Endpoint |
|---|---|
| Login — "QR Kodu Tara" + scanner | `POST /api/mobile/pair/consume` (QR'dan çıkan token ile) |
| Login — manuel kod girişi | `POST /api/mobile/pair/consume` (aynı endpoint, kod = token) |
| App açılışı (boot) | SecureStore'dan session; refresh süresi dolmuşsa login. Ardından `GET /api/mobile/companies` |
| Her API isteği öncesi | Gerekirse `POST /api/nsx-native-token/rotate` (silent) |
| Dashboard — firma/bakiye/müşteri sayısı | `GET /api/mobile/status?companyId=…` |
| Dashboard — son hareketler | `GET /api/mobile/transactions/recent?companyId=…&take=8` |
| Müşteriler — liste + arama | `GET /api/mobile/customers?companyId=…&search=…` |
| Cari Detay — müşteri bilgisi | `GET /api/mobile/customers/{id}` |
| Cari Detay — hareketler | `GET /api/mobile/customers/{id}/transactions` |
| Yeni Müşteri modal | `POST /api/mobile/customers` (body: companyId, name, phone, email, note) |
| Borç Ekle modal | `POST /api/mobile/debts` (body: companyId, customerId, amount, description, clientMutationId) |
| Tahsilat Al modal | `POST /api/mobile/collections` (aynı şema) |
| Ayarlar — Oturumu Kapat | `POST /api/mobile/auth/logout` → SecureStore temizle → `/login` |
| Hareketler tab (global liste) | `GET /api/mobile/transactions/recent?companyId=…&take=200` |

> Yazma uçlarında `clientMutationId` zorunlu (çift kayıt koruması, mevcut `ClientMutationId + ExpectedVersion` mantığıyla uyumlu).

---

## 6. Staging URL Geldiğinde Son Bağlantı Adımları

1. `.env`'e ekle: `EXPO_PUBLIC_NSX_MODE=live` ve `EXPO_PUBLIC_NSX_BASE_URL=<staging-url>` → servisleri yeniden başlat.
2. Backend'in §1'deki 3 endpoint'i + Bearer doğrulamayı staging'e deploy ettiğini doğrula.
3. Desktop test ortamından QR üret → telefonda native scanner ile okut → pair/consume uçtan uca doğrula.
4. Smoke: dashboard bakiyeleri, müşteri listesi, cari detay, borç/tahsilat ekleme, pull-to-refresh, logout → login.
5. Yanıt şekilleri farklıysa **yalnızca** `src/api/http-client.ts` içindeki mapping düzeltilir (tek dosya); ekranlar değişmez.

---

## 7. Açıkça Dışarıda Bırakılanlar (Bu Aşamada Yok)
Swipe ile hareket silme/düzenleme, PDF ekstre, WhatsApp/e-posta paylaşımı, SignalR realtime, çoklu cihaz yönetimi, push notification, yeni rapor ekranları, yeni backend (MongoDB/FastAPI/Firebase), UI redesign, publish/deploy. Finansal hareket silme/düzenleme ileride onay diyalogu + audit kurallarıyla ayrıca tasarlanacak.

---

## 8. Genişletilmiş Uç Noktalar — Backend Live

Aşağıdaki uç noktalar `.NET / CaritakipCloud` sunucusunda **yayında** ve mobil client tarafından **canlı** çağrılıyor.

### 8.1 PUT — Hareket / Müşteri Güncelleme
| Endpoint | Amaç | Durum |
|---|---|---|
| `PUT /api/mobile/customers/{id}` | Müşteri düzenleme | ✅ Canlı |
| `PUT /api/mobile/debts/{id}` | Borç kaydını düzenleme | ✅ Canlı |
| `PUT /api/mobile/collections/{id}` | Tahsilat kaydını düzenleme | ✅ Canlı |
| `DELETE /api/mobile/customers/{id}` | Müşteri silme | ✅ Canlı |
| `DELETE /api/mobile/debts/{id}` | Borç silme | ✅ Canlı |
| `DELETE /api/mobile/collections/{id}` | Tahsilat silme | ✅ Canlı |

**Auth:** Bearer access token. Client 401'de sessiz refresh + retry yapar.

### 8.2 Cihaz Davet Et — Mobil Erişim Paylaşımı
| Endpoint | Method | Auth | Amaç |
|---|---|---|---|
| `POST /api/mobile/pair/invite` | POST | Bearer | Aktif oturumdan ikinci cihaz için **yeni, tek kullanımlık, kısa süreli** pairing token üretimi |

**Request (canlı .NET sözleşmesi):**
```json
{ "expiresInSeconds": 180 }
```
**Response 200 (canlı wire format):**
```json
{
  "pairCode": "NSX-A3B7-K9RP",
  "qrValue": "NSX-A3B7-K9RP",
  "expiresAtUtc": "2026-06-15T14:33:00Z",
  "expiresInSeconds": 180,
  "oneTime": true
}
```
HTTP client bu wire formatı ekran katmanı için `code / qrPayload / expiresAt` alanlarına normalize eder. Böylece QR ve sayaç sunucu alan adlarından bağımsız, tek formatla çalışır.

### 8.3 Bağlı Cihazlar — Görüntüleme
| Endpoint | Method | Auth | Amaç |
|---|---|---|---|
| `GET /api/mobile/devices` | GET | Bearer | Bu tenant + kullanıcı için mobil oturumlar |

**Response (canlı .NET wire formatı)** — düz dizi:
```json
[
  {
    "deviceId": "3f1d...",
    "platform": "android",
    "appVersion": "1.0.0",
    "deviceName": "NSX Android · A1B2",
    "isActive": true,
    "isCurrent": true,
    "createdAtUtc": "2026-05-10T08:12:00Z",
    "updatedAtUtc": "2026-06-15T14:29:11Z",
    "lastSeenAtUtc": "2026-06-15T14:29:11Z"
  }
]
```
HTTP client bu alanları `id / name / status / self / issuedAt / lastSeenAt` ekran modeline normalize eder. Yeni mobil build ayrıca eşleştirmede kalıcı `deviceInfo.deviceId` gönderir; backend aynı kurulum için yeni satır açmak yerine mevcut cihaz kaydını yeniden etkinleştirip tokenları döndürür.

### 8.4 Push token (Expo)
| Endpoint | Method | Auth | Amaç |
|---|---|---|---|
| `POST /api/mobile/devices/push-token` | POST | Bearer | Expo push token kaydı |

**Request:** `{ "token": "ExponentPushToken[...]", "provider": "expo", "platform": "ios" }`  
**Response 200:** `{ "success": true, "registeredAtUtc": "..." }`

### 8.5 Push gönderimi (sunucu)
Borç/tahsilat mutasyonları uygulandığında (`debt` / `collection`, `applied`) tenant’taki diğer native cihazlara Expo Push API ile bildirim gider. İşlemi yapan cihaz (`native:{deviceId}`) hedef dışı bırakılır.

Yapılandırma (`appsettings`):
- `Caritakip:ExpoPushEnabled` — varsayılan `true`
- `Caritakip:ExpoAccessToken` — isteğe bağlı Expo erişim anahtarı

Payload `data`: `{ type, entityType, entityId, customerId?, messageId?, category? }`

### 8.6 Inbox (sunucu)
| Endpoint | Method | Auth | Amaç |
|---|---|---|---|
| `GET /api/mobile/inbox?take=50` | GET | Bearer | Cihaza özel bildirim listesi |
| `GET /api/mobile/inbox/{messageId}` | GET | Bearer | Tek bildirim detayı |
| `POST /api/mobile/inbox/mark-read` | POST | Bearer | `{ "all": true }` veya `{ "ids": ["..."] }` |
| `POST /api/mobile/inbox/delete` | POST | Bearer | `{ "all": true }` veya `{ "ids": ["..."] }` |

**Satır:** `{ id, category, title, body, createdAtUtc, read }`

### 8.7 Hatırlatma worker (saat gelince)
`CaritakipMobileReminderWorker` varsayılan 5 saniyede bir `reminder` entity tarar; masaüstü **`dueAt`** (ve benzeri) alanını okur. Yalnız **dueAt ≤ şimdi** ve geri kazanım penceresindeyse inbox + Expo push gönderir. Gelecek saate ön bildirim yoktur; hatırlatma oluşturma/güncelleme senkronizasyonu OS bildirimi üretmez. Dedupe iletim sonrası yazılır. `isCompleted` mobil push’u engellemez (yalnız iptal/cancelled).

Yapılandırma:
- `Caritakip:ReminderWorkerEnabled` — varsayılan `true`
- `Caritakip:ReminderIntervalSeconds` — varsayılan `5`, aralık `1–60`
- `Caritakip:ReminderIntervalMinutes` / `ReminderIntervalHours` — eski açık ayarlar korunur; saniye ayarı önceliklidir
- `Caritakip:ReminderLookbackHours` — varsayılan `72`

Tarih alanları: `dueAt`, `dueDateUtc` / `dueDate` / `remindAt` / `date`+`time` …

`dueAtUtc`/`dueAt` mevcutsa genel kayıt tarihi/saatinden önce kullanılır. `createdAt`/`updatedAt` gibi denetim tarihleri hatırlatma zamanı yerine kullanılmaz. Türkiye saati esas alınır; UTC ve saat dilimi içeren değerler dönüştürülür.

İşlem bildirimi: yalnız **yeni kayıt (version=1)**; aynı sync paketindeki her borç/tahsilat kendi kategorisi ve ayrıntısıyla ayrı bildirim üretir. Expo push + SignalR yerel banner aynı `messageId` ile tekilleştirilir.

Inbox kaydı yazıldıktan sonra yalnız yetkili native cihaz grubuna `notificationAvailable` olayı gönderilir: `{ id, category, title, body, createdAtUtc, read }`. Bu olay işlem ve zamanlanmış hatırlatma bildirimlerini doğrudan yerel inbox'a ekler ve OS bildirimi gösterir. `changesAvailable` yalnız veri ekranlarını yeniler. Aktif uygulamada 15 saniyelik inbox sorgusu bağlantı kesilmesine karşı yedektir; kapalı uygulamada Expo push kullanılır.

### 8.8 Bildirim kanal tercihleri (cihaz başına)
| Endpoint | Method | Auth | Amaç |
|---|---|---|---|
| `GET /api/mobile/devices/notification-prefs` | GET | Bearer | Bu cihazın kanal tercihleri |
| `PUT /api/mobile/devices/notification-prefs` | PUT | Bearer | Tercihleri kaydet |

**Gövde / yanıt:** `{ master, reminders, collections, debts, system }` (boolean)

Push ve inbox gönderimi sunucuda bu tercihlere göre filtrelenir (`master` kapalıysa hiçbir kanal gitmez).

Yeni kurulumlarda borç dahil tüm kategoriler varsayılan açıktır. Önceden kaydedilmiş cihaz tercihleri korunur.

### 8.9 Telefon bildirimi tıklaması

Push ve yerel OS bildirimlerine dokunulduğunda `/ayarlar/bildirim/[id]` açılır. `data.messageId` sunucu inbox kimliğidir; eski veya kimliksiz bildirimlerde entity/OS request kimliğiyle kararlı bir yerel kayıt oluşturulur. İçerik yönlendirmeden önce kaydedilir; detay çevrimdışıyken de açılır ve mevcut detay ekranı bildirimi okundu işaretler.

`useLastNotificationResponse` açık/arka plandaki tıklamanın yanında uygulamayı kapalı durumdan başlatan tıklamayı da işler. Oturum, root navigator ve ilk giriş yönlendirmesi hazır olana kadar hedef bekletilir. İşlenen son OS yanıtı temizlenir; normal yeniden açılış aynı detayı tekrar açmaz. Borç, tahsilat, hatırlatma ve sistem için aynı akış geçerlidir.

Detaya geçiş yalnız `AppState=active` durumunda ve mevcut UI etkileşimleri tamamlanınca yapılır. Arka plana düşerse bekleyen geçiş iptal edilip öne dönüşte sürdürülür. Varsayılan açma eylemi dışındaki yanıtlar yönlendirme üretmez. Geçersiz native teslim tarihleri güvenli bir tarihle normalize edilir.

---

**Uygulama Notu:** Silme/güncelleme sonrası mobil client `queryClient.invalidateQueries()` çağırır → ilgili müşteri, hareketler ve dashboard sessizce refetch olur; SignalR akışı dokunulmaz.
