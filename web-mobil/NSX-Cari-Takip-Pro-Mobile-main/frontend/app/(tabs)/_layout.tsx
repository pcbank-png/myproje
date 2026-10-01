// Native swipeable bottom tabs.
//
// Yapı:
// - `react-native-tab-view` doğrudan kullanılır (NavigationContainer'a
//   bağımlı değildir — Expo Router 57 artık @react-navigation ağacını
//   sağlamıyor). Altında `react-native-pager-view` native swipe verir.
// - TabBar bileşeni ekranın altında custom render edilir.
// - Auth guard yalnızca "session yok" durumunda `Redirect`.
// - Swipe / theme değişimi / SignalR reconnect session state'i
//   sıfırlayamaz; session AuthContext üzerinden gelir.
// - Sekme geçişi (Ana Sayfa → Müşteriler vb.) `TabNavProvider` ile.

import React, { useCallback } from "react";
import { View, Text, Pressable, StyleSheet, Platform } from "react-native";
import { Redirect } from "expo-router";
import { TabView, type SceneRendererProps, type NavigationState } from "react-native-tab-view";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useAuth } from "@/src/auth/auth-context";
import { useTheme, spacing, fonts } from "@/src/theme";
import { useRealtimeSync } from "@/src/realtime/use-realtime-sync";
import { selectionHaptic } from "@/src/utils/haptics";
import { TabNavProvider, useTabNav, type TabKey } from "@/src/navigation/tab-nav";

import DashboardScreen from "./index";
import CustomersScreen from "./musteriler";
import TransactionsScreen from "./hareketler";
import SettingsScreen from "./ayarlar";

type IconName = React.ComponentProps<typeof Feather>["name"];

interface Route {
  key: TabKey;
  title: string;
  icon: IconName;
  Component: React.ComponentType;
}

const ROUTES: Route[] = [
  { key: "index", title: "Ana Sayfa", icon: "home", Component: DashboardScreen },
  { key: "musteriler", title: "Müşteriler", icon: "users", Component: CustomersScreen },
  { key: "hareketler", title: "Hareketler", icon: "activity", Component: TransactionsScreen },
  { key: "ayarlar", title: "Ayarlar", icon: "settings", Component: SettingsScreen },
];

export default function TabsLayout() {
  const { ready, session } = useAuth();

  // SignalR yalnızca login sonrası. Mock modda no-op.
  useRealtimeSync();

  if (!ready) return null;
  if (!session) return <Redirect href="/login" />;

  return (
    <TabNavProvider>
      <TabsPager />
    </TabNavProvider>
  );
}

function TabsPager() {
  const { index, setIndex } = useTabNav();

  const renderScene = useCallback(({ route }: { route: Route }) => {
    const Comp = route.Component;
    return <Comp />;
  }, []);

  return (
    <TabView
      navigationState={{ index, routes: ROUTES }}
      onIndexChange={(next) => {
        if (next !== index) selectionHaptic();
        setIndex(next);
      }}
      renderScene={renderScene}
      renderTabBar={(props) => <BottomTabBar {...props} />}
      tabBarPosition="bottom"
      swipeEnabled
      lazy={false}
      animationEnabled
    />
  );
}

type BottomTabBarProps = SceneRendererProps & {
  navigationState: NavigationState<Route>;
};

function BottomTabBar({ navigationState, jumpTo }: BottomTabBarProps) {
  const { colors } = useTheme();
  const insets = useSafeAreaInsets();

  return (
    <View
      style={[
        styles.wrap,
        {
          backgroundColor: colors.surfaceSecondary,
          borderTopColor: colors.border,
          paddingBottom: Math.max(insets.bottom, 6),
        },
      ]}
    >
      {navigationState.routes.map((route) => {
        const focused = navigationState.routes[navigationState.index]?.key === route.key;
        const tint = focused ? colors.brandPrimary : colors.muted;
        const bg = focused ? colors.brandTertiary : "transparent";
        return (
          <Pressable
            key={route.key}
            testID={`tab-${route.key}`}
            accessibilityRole="button"
            accessibilityState={focused ? { selected: true } : {}}
            onPress={() => {
              if (!focused) {
                selectionHaptic();
                jumpTo(route.key);
              }
            }}
            style={({ pressed }) => [
              styles.item,
              {
                opacity: pressed ? 0.88 : 1,
                transform: [{ scale: pressed ? 0.96 : 1 }],
              },
            ]}
          >
            <View
              style={[
                styles.iconWrap,
                {
                  backgroundColor: bg,
                  borderColor: focused ? colors.brandPrimary + "33" : "transparent",
                },
              ]}
            >
              <Feather name={route.icon} size={20} color={tint} />
            </View>
            <Text
              style={[
                styles.label,
                {
                  color: tint,
                  fontFamily: focused ? fonts.bodyBold : fonts.bodyMedium,
                },
              ]}
            >
              {route.title}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    flexDirection: "row",
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingTop: 8,
    paddingHorizontal: spacing.xs,
    ...(Platform.OS === "web" ? { minHeight: 68 } : {}),
  },
  item: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    paddingVertical: 4,
    gap: 4,
  },
  iconWrap: {
    height: 32,
    minWidth: 52,
    paddingHorizontal: 14,
    borderRadius: 999,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
  },
  label: {
    fontSize: 11,
    letterSpacing: 0.15,
  },
});
