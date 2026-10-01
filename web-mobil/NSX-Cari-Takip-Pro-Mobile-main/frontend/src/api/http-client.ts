// Live NSX HTTP client for the token-exchange auth model.
//
// Wire contract (see /app/memory/nsx-qr-session-report.md § 2 for the
// backend request/response spec the .NET server must add):
// - POST /api/nsx-native-token/rotate { refreshToken }   (token rotation)
// - POST /api/mobile/pair/consume   { token, deviceInfo? }
// - POST /api/mobile/auth/logout    { refreshToken? }  (Bearer required)
// - All other /api/mobile/*         Bearer access token
//
// No cookies. No CSRF token. No User-Agent pinning. The QR web login
// on the server continues to use cookies for browsers; this client is
// a pure Bearer consumer.

import { getAccessToken, forceRefresh } from "../auth/token-manager";
import { clearSession, getSessionSync } from "../auth/session";
import {
  NsxApiError,
  NsxAuthError,
  type AuthErrorCode,
  type Company,
  type ConnectedDevice,
  type Customer,
  type CustomerId,
  type CustomerInput,
  type DashboardStatus,
  type DeviceInfo,
  type InviteResult,
  type PairingResult,
  type PushTokenInput,
  type PushTokenResult,
  type InboxMessage,
  type InboxMarkReadInput,
  type InboxDeleteInput,
  type NotificationPrefs,
  type RefreshResult,
  type Transaction,
  type TransactionId,
  type TransactionInput,
  type TransactionKind,
} from "./types";
import type { NsxApi } from "./mock-client";

function baseUrl(): string {
  const url = process.env.EXPO_PUBLIC_NSX_BASE_URL;
  if (!url) throw new Error("EXPO_PUBLIC_NSX_BASE_URL tanımlı değil.");
  return url.replace(/\/$/, "");
}

interface FetchOpts {
  method: string;
  path: string;
  body?: unknown;
  auth?: boolean; // default true
  headers?: Record<string, string>; // ek header'lar (örn. expected-version)
  _retry?: boolean; // internal
}

