import type { ExpoConfig } from "expo/config";

import appJson from "./app.json";

const base = appJson.expo as ExpoConfig;

/** EAS projectId — push token için zorunlu (Expo SDK 50+). `npx eas init` veya EXPO_PUBLIC_EAS_PROJECT_ID */
const projectId =
  process.env.EXPO_PUBLIC_EAS_PROJECT_ID?.trim() ||
  (base.extra as { eas?: { projectId?: string } } | undefined)?.eas?.projectId;

export default (): ExpoConfig => ({
  ...base,
  extra: {
    ...(base.extra ?? {}),
    eas: {
      ...((base.extra as { eas?: object } | undefined)?.eas ?? {}),
      ...(projectId ? { projectId } : {}),
    },
  },
});
