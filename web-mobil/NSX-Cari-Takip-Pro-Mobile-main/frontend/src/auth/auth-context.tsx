// Auth context — single source of truth for session lifecycle.
//
// Boot: loadSession() rehydrates from SecureStore (Keychain / Android
// Keystore); if the refresh token is still valid, the user stays
// signed in across app restarts, phone restarts, and normal token
// expiration. Only explicit logout, server-side revoke, device
// de-authorization, or app reinstall clear the session.

import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import {
  clearSession,
  getSessionSync,
  loadSession,
  saveSession,
  setActiveCompany,
  sessionMode,
  subscribeSession,
  type Session,
} from "./session";
import { nsxApi } from "../api";
import { queryClient } from "../query-client";
import type { Company } from "../api/types";
import { adoptNativeDeviceId, buildNativeDeviceInfo } from "./device-identity";
import { tryRegisterPushAfterLogin } from "../notifications/push-register";

interface AuthState {
  ready: boolean;
  session: Session | null;
  companies: Company[];
  activeCompanyId: string | null;
  companyName: string | null;
  pair: (code: string) => Promise<void>;
  logout: () => Promise<void>;
  switchCompany: (companyId: string) => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [ready, setReady] = useState(false);
  const [session, setSession] = useState<Session | null>(null);
  const [companies, setCompanies] = useState<Company[]>([]);

  // Hydrate on mount. Live network failures do NOT wipe the session.
  useEffect(() => {
    let alive = true;
    (async () => {
      const s = await loadSession();
      if (!alive) return;
      if (s) {
        setSession(s);
        try {
          const list = await nsxApi.getCompanies();
          if (alive && getSessionSync()?.tenantId === s.tenantId) setCompanies(list);
        } catch {
          // Transient failure: keep session; screens will show retry states.
        }
      }
      setReady(true);
    })();
    // Subscribe to session changes triggered by token-manager (e.g.
    // refresh-revoked wipes the session → we get null and the router
    // redirects to /login).
    let identity: string | null = null;
    const unsub = subscribeSession((s) => {
      const nextIdentity = s ? `${s.mode}:${s.tenantId}:${s.mobileDeviceId}:${s.companyId}` : null;
      if (identity !== nextIdentity) {
        void queryClient.cancelQueries();
        queryClient.clear();
        setCompanies([]);
        identity = nextIdentity;
      }
      if (!alive) return;
      setSession(s);
      if (!s) setCompanies([]);
    });
    return () => {
      alive = false;
      unsub();
    };
  }, []);

  const pair = useCallback(async (code: string) => {
    const deviceInfo = await buildNativeDeviceInfo();
    const res = await nsxApi.pair(code, deviceInfo);
    await adoptNativeDeviceId(res.mobileDeviceId ?? deviceInfo.deviceId);
    const newSession: Session = {
      mode: sessionMode,
      accessToken: res.accessToken,
      refreshToken: res.refreshToken,
      accessTokenExpiresAt: res.accessTokenExpiresAt,
      refreshTokenExpiresAt: res.refreshTokenExpiresAt,
      tenantId: res.tenantId,
      mobileDeviceId: res.mobileDeviceId ?? deviceInfo.deviceId,
      companyId: res.defaultCompanyId,
      companyName: res.companyName,
      pairedBy: res.pairedBy,
    };
    await saveSession(newSession);
    setSession(newSession);
    setCompanies(res.companies);
    void tryRegisterPushAfterLogin();
  }, []);

  const logout = useCallback(async () => {
    const current = session;
    // Best-effort server revoke; even if the network fails we still
    // wipe local secrets so the device is not signed in locally.
    if (current?.refreshToken) {
      try {
        await nsxApi.logout(current.refreshToken);
      } catch {}
    }
    await clearSession();
    setSession(null);
    setCompanies([]);
  }, [session]);

  const switchCompany = useCallback(async (companyId: string) => {
    await setActiveCompany(companyId);
    setSession((prev) => (prev ? { ...prev, companyId } : prev));
  }, []);

  const value = useMemo<AuthState>(
    () => ({
      ready,
      session,
      companies,
      activeCompanyId: session?.companyId ?? null,
      companyName: companies.find((company) => company.id === session?.companyId)?.name ?? session?.companyName ?? null,
      pair,
      logout,
      switchCompany,
    }),
    [ready, session, companies, pair, logout, switchCompany],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside AuthProvider");
  return ctx;
}