async function rawFetch<T>({ method, path, body, auth = true, headers: extra, _retry }: FetchOpts): Promise<T> {
  const account = getSessionSync();
  const headers: Record<string, string> = {
    Accept: "application/json",
    ...(extra ?? {}),
  };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (auth) {
    const token = await getAccessToken();
    if (token) headers.Authorization = `Bearer ${token}`;
  }

  const identity = (session: typeof account) => session ? `${session.mode}:${session.tenantId}:${session.mobileDeviceId}` : null;
  if (auth && (!account || identity(account) !== identity(getSessionSync()))) throw new Error("Oturum değişti.");
  let res: Response;
  try {
    res = await fetch(`${baseUrl()}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (e) {
    // Live mode: propagate network error. NEVER fall back to mock.
    throw new Error(
      e instanceof Error ? `Ağ hatası: ${e.message}` : "Sunucuya erişilemedi.",
    );
  }

  if (auth && identity(account) !== identity(getSessionSync())) throw new Error("Oturum değişti.");

  if (res.status === 401 && auth && !_retry) {
    // Try one silent refresh + retry. If refresh throws an auth error,
    // token-manager already cleared the session and the caller will be
    // routed back to /login.
    const errCode = await readAuthError(res);
    if (errCode === "REFRESH_EXPIRED" || errCode === "REFRESH_REVOKED" || errCode === "DEVICE_UNAUTHORIZED") {
      await clearSession();
      throw new NsxAuthError(errCode);
    }
    try {
      await forceRefresh(headers.Authorization?.slice(7));
    } catch (e) {
      throw e;
    }
    return rawFetch<T>({ method, path, body, auth, headers: extra, _retry: true });
  }

  if (!res.ok) {
    const text = await res.text().catch(() => "");
    if (res.status === 401 || res.status === 403) {
      const code = (await parseErrorCode(text)) ?? "TOKEN_INVALID";
      throw new NsxAuthError(code);
    }
    // Debug/log tarafında ham status ve body kaybolmasın.
    if (__DEV__) {
      console.warn(`[NSX API] ${method} ${path} → HTTP ${res.status}`, text);
    }
    // Yapılandırılmış hata: çağıran katman (mutation onError) status'a
    // göre kullanıcıya gösterilecek Türkçe mesajı kendisi üretir.
    // Buradaki `message` yalnızca fallback amaçlı ham gövde/status'tur.
    throw new NsxApiError(res.status, text);
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

async function readAuthError(res: Response): Promise<AuthErrorCode | undefined> {
  const cloned = res.clone();
  const text = await cloned.text().catch(() => "");
  return parseErrorCode(text);
}

function parseErrorCode(text: string): AuthErrorCode | undefined {
  if (!text) return undefined;
  try {
    const parsed = JSON.parse(text) as { error?: string };
    const code = parsed.error as AuthErrorCode | undefined;
    if (
      code === "TOKEN_EXPIRED" ||
      code === "TOKEN_INVALID" ||
      code === "REFRESH_EXPIRED" ||
      code === "REFRESH_REVOKED" ||
      code === "DEVICE_UNAUTHORIZED"
    ) {
      return code;
    }
  } catch {}
  return undefined;
}


type InviteWireResult = {
  pairCode?: string;
  qrValue?: string;
  expiresAtUtc?: string;
  expiresInSeconds?: number;
  oneTime?: boolean;
  // Accept already-normalized shapes too (mock/proxy compatibility).
  code?: string;
  qrPayload?: string;
  expiresAt?: string;
};

type DeviceWireResult = {
  deviceId?: string;
  platform?: string;
  appVersion?: string;
  deviceName?: string;
  isActive?: boolean;
  isCurrent?: boolean;
  createdAtUtc?: string;
  updatedAtUtc?: string;
  lastSeenAtUtc?: string | null;
  // Accept normalized aliases too.
  id?: string;
  name?: string;
  status?: "active" | "inactive" | "revoked";
  self?: boolean;
  issuedAt?: string;
  lastSeenAt?: string;
};

function normalizeInvite(raw: InviteWireResult): InviteResult {
  const code = (raw.code ?? raw.pairCode ?? "").trim();
  const qrPayload = (raw.qrPayload ?? raw.qrValue ?? code).trim();
  const rawExpiry = raw.expiresAt ?? raw.expiresAtUtc ?? "";
  const parsed = Date.parse(rawExpiry);
  const seconds = Number(raw.expiresInSeconds);
  const expiresAt = Number.isFinite(parsed)
    ? new Date(parsed).toISOString()
    : Number.isFinite(seconds) && seconds > 0
      ? new Date(Date.now() + seconds * 1000).toISOString()
      : new Date(Date.now() + 180_000).toISOString();

  if (!code || !qrPayload) {
    throw new NsxApiError(502, "Cihaz davet yanıtı eksik: eşleştirme kodu/QR verisi bulunamadı.");
  }

  return { code, qrPayload, expiresAt };
}

function normalizePlatform(value?: string): ConnectedDevice["platform"] {
  const p = (value ?? "").toLowerCase();
  if (p === "ios" || p === "android") return p;
  return "web";
}

function deviceTimestamp(d: ConnectedDevice): number {
  const last = Date.parse(d.lastSeenAt);
  if (Number.isFinite(last)) return last;
  const issued = Date.parse(d.issuedAt);
  return Number.isFinite(issued) ? issued : 0;
}

function normalizeDevice(raw: DeviceWireResult): ConnectedDevice | null {
  const id = (raw.id ?? raw.deviceId ?? "").trim();
  if (!id) return null;
  const platform = normalizePlatform(raw.platform);
  const suffix = id.slice(-4).toUpperCase();
  const fallbackName = platform === "ios" ? "iPhone / iPad" : platform === "android" ? "Android cihaz" : "Web cihaz";
  const name = (raw.name ?? raw.deviceName ?? "").trim() || `${fallbackName} · ${suffix}`;
  const issuedAt = raw.issuedAt ?? raw.createdAtUtc ?? raw.updatedAtUtc ?? new Date(0).toISOString();
  const lastSeenAt = raw.lastSeenAt ?? raw.lastSeenAtUtc ?? raw.updatedAtUtc ?? issuedAt;
  const self = Boolean(raw.self ?? raw.isCurrent);
  const status = raw.status ?? (raw.isActive === false ? "inactive" : "active");

  return {
    id,
    name,
    platform,
    appVersion: raw.appVersion || undefined,
    lastSeenAt,
    issuedAt,
    status,
    self,
  };
}

/**
 * Old backend builds created a new row on every QR pairing. Until those
 * historical rows are cleaned server-side, collapse obvious same-device
 * registrations for display. Prefer the current registration, otherwise the
 * most recently seen one. New builds use a stable device id and do not create
 * these duplicates anymore.
 */
function dedupeDevices(devices: ConnectedDevice[]): ConnectedDevice[] {
  const byFingerprint = new Map<string, ConnectedDevice>();

  for (const device of devices) {
    const legacyUnnamed = /^(Android cihaz|iPhone \/ iPad|Web cihaz)\s*·\s*[0-9A-F]{4}$/i.test(device.name);
    const normalizedName = legacyUnnamed
      ? device.name.replace(/\s*·\s*[0-9A-F]{4}$/i, "").trim().toLowerCase()
      : device.name.trim().toLowerCase();
    const fingerprint = [device.platform, normalizedName, device.appVersion ?? ""].join("|");
    const current = byFingerprint.get(fingerprint);
    if (!current) {
      byFingerprint.set(fingerprint, device);
      continue;
    }

    if (device.self && !current.self) {
      byFingerprint.set(fingerprint, device);
      continue;
    }
    if (device.self === current.self && deviceTimestamp(device) > deviceTimestamp(current)) {
      byFingerprint.set(fingerprint, device);
    }
  }

  return [...byFingerprint.values()].sort((a, b) => {
    if (a.self !== b.self) return a.self ? -1 : 1;
    if (a.status !== b.status) return a.status === "active" ? -1 : 1;
    return deviceTimestamp(b) - deviceTimestamp(a);
  });
}

export const httpApi: NsxApi = {
  pair: (code: string, deviceInfo?: DeviceInfo) =>
    rawFetch<PairingResult>({
      method: "POST",
      path: "/api/mobile/pair/consume",
      body: { token: code, deviceInfo },
      auth: false,
    }),

  refresh: (refreshToken: string) =>
    rawFetch<RefreshResult>({
      method: "POST",
      path: "/api/nsx-native-token/rotate",
      body: { refreshToken },
      auth: false,
    }),

  logout: async (refreshToken: string) => {
    await rawFetch<{ success: boolean }>({
      method: "POST",
      path: "/api/mobile/auth/logout",
      body: { refreshToken },
      auth: true,
    });
  },

  getCompanies: () => rawFetch<Company[]>({ method: "GET", path: "/api/mobile/companies" }),

  getStatus: (companyId) =>
    rawFetch<DashboardStatus>({
      method: "GET",
      path: `/api/mobile/status?companyId=${encodeURIComponent(companyId)}`,
    }),

  getCustomers: (companyId, search) => {
    const qs = new URLSearchParams({ companyId });
    if (search) qs.set("search", search);
    return rawFetch<Customer[]>({ method: "GET", path: `/api/mobile/customers?${qs.toString()}` });
  },

  getCustomer: (id) => rawFetch<Customer | undefined>({ method: "GET", path: `/api/mobile/customers/${id}` }),

  createCustomer: (companyId, input) =>
    rawFetch<Customer>({
      method: "POST",
      path: "/api/mobile/customers",
      body: { companyId, ...input },
    }),

  updateCustomer: (id: CustomerId, input: CustomerInput, expectedVersion?: number) =>
    rawFetch<Customer>({
      method: "PUT",
      path: `/api/mobile/customers/${id}`,
      // Backend concurrency sözleşmesi: mevcut müşterinin `version`
      // değeri `expectedVersion` olarak hem body'de hem header'da gider.
      body: expectedVersion !== undefined ? { ...input, expectedVersion } : input,
      headers:
        expectedVersion !== undefined
          ? { "X-Expected-Version": String(expectedVersion) }
          : undefined,
    }),

  deleteCustomer: (id, expectedVersion?: number) =>
    rawFetch<void>({
      method: "DELETE",
      path: `/api/mobile/customers/${id}`,
      body: expectedVersion !== undefined ? { expectedVersion } : undefined,
      headers:
        expectedVersion !== undefined
          ? { "X-Expected-Version": String(expectedVersion) }
          : undefined,
    }),

  getRecentTransactions: (companyId, take = 20) =>
    rawFetch<Transaction[]>({
      method: "GET",
      path: `/api/mobile/transactions/recent?companyId=${companyId}&take=${take}`,
    }),

  getCustomerTransactions: (customerId) =>
    rawFetch<Transaction[]>({
      method: "GET",
      path: `/api/mobile/customers/${customerId}/transactions`,
    }),

  createTransaction: (companyId, input: TransactionInput) => {
    const path = input.kind === "debt" ? "/api/mobile/debts" : "/api/mobile/collections";
    return rawFetch<Transaction>({ method: "POST", path, body: { companyId, ...input } });
  },

  updateTransaction: (id: TransactionId, input: TransactionInput, expectedVersion?: number) => {
    const path =
      input.kind === "debt" ? `/api/mobile/debts/${id}` : `/api/mobile/collections/${id}`;
    return rawFetch<Transaction>({
      method: "PUT",
      path,
      body: expectedVersion !== undefined ? { ...input, expectedVersion } : input,
      headers:
        expectedVersion !== undefined
          ? { "X-Expected-Version": String(expectedVersion) }
          : undefined,
    });
  },

  deleteTransaction: (id, kind?: TransactionKind, expectedVersion?: number) => {
    const path =
      kind === "collection"
        ? `/api/mobile/collections/${id}`
        : `/api/mobile/debts/${id}`;
    return rawFetch<void>({
      method: "DELETE",
      path,
      body: expectedVersion !== undefined ? { expectedVersion } : undefined,
      headers:
        expectedVersion !== undefined
          ? { "X-Expected-Version": String(expectedVersion) }
          : undefined,
    });
  },

  createInvite: async (_companyId: string) => {
    const raw = await rawFetch<InviteWireResult>({
      method: "POST",
      path: "/api/mobile/pair/invite",
      body: { expiresInSeconds: 180 },
    });
    return normalizeInvite(raw);
  },

  getDevices: async () => {
    // Live .NET contract uses deviceId/deviceName/isActive/isCurrent/...;
    // older frontend builds expected id/name/status/self. Normalize once here
    // so the screen never sees wire-format differences.
    const res = await rawFetch<DeviceWireResult[] | { devices: DeviceWireResult[] }>({
      method: "GET",
      path: "/api/mobile/devices",
    });
    const rows = Array.isArray(res) ? res : res && Array.isArray(res.devices) ? res.devices : [];
    return dedupeDevices(rows.map(normalizeDevice).filter((d): d is ConnectedDevice => d !== null));
  },

  registerPushToken: (input: PushTokenInput) =>
    rawFetch<PushTokenResult>({
      method: "POST",
      path: "/api/mobile/devices/push-token",
      body: {
        token: input.token,
        provider: input.provider ?? "expo",
        platform: input.platform ?? "unknown",
      },
    }),

  getInbox: (take = 50) =>
    rawFetch<InboxMessage[]>({
      method: "GET",
      path: `/api/mobile/inbox?take=${take}`,
    }),

  markInboxRead: (input: InboxMarkReadInput) =>
    rawFetch<{ success: boolean }>({
      method: "POST",
      path: "/api/mobile/inbox/mark-read",
      body: input,
    }),

  deleteInbox: (input: InboxDeleteInput) =>
    rawFetch<{ success: boolean; deleted: number }>({
      method: "POST",
      path: "/api/mobile/inbox/delete",
      body: input,
    }),

  getInboxMessage: (id: string) =>
    rawFetch<InboxMessage>({
      method: "GET",
      path: `/api/mobile/inbox/${encodeURIComponent(id)}`,
    }),

  getNotificationPrefs: () =>
    rawFetch<NotificationPrefs>({
      method: "GET",
      path: "/api/mobile/devices/notification-prefs",
    }),

  updateNotificationPrefs: (prefs: NotificationPrefs) =>
    rawFetch<NotificationPrefs>({
      method: "PUT",
      path: "/api/mobile/devices/notification-prefs",
      body: prefs,
    }),
};
