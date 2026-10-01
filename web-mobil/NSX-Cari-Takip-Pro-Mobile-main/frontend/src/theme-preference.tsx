// Tema tercihi (system / light / dark) — kalıcı, uygulama yeniden
// başlatıldığında da hatırlanır. AsyncStorage üzerinde saklanır.
//
// - "system": OS'un color scheme'ine uy (default)
// - "light": her koşulda açık tema
// - "dark":  her koşulda koyu tema
//
// ThemeProviderı `_layout.tsx`'te uygulamayı sarmalar. `useTheme()` bu
// state'ten okur; tema değişimi navigation reset yapmaz — sadece
// bileşenlerin renk hesaplamalarını yeniden çalıştırır.

import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from "react";
import { useColorScheme } from "react-native";
import AsyncStorage from "@react-native-async-storage/async-storage";
import type { ColorScheme } from "./theme";

export type ThemePreference = "system" | "light" | "dark";

interface ThemePreferenceState {
  preference: ThemePreference;
  resolvedScheme: ColorScheme;
  setPreference: (p: ThemePreference) => void;
  ready: boolean;
}

const STORAGE_KEY = "nsx.theme-preference.v1";

const ThemePreferenceContext = createContext<ThemePreferenceState | null>(null);

export function ThemePreferenceProvider({ children }: { children: React.ReactNode }) {
  const systemScheme = useColorScheme();
  const [preference, setPreferenceState] = useState<ThemePreference>("system");
  const [ready, setReady] = useState(false);

  useEffect(() => {
    (async () => {
      try {
        const raw = await AsyncStorage.getItem(STORAGE_KEY);
        if (raw === "light" || raw === "dark" || raw === "system") {
          setPreferenceState(raw);
        }
      } catch {
        // yok say — default "system" kalır
      } finally {
        setReady(true);
      }
    })();
  }, []);

  const setPreference = useCallback((p: ThemePreference) => {
    setPreferenceState(p);
    // fire-and-forget; kaydedilemezse bile UI çalışır
    AsyncStorage.setItem(STORAGE_KEY, p).catch(() => {});
  }, []);

  const resolvedScheme: ColorScheme = useMemo(() => {
    if (preference === "system") return systemScheme === "dark" ? "dark" : "light";
    return preference;
  }, [preference, systemScheme]);

  const value = useMemo<ThemePreferenceState>(
    () => ({ preference, resolvedScheme, setPreference, ready }),
    [preference, resolvedScheme, setPreference, ready],
  );

  return (
    <ThemePreferenceContext.Provider value={value}>{children}</ThemePreferenceContext.Provider>
  );
}

export function useThemePreference(): ThemePreferenceState {
  const ctx = useContext(ThemePreferenceContext);
  if (!ctx) {
    // Provider yoksa (test/isolation), sistem şemasına düş.
    // Bu path yalnızca dev-time'da hit alır.
    return {
      preference: "system",
      resolvedScheme: "light",
      setPreference: () => {},
      ready: true,
    };
  }
  return ctx;
}
