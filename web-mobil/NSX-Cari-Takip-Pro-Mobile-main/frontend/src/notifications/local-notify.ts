// Yerel OS banner — ses açık (hatırlatma / işlem).
import { getSessionSync } from "@/src/auth/session";
import { Platform } from "react-native";
import * as Notifications from "expo-notifications";
import {
  channelForCategory,
  ensureNotificationChannels,
} from "./notification-channels";

export async function ensureNotificationPermission(): Promise<boolean> {
  if (Platform.OS === "web") return false;
  await ensureNotificationChannels();
  const current = await Notifications.getPermissionsAsync();
  if (current.status === "granted") return true;
  const asked = await Notifications.requestPermissionsAsync({
    ios: {
      allowAlert: true,
      allowBadge: true,
      allowSound: true,
    },
  });
  return asked.status === "granted";
}

export async function presentLocalNotification(
  title: string,
  body: string,
  options?: { category?: string; messageId?: string },
): Promise<{ ok: boolean; reason?: string }> {
  if (Platform.OS === "web") return { ok: false, reason: "web" };
  const session = getSessionSync();

  const granted = await ensureNotificationPermission();
  if (!granted) return { ok: false, reason: "permission" };

  await ensureNotificationChannels();
  const channelId = channelForCategory(options?.category);
  const current = getSessionSync();
  if (!session || session.tenantId !== current?.tenantId || session.mobileDeviceId !== current?.mobileDeviceId)
    return { ok: false, reason: "account_changed" };

  try {
    await Notifications.scheduleNotificationAsync({
      content: {
        title: title.trim() || "NSX Cari",
        body: body.trim() || "Yeni bildirim",
        // iOS: 'default' string sesi garanti eder; boolean bazen sessiz kalır.
        sound: "default",
        data: { tenantId: session.tenantId, mobileDeviceId: session.mobileDeviceId, messageId: options?.messageId, category: options?.category, localFallback: true },
        priority: Notifications.AndroidNotificationPriority.HIGH,
      },
      trigger: Platform.OS === "android" ? { channelId } : null,
    });
    return { ok: true };
  } catch (e) {
    return {
      ok: false,
      reason: e instanceof Error ? e.message : "schedule_failed",
    };
  }
}
