// NSX API domain types. Mirror the .NET 8 CaritakipCloud models so that
// swapping from mock to real API needs no component changes.
// Reference: 02_Server_API_Reference/CaritakipCloud/Models/CaritakipCloudModels.cs

export type CustomerId = string;
export type CompanyId = string;
export type TransactionId = string;

export type TransactionKind = "debt" | "collection";

export interface Company {
  id: CompanyId;
  name: string;
  taxNumber?: string;
  isActive: boolean;
}

export interface Customer {
  id: CustomerId;
  companyId: CompanyId;
  name: string;
  phone?: string;
  email?: string;
  note?: string;
  /** Net = Alacak − Tahsilat. Positive = firmanın kalan alacağı, negative = fazla tahsilat. */
  netBalance: number;
  updatedAt: string;
  version: number;
}

export interface Transaction {
  id: TransactionId;
  companyId: CompanyId;
  customerId: CustomerId;
  customerName: string;
  kind: TransactionKind;
  amount: number;
  description?: string;
  date: string; // ISO
  createdAt: string;
  version: number;
}

export interface DashboardStatus {
  companyId: CompanyId;
  companyName: string;
  /** Brüt alacak (tüm alacak/borç kayıtları toplamı) — PC: ALACAK */
  totalReceivable: number;
  /** Yapılan tahsilat toplamı — PC: TAHSİLAT */
  totalCollected: number;
  /** @deprecated totalReceivable ile aynı anlamda kullanılıyordu; UI artık totalReceivable okur */
  totalDebt?: number;
  /** Kalan alacak = Alacak − Tahsilat — PC: NET BAKİYE */
  netBalance: number;
  customerCount: number;
  updatedAt: string;
}

export interface PairingResult {
  /** Short-lived Bearer access token. */
  accessToken: string;
  /** Long-lived rotating refresh token; only used against /auth/refresh. */
  refreshToken: string;
  /** ISO expiry of the access token. Client refreshes before this. */
  accessTokenExpiresAt: string;
  /** ISO expiry of the refresh token. When passed, user must re-scan QR. */
  refreshTokenExpiresAt: string;
  tenantId: string;
  /** Server-side native device id. Present on current backend builds. */
  mobileDeviceId?: string;
  companyName: string;
  /** Who issued the QR on the desktop side; shown as "user" in the app. */
  pairedBy?: { deviceId?: string; displayName?: string };
  companies: Company[];
  defaultCompanyId: CompanyId;
}

export interface RefreshResult {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  refreshTokenExpiresAt: string;
}

export interface DeviceInfo {
  platform: "ios" | "android" | "web";
  appVersion: string;
  deviceName?: string;
  /** Stable per-install id; backend reuses this instead of creating duplicates. */
  deviceId?: string;
}

/**
 * Reason codes the client uses to route auth failures. Prefix with 401
 * body { error: "..." } from the .NET backend for exact matching.
 */
export type AuthErrorCode =
  | "TOKEN_EXPIRED" // access token expired → refresh
  | "TOKEN_INVALID" // access token unusable → refresh
  | "REFRESH_EXPIRED" // refresh gone → force re-scan
  | "REFRESH_REVOKED" // server revoked (logout elsewhere, security change) → force re-scan
  | "DEVICE_UNAUTHORIZED"; // device removed from tenant → force re-scan

export class NsxAuthError extends Error {
  constructor(public code: AuthErrorCode, message?: string) {
    super(message ?? code);
    this.name = "NsxAuthError";
  }
}

/**
 * Genel HTTP hatası — auth dışı 4xx/5xx durumlar. Ham status ve gövde
 * korunur ki çağıran katman (mutation onError) kullanıcıya uygun Türkçe
 * mesajı kendi üretsin. Log tarafında ham içerik `__DEV__` altında
 * gözlenir; production'da kullanıcıya asla ham body gösterilmez.
 */
export class NsxApiError extends Error {
  constructor(public status: number, public body: string) {
    super(body || `HTTP ${status}`);
    this.name = "NsxApiError";
  }
}

/** İstenen işlem için status → Türkçe kullanıcı mesajı çevirici. */
export function nsxApiErrorMessage(err: unknown, action: "update" | "delete" | "create" | "read"): string {
  if (err instanceof NsxApiError) {
    switch (err.status) {
      case 405:
        return "Bu işlem sunucu tarafından henüz desteklenmiyor.";
      case 406:
        return "Bu işlem şu anda kullanılamıyor.";
      case 409:
        return "Kayıt başka bir cihazda değişmiş. Sayfayı yenileyip tekrar deneyin.";
      case 422:
        return "Girilen bilgilerde bir hata var. Lütfen kontrol edin.";
      case 429:
        return "Çok fazla istek gönderildi. Kısa bir süre sonra tekrar deneyin.";
      case 500:
      case 502:
      case 503:
      case 504:
        return "Sunucuya şu anda ulaşılamıyor. Tekrar deneyin.";
    }
    // 4xx diğer durumlar için gövdedeki `message` alanına düş.
    try {
      const parsed = JSON.parse(err.body) as { message?: string };
      if (parsed?.message) return parsed.message;
    } catch {}
    // Fallback:
    switch (action) {
      case "update":
        return "Kayıt güncellenemedi. Tekrar deneyin.";
      case "delete":
        return "Kayıt silinemedi. Tekrar deneyin.";
      case "create":
        return "Kayıt oluşturulamadı. Tekrar deneyin.";
      default:
        return "Sunucudan yanıt alınamadı.";
    }
  }
  if (err instanceof Error) return err.message;
  return "Bilinmeyen bir hata oluştu.";
}

export interface TransactionInput {
  customerId: CustomerId;
  kind: TransactionKind;
  amount: number;
  description?: string;
  date?: string; // ISO; defaults to now
  clientMutationId?: string; // idempotency key
}

export interface CustomerInput {
  name: string;
  phone?: string;
  email?: string;
  note?: string;
}

/** Cihaz davet üretimi (POST /api/mobile/pair/invite). */
export interface InviteResult {
  /** İnsan-okur kısa kod (örn "NSX-A3B7-K9RP"). */
  code: string;
  /** QR'a gömülecek ham veri (URL veya raw token). */
  qrPayload: string;
  /** ISO tarih; kalan süre = expiresAt - now(). */
  expiresAt: string;
}

/** Bağlı mobil cihazlar (GET /api/mobile/devices). */
export interface ConnectedDevice {
  id: string;
  name: string;
  platform: "ios" | "android" | "web";
  appVersion?: string;
  lastSeenAt: string;
  issuedAt: string;
  status: "active" | "inactive" | "revoked";
  /** Bu cihaz mı? */
  self?: boolean;
}

/** POST /api/mobile/devices/push-token */
export interface PushTokenInput {
  token: string;
  provider?: "expo" | "fcm" | "apns";
  platform?: "ios" | "android" | "unknown";
}

export interface PushTokenResult {
  success: boolean;
  registeredAt?: string;
}

/** GET /api/mobile/inbox */
export interface InboxMessage {
  id: string;
  category: string;
  title: string;
  body: string;
  createdAtUtc: string;
  read: boolean;
}

export interface InboxMarkReadInput {
  ids?: string[];
  all?: boolean;
}

export interface InboxDeleteInput {
  ids?: string[];
  all?: boolean;
  createdBeforeUtc?: string;
}

/** GET/PUT /api/mobile/devices/notification-prefs */
export interface NotificationPrefs {
  master: boolean;
  reminders: boolean;
  collections: boolean;
  debts: boolean;
  system: boolean;
}
