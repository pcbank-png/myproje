// SignalR realtime config — tek değişim noktası.
// Backend SignalR URL/event sözleşmesi ayrıca verilecek; o zaman sadece
// bu dosya (veya env) güncellenir, başka koda dokunulmaz.
//
// Referans paketten doğrulanan mevcut sunucu değerleri:
//   hub:   /Api/CaritakipCloud/hub
//   event: changesAvailable { cursor, entities: [{ entityType, entityId, version, cursor }] }
//   entityType: "customer" | "debt" | "collection"

export const REALTIME_CONFIG = {
  /** Hub path — EXPO_PUBLIC_NSX_SIGNALR_HUB env'i ile ezilebilir. */
  hubPath: process.env.EXPO_PUBLIC_NSX_SIGNALR_HUB ?? "/Api/CaritakipCloud/hub",
  events: {
    /** Sunucu bir mutation uyguladığında tenant grubuna gönderir. */
    changesAvailable: "changesAvailable",
    notificationAvailable: "notificationAvailable",
    /** Bağlantı sonrası sunucunun caller'a gönderdiği selam. */
    connected: "connected",
  },
  /** withAutomaticReconnect backoff adımları. */
  reconnectDelaysMs: [0, 2_000, 5_000, 10_000, 30_000],
  /** Realtime bağlı değilken devreye giren sessiz polling aralığı. */
  fallbackPollMs: 15_000,
} as const;
