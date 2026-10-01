import { Platform } from "react-native";
import Constants from "expo-constants";
import { storage } from "@/src/utils/storage";
import type { DeviceInfo } from "@/src/api/types";

const DEVICE_ID_KEY = "nsx.native.device-id.v1";

function isGuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

// Device id is an installation identifier, not a credential. It only needs
// to be stable + unique so the backend can upsert the same physical install
// instead of creating a new row on every QR pairing.
function createGuid(): string {
  let seed = Date.now() ^ Math.floor(Math.random() * 0x7fffffff);
  const nibble = () => {
    seed = (seed * 1664525 + 1013904223) >>> 0;
    return ((seed ^ Math.floor(Math.random() * 16)) & 0xf).toString(16);
  };

  const chars = Array.from({ length: 32 }, nibble);
  chars[12] = "4";
  chars[16] = ((parseInt(chars[16], 16) & 0x3) | 0x8).toString(16);
  const raw = chars.join("");
  return `${raw.slice(0, 8)}-${raw.slice(8, 12)}-${raw.slice(12, 16)}-${raw.slice(16, 20)}-${raw.slice(20)}`;
}

export async function getOrCreateNativeDeviceId(): Promise<string> {
  const existing = await storage.secureGet(DEVICE_ID_KEY, "");
  if (typeof existing === "string" && isGuid(existing)) return existing;

  const created = createGuid();
  await storage.secureSet(DEVICE_ID_KEY, created);
  return created;
}

export async function adoptNativeDeviceId(deviceId?: string | null): Promise<void> {
  const normalized = (deviceId ?? "").trim();
  if (!isGuid(normalized)) return;
  await storage.secureSet(DEVICE_ID_KEY, normalized);
}

export async function buildNativeDeviceInfo(): Promise<DeviceInfo> {
  const version = Constants.expoConfig?.version ?? "1.0.0";
  const platform = (Platform.OS === "ios" || Platform.OS === "android" ? Platform.OS : "web") as
    | "ios"
    | "android"
    | "web";
  const deviceId = await getOrCreateNativeDeviceId();

  // expo-device is intentionally not introduced this late in the release.
  // The short suffix keeps device rows distinguishable even without model
  // metadata while remaining stable across subsequent pairings.
  const baseName = platform === "ios" ? "NSX iPhone" : platform === "android" ? "NSX Android" : "NSX Web";
  const deviceName = `${baseName} · ${deviceId.slice(-4).toUpperCase()}`;

  return { platform, appVersion: version, deviceName, deviceId };
}
