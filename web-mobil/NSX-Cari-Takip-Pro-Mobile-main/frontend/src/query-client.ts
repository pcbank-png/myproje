// One QueryClient for the whole app; the provider in app/_layout.tsx uses
// this instance. Import it for cache calls outside components, for example
// queryClient.invalidateQueries or setQueryData in websocket or push
// handlers; inside components useQueryClient() returns this same instance.
//
// Auto-refresh policy (RN AppState driven):
// - App foregrounds → focusManager marks focused → active queries refetch
//   (refetchOnWindowFocus).
// - Screen mounts (tab change / first open) → refetchOnMount always runs.
// - Realtime (SignalR) bağlıyken polling kapalı; event'ler query
//   invalidation'ı tetikler. Bağlı değilken 15 sn sessiz fallback polling
//   (yalnızca aktif/gözlemlenen query'ler).
// - App backgrounds → focused=false → polling stops
//   (refetchIntervalInBackground: false). No traffic, no battery drain.
// - Background polls are silent: cached data stays on screen during
//   refetch (isPending only true on first load). Screens drive
//   RefreshControl with their own manual-refresh state, so automatic
//   refetches never show the pull-to-refresh spinner.
import { AppState, type AppStateStatus } from "react-native";
import { focusManager, QueryClient } from "@tanstack/react-query";
import { isRealtimeConnected } from "./realtime/realtime-state";
import { REALTIME_CONFIG } from "./realtime/realtime-config";

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 0,
      refetchOnMount: true,
      refetchOnWindowFocus: true,
      // SignalR bağlıyken polling kapalı (event-driven). Bağlantı yoksa
      // 15 sn'lik sessiz fallback devrede. Her iki durumda da refetch
      // RefreshControl'u tetiklemez — ekranlar manuel refresh state'ini
      // ayrı tutar.
      refetchInterval: () =>
        isRealtimeConnected() ? false : REALTIME_CONFIG.fallbackPollMs,
      refetchIntervalInBackground: false,
      retry: 1,
    },
  },
});

const onAppStateChange = (status: AppStateStatus) => {
  focusManager.setFocused(status === "active");
};
const appStateSub = AppState.addEventListener("change", onAppStateChange);
// No cleanup: lives for the lifetime of the app bundle.
void appStateSub;
