// Internal indirection to break the token-manager ↔ api/index.ts circle.
// api/index.ts sets this at module init; token-manager reads it via a
// getter so we never resolve modules out of order.
import type { NsxApi } from "./mock-client";

let current: NsxApi | null = null;

export function setNsxApiInternal(api: NsxApi): void {
  current = api;
}

export function nsxApiInternal(): NsxApi {
  if (!current) throw new Error("nsxApi not initialized");
  return current;
}
