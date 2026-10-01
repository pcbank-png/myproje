// Oturum varken push token kaydını dener (sessiz).
import { useEffect } from "react";
import { AppState } from "react-native";
import { useAuth } from "@/src/auth/auth-context";
import { registerPushTokenWithServer } from "./push-register";
import { refreshInboxFromServer } from "./inbox";
import { syncNotificationPrefsToServer } from "./notification-prefs";

export function PushBootstrap() {
  const { session, ready } = useAuth();

  useEffect(() => {
    if (!ready || !session?.tenantId) return;
    let disposed = false;
    let needsRetry = true;
    const register = async (requestPermission = false) => {
      const result = await registerPushTokenWithServer({ requestPermission });
      if (!disposed) needsRetry = result.status === "error" || result.status === "no_token";
    };
    void register(true);
    void refreshInboxFromServer({ notify: false });
    void syncNotificationPrefsToServer();
    const sub = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        void register();
        void syncNotificationPrefsToServer();
      }
    });
    const retry = setInterval(() => {
      if (needsRetry && AppState.currentState === "active") void register();
    }, 60_000);
    return () => {
      disposed = true;
      sub.remove();
      clearInterval(retry);
    };
  }, [ready, session?.tenantId, session?.mobileDeviceId]);

  return null;
}
