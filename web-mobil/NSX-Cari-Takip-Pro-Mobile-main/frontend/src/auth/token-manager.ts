// Silent access-token refresh manager.
//
// Guarantees:
// - App/phone restart never logs the user out. On boot, session-context
//   loads the persisted session; the first API call that needs auth
//   calls getAccessToken(), which returns the cached token if it is
//   still valid, or performs a single-flight refresh otherwise.
// - Only one refresh is in flight at a time — concurrent callers await
//   the same promise. Refresh token is rotated on every use.
// - A REFRESH_REVOKED / REFRESH_EXPIRED / DEVICE_UNAUTHORIZED error
//   clears SecureStore and emits a session change so the router pushes
//   the user back to /login.
// - Live mode failures are surfaced as errors — never falls back to mock.

import { nsxApiInternal } from "../api/internal";
import {
  clearSession,
  getSessionSync,
  updateTokens,
  type Session,
} from "./session";
import { NsxAuthError } from "../api/types";

// Refresh 30 s before the access token actually expires so the
// concurrent request wave uses the fresh token.
const REFRESH_MARGIN_MS = 30_000;

let inFlight: Promise<string> | null = null;
let inFlightToken: string | null = null;

function isAccessTokenFresh(s: Session): boolean {
  return Date.parse(s.accessTokenExpiresAt) - Date.now() > REFRESH_MARGIN_MS;
}

async function doRefresh(current: Session): Promise<string> {
  try {
    const res = await nsxApiInternal().refresh(current.refreshToken);
    if (getSessionSync()?.refreshToken !== current.refreshToken) throw new Error("Oturum değişti.");
    await updateTokens(res, current.refreshToken);
    return res.accessToken;
  } catch (e) {
    // Refresh failed: refresh token is unusable. Wipe and force re-scan.
    if (e instanceof NsxAuthError && getSessionSync()?.refreshToken === current.refreshToken) {
      if (
        e.code === "REFRESH_EXPIRED" ||
        e.code === "REFRESH_REVOKED" ||
        e.code === "DEVICE_UNAUTHORIZED" ||
        e.code === "TOKEN_INVALID"
      ) {
        await clearSession();
      }
    } else {
      // Network / server error — keep session, propagate error upward so
      // UI can retry. Do NOT log user out on transient failures.
    }
    throw e;
  }
}

/** Returns a valid access token, refreshing silently if needed. */
export async function getAccessToken(): Promise<string | null> {
  const s = getSessionSync();
  if (!s) return null;
  if (isAccessTokenFresh(s)) return s.accessToken;
  if (!inFlight || inFlightToken !== s.refreshToken) {
    inFlightToken = s.refreshToken;
    const pending = doRefresh(s).finally(() => {
      if (inFlight === pending) { inFlight = null; inFlightToken = null; }
    });
    inFlight = pending;
  }
  return inFlight;
}

/** Reactive refresh triggered by a 401 from an authorized request. */
export async function forceRefresh(rejectedAccessToken?: string): Promise<string | null> {
  const s = getSessionSync();
  if (!s) return null;
  if (rejectedAccessToken && s.accessToken !== rejectedAccessToken) return s.accessToken;
  if (!inFlight || inFlightToken !== s.refreshToken) {
    inFlightToken = s.refreshToken;
    const pending = doRefresh(s).finally(() => {
      if (inFlight === pending) { inFlight = null; inFlightToken = null; }
    });
    inFlight = pending;
  }
  return inFlight;
}
