// Custom TabView (react-native-tab-view) Expo Router ile senkron değil.
// Ana Sayfa → Müşteriler / Hareketler gibi geçişler bu context üzerinden yapılır.

import React, { createContext, useCallback, useContext, useMemo, useState } from "react";

export type TabKey = "index" | "musteriler" | "hareketler" | "ayarlar";

interface TabNavContextValue {
  index: number;
  setIndex: (i: number) => void;
  jumpTo: (key: TabKey) => void;
}

const TabNavContext = createContext<TabNavContextValue | null>(null);

const KEY_TO_INDEX: Record<TabKey, number> = {
  index: 0,
  musteriler: 1,
  hareketler: 2,
  ayarlar: 3,
};

export function TabNavProvider({ children }: { children: React.ReactNode }) {
  const [index, setIndex] = useState(0);

  const jumpTo = useCallback((key: TabKey) => {
    const next = KEY_TO_INDEX[key];
    if (next !== undefined) setIndex(next);
  }, []);

  const value = useMemo(
    () => ({ index, setIndex, jumpTo }),
    [index, jumpTo],
  );

  return <TabNavContext.Provider value={value}>{children}</TabNavContext.Provider>;
}

export function useTabNav(): TabNavContextValue {
  const ctx = useContext(TabNavContext);
  if (!ctx) {
    throw new Error("useTabNav must be used within TabNavProvider");
  }
  return ctx;
}
