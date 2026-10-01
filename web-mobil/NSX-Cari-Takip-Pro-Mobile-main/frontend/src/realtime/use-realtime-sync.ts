// SignalR realtime senkronizasyonu — yalnızca mobil client altyapısı.
//
// Kurallar:
// - Yalnızca live modda ve login sonrası bağlanır (tabs layout mount'unda).
// - Bearer access token accessTokenFactory ile taşınır; token rotation'a
//   dokunmaz — token-manager'dan her bağlantıda taze token ister.
// - Event geldiğinde yeni veri sistemi YOK: sadece mevcut React Query
//   invalidate/refetch fonksiyonları tetiklenir. Bunlar RefreshControl'u
//   tetiklemez — ekran sessizce tazelenir.
// - AppState: background'da bağlantı kapanır (gereksiz iş/batarya yok),
//   foreground'da kontrol edilip gerekirse yeniden bağlanır.
// - Logout / tabs unmount → bağlantı durur.
// - Bağlantı kurulamazsa sessiz kalır; query-client'ın 15 sn'lik sessiz
//   polling fallback'i veriyi taze tutmaya devam eder.

import { useEffect } from "react";
import { AppState } from "react-native";
import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { queryClient } from "@/src/query-client";
import { getAccessToken } from "@/src/auth/token-manager";
import { useAuth } from "@/src/auth/auth-context";
import { isMockMode } from "@/src/api";
import { REALTIME_CONFIG } from "./realtime-config";
import { setRealtimeConnected } from "./realtime-state";
import { receiveInboxMessage, refreshInboxFromServer } from "@/src/notifications/inbox";
import type { InboxMessage } from "@/src/api/types";

// Hermes TextEncoder/TextDecoder polyfill (yalnızca gerektiğinde).
if (typeof globalThis.TextEncoder === "undefined") {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  require("fast-text-encoding");
}

interface ChangeEntity {
  entityType: string;
  entityId: string;
  version: number;
  cursor: number;
}

interface ChangesAvailablePayload {
  cursor: number;
  entities: ChangeEntity[];
}

// Event → mevcut query key'leri. invalidateQueries aktif query'leri sessizce
// refetch eder; RefreshControl'u veya tam ekran loader'ı tetiklemez.
function invalidateForEntities(entities?: ChangeEntity[]): void {
  if (!entities || entities.length === 0) {
    void queryClient.invalidateQueries();
    return;
  }
  const keys = new Set<string>();
  for (const e of entities) {
    if (e.entityType === "customer") {
      // Müşteri değişikliği → müşteri listeleri + detay + dashboard
      keys.add("customers");
      keys.add("customer");
      keys.add("status");
      keys.add("recent");
      keys.add("recent-full");
    } else if (e.entityType === "debt" || e.entityType === "collection") {
      // Borç/tahsilat → hareketler + ilgili müşteri + dashboard + liste bakiyeleri
      keys.add("recent");
      keys.add("recent-full");
      keys.add("customer-tx");
      keys.add("customer");
      keys.add("customers");
      keys.add("status");
    } else {
      // Bilinmeyen tip → genel sessiz tazeleme
      void queryClient.invalidateQueries();
      return;
    }
  }
  for (const key of keys) {
    void queryClient.invalidateQueries({ queryKey: [key] });
  }
}

export function useRealtimeSync(): void {
  const { session } = useAuth();
  const authed = !!session;

  useEffect(() => {
    if (!authed || isMockMode) return;
    const baseUrl = process.env.EXPO_PUBLIC_NSX_BASE_URL?.replace(/\/$/, "");
    if (!baseUrl) return;

    const connection = new HubConnectionBuilder()
      .withUrl(`${baseUrl}${REALTIME_CONFIG.hubPath}`, {
        accessTokenFactory: async () => (await getAccessToken()) ?? "",
        transport: HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect([...REALTIME_CONFIG.reconnectDelaysMs])
      .configureLogging(LogLevel.Warning)
      .build();

    let disposed = false;
    connection.on(REALTIME_CONFIG.events.changesAvailable, (payload: ChangesAvailablePayload) => {
      invalidateForEntities(payload?.entities);
    });
    connection.on(REALTIME_CONFIG.events.connected, () => {
      setRealtimeConnected(true);
    });
    connection.on(REALTIME_CONFIG.events.notificationAvailable, (message: InboxMessage) => {
      if (!disposed && message?.id) void receiveInboxMessage(message).catch(() => console.warn("Bildirim cihazda kaydedilemedi."));
    });
    connection.onreconnecting(() => setRealtimeConnected(false));
    connection.onreconnected(() => {
      setRealtimeConnected(true);
      void queryClient.invalidateQueries();
      void refreshInboxFromServer({ notify: true });
    });
    connection.onclose(() => setRealtimeConnected(false));


    const start = async () => {
      if (disposed || connection.state !== HubConnectionState.Disconnected) return;
      try {
        await connection.start();
        if (disposed) {
          await connection.stop().catch(() => {});
          return;
        }
        setRealtimeConnected(true);
        // İlk bağlantıda güncel snapshot.
        void queryClient.invalidateQueries();
        void refreshInboxFromServer({ notify: true });
      } catch {
        // Sessiz başarısızlık — polling fallback veriyi taze tutar.
        setRealtimeConnected(false);
      }
    };

    const appStateSub = AppState.addEventListener("change", (state) => {
      if (state === "active") {
        void start();
        // Kaçırılan borç/tahsilat inbox → zil + taze banner (throttle inbox.ts'te).
        void refreshInboxFromServer({ notify: true });
        return;
      }
      // Background'da hub'ı KESME — kilit ekranı / kısa arka plan için
      // SignalR + yerel banner yolu açık kalsın. OS kill ederse push gerekir.
    });

    void start();

    // Hub düşse bile inbox poll (seyrek) — 429 üretmesin.
    const inboxPoll = setInterval(() => {
      if (!disposed && AppState.currentState === "active") {
        void refreshInboxFromServer({ notify: true });
      }
    }, 15_000);

    // İlk bağlantı geçici bir ağ/sunucu hatasıyla başarısız olursa app
    // açıkken 30 sn'de bir sessizce tekrar dene. Bağlanana kadar 15 sn'lik
    // sessiz polling fallback zaten veriyi taze tutuyor.
    const retryTimer = setInterval(() => {
      if (!disposed && connection.state === HubConnectionState.Disconnected) {
        void start();
      }
    }, 30_000);

    return () => {
      disposed = true;
      clearInterval(retryTimer);
      clearInterval(inboxPoll);
      appStateSub.remove();
      setRealtimeConnected(false);
      void connection.stop().catch(() => {});
    };
  }, [authed]);
}
