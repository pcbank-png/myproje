// Sync incoming notifications and open their detail on a native notification tap.
import { useEffect, useRef, useState } from "react";
import { AppState, InteractionManager, Platform } from "react-native";
import * as Notifications from "expo-notifications";
import { useRootNavigationState, useRouter, useSegments } from "expo-router";
import { getSessionSync } from "@/src/auth/session";
import { useAuth } from "@/src/auth/auth-context";
import { addInboxItem, getInboxItem, markInboxNotified } from "./inbox";
import { notificationResponseKey, readNotificationContent } from "./notification-content";
import { traceNotificationTap } from "./notification-debug";

async function scopedNotification(notification: Notifications.Notification) {
  const session = getSessionSync();
  if (!session) return null;
  const data = notification.request.content.data;
  const item = readNotificationContent(notification);
  // Legacy payloads without tenant metadata are resolved through the authorized
  // inbox endpoint instead of trusting possibly old OS notification content.
  if (!data?.tenantId) return getInboxItem(item.id);
  if (data.tenantId !== session.tenantId) return null;
  if (data.mobileDeviceId && data.mobileDeviceId !== session.mobileDeviceId) return null;
  return item;
}

function notificationAccount(): string | null {
  const session = getSessionSync();
  return session ? `${session.tenantId}:${session.mobileDeviceId ?? "device"}` : null;
}

export function PushInboxSync() {
  return Platform.OS === "web" ? null : <NativePushInboxSync />;
}

export function NativePushInboxSync() {
  const router = useRouter();
  const navigation = useRootNavigationState();
  const segments: readonly string[] = useSegments();
  const { ready, session } = useAuth();
  // Includes the tap that launched a completely closed application.
  const response = Notifications.useLastNotificationResponse();
  const [pending, setPending] = useState<{ id: string; key: string; account: string } | null>(null);
  const handledKey = useRef<string | null>(null);
  const [appState, setAppState] = useState(AppState.currentState);

  useEffect(() => {
    const subscription = AppState.addEventListener("change", setAppState);
    return () => subscription.remove();
  }, []);

  useEffect(() => {
    const received = Notifications.addNotificationReceivedListener((event) => {
      // The presentation handler claims the id; claiming it here can hide push.
      void Promise.resolve().then(async () => { const item = await scopedNotification(event); if (item) await addInboxItem(item); }).catch((error) => {
        console.warn("[NSX Notification] Incoming notification could not be stored", error);
      });
    });
    return () => received.remove();
  }, []);

  useEffect(() => {
    if (!ready || !session?.tenantId || !response || response.actionIdentifier !== Notifications.DEFAULT_ACTION_IDENTIFIER) return;
    const key = notificationResponseKey(response);
    if (handledKey.current === key) return;
    let cancelled = false;
    // Store first so the detail works offline and after a cold start.
    void (async () => {
      const account = notificationAccount();
      const item = await scopedNotification(response.notification);
      if (cancelled || !account || account !== notificationAccount()) return;
      if (!item) { await Notifications.clearLastNotificationResponseAsync(); return; }
      traceNotificationTap("tap_received", { id: item.id, category: item.category, appState: AppState.currentState });
      markInboxNotified(item.id);
      await addInboxItem(item);
      traceNotificationTap("tap_prepared", { id: item.id, category: item.category });
      if (!cancelled && account === notificationAccount()) setPending({ id: item.id, key, account });
    })().catch((error) => {
      console.warn("[NSX Notification] Notification tap could not be prepared", error);
    });
    return () => { cancelled = true; };
  }, [response, ready, session?.tenantId, session?.mobileDeviceId]);

  useEffect(() => {
    if (!pending || !response || handledKey.current === pending.key) return;
    if (pending.account !== notificationAccount()) return;
    if (notificationResponseKey(response) !== pending.key) return;
    if (appState !== "active") {
      traceNotificationTap("waiting_for_foreground", { id: pending.id, appState });
      return;
    }
    // Wait for session restoration, fonts/root stack and the initial auth redirect.
    // A pending tap also survives login when the previous session has expired.
    if (!ready || !session || !navigation?.key || segments.length === 0) return;
    if (segments[0] === "index" || segments[0] === "login" || segments[0] === "qr-scanner") return;
    // iOS can deliver a reminder response while the app is still inactive.
    // Wait for activation and existing native transitions before opening a screen.
    const task = InteractionManager.runAfterInteractions(() => {
      if (AppState.currentState !== "active" || handledKey.current === pending.key || pending.account !== notificationAccount()) return;
      try {
        traceNotificationTap("navigation_started", { id: pending.id, appState: AppState.currentState });
        router.navigate({ pathname: "/ayarlar/bildirim/[id]", params: { id: pending.id } });
        traceNotificationTap("navigation_requested", { id: pending.id });
        handledKey.current = pending.key;
        void (async () => {
          try { await Notifications.clearLastNotificationResponseAsync(); } catch {}
        })();
      } catch (error) {
        console.warn("[NSX Notification] Notification detail navigation failed", error);
      }
    });
    return () => task.cancel();
  }, [pending, response, ready, session, navigation?.key, segments, router, appState]);

  return null;
}
