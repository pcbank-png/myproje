import { useCallback, useEffect } from "react";
import { AppState } from "react-native";
import { useFocusEffect } from "expo-router";
import { isMockMode } from "@/src/api";
import { refreshInboxFromServer } from "./inbox";

/** Dashboard zili + arka plandan dönüşte inbox tazele. */
export function useInboxPolling() {
  useFocusEffect(
    useCallback(() => {
      if (isMockMode) return;
      void refreshInboxFromServer({ notify: true });
    }, []),
  );

  useEffect(() => {
    if (isMockMode) return;
    const sub = AppState.addEventListener("change", (state) => {
      if (state === "active") void refreshInboxFromServer({ notify: true });
    });
    return () => sub.remove();
  }, []);
}
