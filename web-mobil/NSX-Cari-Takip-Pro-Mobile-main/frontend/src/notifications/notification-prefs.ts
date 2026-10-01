import AsyncStorage from "@react-native-async-storage/async-storage";
import { isMockMode, nsxApi } from "@/src/api";
import type { NotificationPrefs } from "@/src/api/types";

export const NOTIF_STORAGE_PREFIX = "nsx.notif.v1.";

export const DEFAULT_NOTIFICATION_PREFS: NotificationPrefs = {
  master: true,
  reminders: true,
  collections: true,
  debts: true,
  system: true,
};

const KEYS: (keyof NotificationPrefs)[] = [
  "master",
  "reminders",
  "collections",
  "debts",
  "system",
];

export async function readLocalNotificationPrefs(): Promise<NotificationPrefs> {
  const prefs = { ...DEFAULT_NOTIFICATION_PREFS };
  try {
    await Promise.all(
      KEYS.map(async (key) => {
        const raw = await AsyncStorage.getItem(NOTIF_STORAGE_PREFIX + key);
        if (raw !== null) prefs[key] = raw === "1";
      }),
    );
  } catch {
    // varsayılan
  }
  return prefs;
}

export async function writeLocalNotificationPrefs(prefs: NotificationPrefs): Promise<void> {
  await Promise.all(
    KEYS.map((key) =>
      AsyncStorage.setItem(NOTIF_STORAGE_PREFIX + key, prefs[key] ? "1" : "0"),
    ),
  );
}

/** Sunucuya kaydet (live); mock'ta no-op. */
export async function syncNotificationPrefsToServer(
  prefs?: NotificationPrefs,
): Promise<void> {
  if (isMockMode) return;
  try {
    const payload = prefs ?? (await readLocalNotificationPrefs());
    await nsxApi.updateNotificationPrefs(payload);
  } catch {
    // sessiz — UI yerel tercihle devam eder
  }
}

/** Live modda sunucudan çek; yoksa yerel. */
export async function hydrateNotificationPrefs(): Promise<NotificationPrefs> {
  if (isMockMode) return readLocalNotificationPrefs();
  try {
    const remote = await nsxApi.getNotificationPrefs();
    await writeLocalNotificationPrefs(remote);
    return remote;
  } catch {
    return readLocalNotificationPrefs();
  }
}
