// Persistent session storage for the token-exchange auth model.
//
// Design guarantees:
// - The refresh token is stored in SecureStore (iOS Keychain / Android
//   Keystore) and survives app restart, phone restart, and OS reboots.
// - Access tokens are short-lived and refreshed silently by
//   src/auth/token-manager.ts — normal expiry never logs the user out.
// - Mock and Live sessions live under separate keys; switching modes
//   never leaks credentials or falls back to demo data.
// - The user is only pushed back to /login when the refresh token itself
//   is missing/expired/revoked (i.e. explicit logout, server-side revoke,
//   device de-authorization, account security change, or app reinstall).
// - No cookies, no CSRF token, no User-Agent pinning — those belong to
//   the existing QR web login flow and stay untouched on the server.

import * as SecureStore from "expo-secure-store";
import { Platform } from "react-native";

export type SessionMode = "mock" | "live";

export interface Session {
  /** Which stack issued this session — hard barrier against cross-mode use. */
  mode: SessionMode;
  /** Short-lived Bearer access token. */
  accessToken: string;
  /** Long-lived rotating refresh token. Only used against /auth/refresh. */
  refreshToken: string;
  /** ISO. Client refreshes access token proactively before this. */
  accessTokenExpiresAt: string;
  /** ISO. When this passes, the user must re-scan a QR. */
  refreshTokenExpiresAt: string;
  tenantId: string;
  /** Current native device row id on the server (newer sessions). */
  mobileDeviceId?: string;
  companyId: string;
  companyName: string;
  /** Desktop device / user that issued the pairing QR. */
  pairedBy?: { deviceId?: string; displayName?: string };
}

const CURRENT_MODE: SessionMode =
  process.env.EXPO_PUBLIC_NSX_MODE === "live" ? "live" : "mock";
const KEY = `nsx.session.v2.${CURRENT_MODE}`;
const LEGACY_KEYS = ["nsx.session.v1", "nsx.session.v2"]; // pre-mode-split

let memory: Session | null = null;
let generation = 0;
let storageQueue: Promise<void> = Promise.resolve();
function serializeStorage(task: () => Promise<void>): Promise<void> {
  const pending = storageQueue.then(task);
  storageQueue = pending.catch(() => {});
  return pending;
}
const listeners = new Set<(s: Session | null) => void>();

function emit(): void {
  for (const l of listeners) l(memory);
}

async function purgeLegacy(): Promise<void> {
  if (Platform.OS === "web") return;
  for (const k of LEGACY_KEYS) {
    try {
      await SecureStore.deleteItemAsync(k);
    } catch {}
  }
}

export async function saveSession(s: Session): Promise<void> {
  if (s.mode !== CURRENT_MODE) {
    // Refuse to persist a session from a different runtime mode; this
    // is what prevents live→mock fallback (and vice versa).
    throw new Error(`Session mode mismatch: expected ${CURRENT_MODE}, got ${s.mode}`);
  }
  const identity = (value: Session | null) => value ? `${value.mode}:${value.tenantId}:${value.mobileDeviceId}` : null;
  if (identity(memory) !== identity(s)) generation++;
  const expected = generation;
  await serializeStorage(async () => {
    if (generation !== expected) throw new Error("Oturum değişti.");
  if (Platform.OS !== "web") {
    let failure: unknown;
    for (let attempt = 0; attempt < 3; attempt++) {
      try { await SecureStore.setItemAsync(KEY, JSON.stringify(s)); failure = null; break; }
      catch (error) { failure = error; }
    }
    if (failure) throw new Error("Oturum cihazda güvenli biçimde saklanamadı. Lütfen tekrar deneyin.");
  }
  });
  if (generation !== expected) throw new Error("Oturum değişti.");
  memory = s;
  emit();
}

/**
 * Loads the persisted session from SecureStore. Does NOT decide whether
 * the session is still valid — that's token-manager's job. It only
 * discards records whose refresh token is definitely past expiry.
 */
export async function loadSession(): Promise<Session | null> {
  if (memory) return memory;
  const expected = generation;
  if (Platform.OS === "web") return null;
  await purgeLegacy();
  try {
    const raw = await SecureStore.getItemAsync(KEY);
    if (generation !== expected) return memory;
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Session;
    if (parsed.mode !== CURRENT_MODE) {
      await SecureStore.deleteItemAsync(KEY).catch(() => {});
      return null;
    }
    const refreshExpired = Date.parse(parsed.refreshTokenExpiresAt) <= Date.now();
    if (refreshExpired) {
      await SecureStore.deleteItemAsync(KEY).catch(() => {});
      return null;
    }
    memory = parsed;
    emit();
    return memory;
  } catch {
    return null;
  }
}

/** Synchronous accessor for hot paths (token-manager, http-client). */
export function getSessionSync(): Session | null {
  return memory;
}

export async function clearSession(): Promise<void> {
  generation++;
  memory = null;
  emit();
  await serializeStorage(async () => {
    if (Platform.OS === "web") return;
    try { await SecureStore.deleteItemAsync(KEY); }
    catch {
      try { await SecureStore.setItemAsync(KEY, ""); }
      catch { throw new Error("Oturum cihazdan temizlenemedi. Uygulama verilerini temizleyin."); }
    }
  });
}

export async function setActiveCompany(companyId: string): Promise<void> {
  if (!memory) return;
  const next: Session = { ...memory, companyId };
  await saveSession(next);
}

/** Rotate tokens after a successful refresh. Never touches company info. */
export async function updateTokens(next: {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  refreshTokenExpiresAt: string;
}, expectedRefreshToken?: string): Promise<void> {
  if (!memory || (expectedRefreshToken && memory.refreshToken !== expectedRefreshToken)) throw new Error("Oturum değişti.");
  const merged: Session = { ...memory, ...next };
  // The server has already rotated the token. Retain the new token in memory
  // even if Keychain is temporarily unavailable; never replay the old token.
  memory = merged;
  try { await saveSession(merged); }
  catch (error) {
    await serializeStorage(async () => {
      if (memory?.refreshToken === merged.refreshToken && Platform.OS !== "web") await SecureStore.deleteItemAsync(KEY);
    });
    emit();
    throw error;
  }
}

export function subscribeSession(l: (s: Session | null) => void): () => void {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
}

export const sessionMode: SessionMode = CURRENT_MODE;
