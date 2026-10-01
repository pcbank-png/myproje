// API facade — strict mock/live split. NEVER falls back from live to
// mock; a live failure surfaces as an error to the caller.
import { setNsxApiInternal } from "./internal";
import { mockApi, type NsxApi } from "./mock-client";
import { httpApi } from "./http-client";

const rawMode = process.env.EXPO_PUBLIC_NSX_MODE;
if (rawMode !== "live" && rawMode !== "mock") throw new Error("EXPO_PUBLIC_NSX_MODE must be live or mock");
const mode: "live" | "mock" = rawMode;

export const nsxApi: NsxApi = mode === "live" ? httpApi : mockApi;
export const isMockMode = mode !== "live";
export const nsxMode: "live" | "mock" = mode;

// Wire the api into token-manager exactly once, without importing
// api/index from auth/token-manager (which would create a cycle).
setNsxApiInternal(nsxApi);

export type { NsxApi };
export * from "./types";
