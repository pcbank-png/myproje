import { QueryClientProvider } from "@tanstack/react-query";
import { Stack } from "expo-router";
import React, { useState } from "react";
import { LogBox, Platform, View } from "react-native";
import { KeyboardProvider } from "react-native-keyboard-controller";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { GestureHandlerRootView } from "react-native-gesture-handler";

import { ErrorBoundary } from "@/src/components/error-boundary";
import { AnimatedSplash } from "@/src/components/animated-splash";
import { queryClient } from "@/src/query-client";
import { AuthProvider } from "@/src/auth/auth-context";
import { ThemePreferenceProvider } from "@/src/theme-preference";
import { useTheme } from "@/src/theme";
import { useBrandFonts } from "@/src/hooks/use-brand-fonts";
import { prepareSplashIntro } from "@/src/utils/audio";
import { configureNotificationHandler } from "@/src/notifications/push-register";
import { PushBootstrap } from "@/src/notifications/push-bootstrap";
import { PushInboxSync } from "@/src/notifications/push-inbox-sync";

configureNotificationHandler();

// Disable logbox errors etc so that users can see the app
// and agent works as expected.
LogBox.ignoreAllLogs(true);

// Dev-only: web preview'da `#error-toast` overlay'i "POP_TO_TOP not
// handled" uyarısıyla tıklamaları engelliyor. Bu uyarı Expo Router'ın
// native stack'inin tabPress dinleyicisinden gelir (uygulamada gerçek
// bir <Tabs> yok; swipe için react-native-tab-view kullanıyoruz) ve
// production'da görünmez. Yalnızca bu mesajı console.error üzerinden
// filtrele; diğer hatalar aynen loglanmaya devam eder.
if (__DEV__ && Platform.OS === "web") {
  const originalConsoleError = console.error.bind(console);
  console.error = (...args: unknown[]) => {
    const first = args[0];
    if (
      typeof first === "string" &&
      first.includes("POP_TO_TOP") &&
      first.includes("not handled by any navigator")
    ) {
      return;
    }
    originalConsoleError(...args);
  };
}

// Splash ses player'ını bundle yüklenir yüklenmez hazırla —
// AnimatedSplash mount olduğunda `play()` sıfır gecikmeyle başlar
// (ses/animasyon senkronu için).
prepareSplashIntro();

export default function RootLayout() {
  const [splashDone, setSplashDone] = useState(false);
  const fontsReady = useBrandFonts();

  return (
    <GestureHandlerRootView style={{ flex: 1 }}>
      <SafeAreaProvider>
        <ErrorBoundary>
          <ThemePreferenceProvider>
            <QueryClientProvider client={queryClient}>
              <KeyboardProvider>
                <AuthProvider>
                  <PushBootstrap />
                  <PushInboxSync />
                  {fontsReady ? <ThemedStack /> : <View style={{ flex: 1 }} />}
                </AuthProvider>
              </KeyboardProvider>
            </QueryClientProvider>
            {!splashDone ? (
              <View style={{ position: "absolute", top: 0, left: 0, right: 0, bottom: 0 }}>
                <AnimatedSplash onFinish={() => setSplashDone(true)} />
              </View>
            ) : null}
          </ThemePreferenceProvider>
        </ErrorBoundary>
      </SafeAreaProvider>
    </GestureHandlerRootView>
  );
}

function ThemedStack() {
  const { colors } = useTheme();
  return (
    <Stack
      screenOptions={{
        headerShown: false,
        contentStyle: { backgroundColor: colors.surface },
      }}
    >
      <Stack.Screen name="index" />
      <Stack.Screen name="login" />
      <Stack.Screen name="(tabs)" />
      <Stack.Screen name="musteri/[id]" />
      <Stack.Screen name="cihaz-davet" />
      <Stack.Screen name="bagli-cihazlar" />
      <Stack.Screen name="ayarlar/gorunum" />
      <Stack.Screen name="ayarlar/bildirimler" />
      <Stack.Screen name="ayarlar/bildirim/[id]" />
      <Stack.Screen name="ayarlar/yedekleme" />
      <Stack.Screen name="ayarlar/yardim" />
      <Stack.Screen name="ayarlar/hakkinda" />
      <Stack.Screen
        name="qr-scanner"
        options={{ presentation: "fullScreenModal", animation: "slide_from_bottom" }}
      />
      <Stack.Screen
        name="musteri-new"
        options={{ presentation: "modal", animation: "slide_from_bottom" }}
      />
      <Stack.Screen
        name="hareket-new"
        options={{ presentation: "modal", animation: "slide_from_bottom" }}
      />
    </Stack>
  );
}
