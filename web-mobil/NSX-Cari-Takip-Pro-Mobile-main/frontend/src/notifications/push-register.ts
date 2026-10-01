// Expo push token kaydı + izin akışı. Web önizlemede no-op.
import { Platform } from "react-native";
import Constants from "expo-constants";
import * as Notifications from "expo-notifications";
import { nsxApi, isMockMode } from "@/src/api";
import { NsxApiError } from "@/src/api/types";
import { getSessionSync } from "@/src/auth/session";
import { ensureNotificationChannels } from "./notification-channels";
import { markNotificationShown, wasNotificationShown } from "./notification-dedupe";

export type PushRegistrationStatus =
  | "unsupported"
  | "denied"
  | "no_token"
  | "registered"
  | "mock"
  | "error";

export interface PushRegistrationResult {
  status: PushRegistrationStatus;
  token?: string;
  message?: string;
}

/** Foreground bildirimlerde banner + ses. */
export function configureNotificationHandler(): void {
  if (Platform.OS === "web") return;
  void ensureNotificationChannels();
  Notifications.setNotificationHandler({
    handleNotification: async (notification) => {
      const data = notification.request.content.data;
      const id = typeof data?.messageId === "string" ? data.messageId : undefined;
      const session = getSessionSync();
      const matchesAccount = !!session && (!data?.tenantId || data.tenantId === session.tenantId)
        && (!data?.mobileDeviceId || data.mobileDeviceId === session.mobileDeviceId);
      const show = matchesAccount && (data?.localFallback === true || !wasNotificationShown(id));
      if (show) markNotificationShown(id);
      return {
        shouldPlaySound: show,
        shouldSetBadge: show,
        shouldShowBanner: show,
        shouldShowList: show,
      };
    },
  });
}

function isExpoGo(): boolean {
  return Constants.executionEnvironment === "storeClient";
}

async function resolveProjectId(): Promise<string | undefined> {
  const extra = Constants.expoConfig?.extra as { eas?: { projectId?: string } } | undefined;
  const fromConfig =
    extra?.eas?.projectId ??
    (Constants as unknown as { easConfig?: { projectId?: string } }).easConfig?.projectId;
  const fromEnv = process.env.EXPO_PUBLIC_EAS_PROJECT_ID?.trim();
  return fromEnv || fromConfig;
}

function missingProjectIdMessage(): string {
  if (isExpoGo()) {
    return (
      "Expo Go için EAS projectId gerekir. frontend klasöründe `npx eas init` çalıştırın, " +
      "oluşan projectId’yi EXPO_PUBLIC_EAS_PROJECT_ID olarak ekleyip Expo’yu yeniden başlatın " +
      "(kalıcı push için `eas build --profile development` önerilir)."
    );
  }
  return (
    "EAS projectId tanımlı değil. `npx eas init` ve ardından development/production build alın; " +
    "veya .env içine EXPO_PUBLIC_EAS_PROJECT_ID ekleyin."
  );
}

/** İzin iste, Expo push token al, backend'e kaydet. */
async function registerPushToken(options?: { requestPermission?: boolean }): Promise<PushRegistrationResult> {
  if (Platform.OS === "web") {
    return { status: "unsupported", message: "Web önizlemede push yok." };
  }

  if (isMockMode) {
    return { status: "mock", message: "Demo modunda sunucuya token gönderilmez." };
  }

  try {
    await ensureNotificationChannels();
    const { status: existing } = await Notifications.getPermissionsAsync();
    let finalStatus = existing;
    if (existing !== "granted" && options?.requestPermission !== false) {
      const { status } = await Notifications.requestPermissionsAsync();
      finalStatus = status;
    }
    if (finalStatus !== "granted") {
      return {
        status: "denied",
        message: "Bildirim izni kapalı. Telefon Ayarlar → NSX Cari Takip → Bildirimler’i açın.",
      };
    }

    if (Platform.OS === "android") {
      await ensureNotificationChannels();
    }

    const projectId = await resolveProjectId();
    if (!projectId) {
      return { status: "no_token", message: missingProjectIdMessage() };
    }

    let tokenResult: Notifications.ExpoPushToken;
    try {
      tokenResult = await Notifications.getExpoPushTokenAsync({ projectId });
    } catch (e) {
      const detail = e instanceof Error ? e.message : String(e);
      return {
        status: "no_token",
        message: `Push token alınamadı: ${detail}. ${missingProjectIdMessage()}`,
      };
    }

    const token = tokenResult.data?.trim();
    if (!token) {
      return { status: "no_token", message: missingProjectIdMessage() };
    }

    try {
      await nsxApi.registerPushToken({
        token,
        provider: "expo",
        platform: Platform.OS === "ios" ? "ios" : Platform.OS === "android" ? "android" : "unknown",
      });
    } catch (e) {
      if (e instanceof NsxApiError) {
        if (e.status === 401) {
          return {
            status: "error",
            message: "Oturum geçersiz. Çıkış yapıp QR ile yeniden giriş yapın, sonra tekrar deneyin.",
          };
        }
        if (e.status === 404) {
          return {
            status: "error",
            message: "Sunucu bu cihazı bulamadı. Uygulamayı kapat-aç veya yeniden eşleştirin.",
          };
        }
        return {
          status: "error",
          message: `Sunucu push kaydı reddetti (HTTP ${e.status}).`,
        };
      }
      throw e;
    }

    return { status: "registered", token };
  } catch (e) {
    return {
      status: "error",
      message: e instanceof Error ? e.message : "Push kaydı başarısız.",
    };
  }
}

let registrationInFlight: Promise<PushRegistrationResult> | null = null;

export function registerPushTokenWithServer(options?: { requestPermission?: boolean }): Promise<PushRegistrationResult> {
  if (!registrationInFlight) {
    registrationInFlight = registerPushToken(options).finally(() => {
      registrationInFlight = null;
    });
  }
  return registrationInFlight;
}

/** Oturum açıkken arka planda dene; hata fırlatmaz. */
export async function tryRegisterPushAfterLogin(): Promise<void> {
  try {
    await registerPushTokenWithServer();
  } catch {
    // sessiz
  }
}
