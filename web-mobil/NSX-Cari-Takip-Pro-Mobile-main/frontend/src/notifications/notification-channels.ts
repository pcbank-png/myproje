// Android bildirim kanalları — ses + yüksek öncelik (Expo push channelId ile eşleşir).
import { Platform } from "react-native";
import * as Notifications from "expo-notifications";

export const CHANNEL_DEFAULT = "default";
export const CHANNEL_CARI = "nsx-cari";
export const CHANNEL_REMINDERS = "nsx-reminders";

let ready = false;

export async function ensureNotificationChannels(): Promise<void> {
  if (Platform.OS !== "android" || ready) return;

  const common = {
    enableVibrate: true,
    showBadge: true,
    sound: "default" as const,
    vibrationPattern: [0, 250, 120, 250] as number[],
  };

  await Notifications.setNotificationChannelAsync(CHANNEL_DEFAULT, {
    name: "NSX Cari",
    importance: Notifications.AndroidImportance.HIGH,
    ...common,
  });

  await Notifications.setNotificationChannelAsync(CHANNEL_CARI, {
    name: "NSX Cari işlemler",
    importance: Notifications.AndroidImportance.HIGH,
    ...common,
  });

  await Notifications.setNotificationChannelAsync(CHANNEL_REMINDERS, {
    name: "NSX Hatırlatmalar",
    description: "Vade ve ödeme hatırlatmaları",
    importance: Notifications.AndroidImportance.MAX,
    ...common,
  });

  ready = true;
}

export function channelForCategory(category?: string): string {
  if ((category ?? "").toLowerCase() === "reminders") return CHANNEL_REMINDERS;
  return CHANNEL_CARI;
}
