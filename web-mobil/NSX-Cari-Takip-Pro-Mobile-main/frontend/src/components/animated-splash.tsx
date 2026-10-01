// NSX premium splash — ses ve animasyon TEK marka introsudur.
//
// Zamanlama kuralı: Sabit süre YOK. Tüm fazlar ses dosyasının gerçek
// playback durumuna bağlıdır (`startSplashIntro` event'leri):
//   - onStart   : ses çalmaya başladı → giriş fazı (halo soft→canlı
//                 büyür; sürenin ortasında softlaşıp küçülür; logo
//                 belirir, shimmer ikinci swell bölgesinde 2 tur)
//   - onNearEnd : bitime ~500 ms kala → logo fade + root scrim açılır
//   - onEnd     : ses bitti → splash tamamen kapanır, ana ekran gelir
//
// Ses sona kadar kesintisiz çalar; splash kapanışı sesi erken durdurmaz.
// Ses gecikirse animasyon da onunla birlikte gecikir (onStart tetiklenene
// kadar animasyon başlamaz). Cold launch dışında tekrar oynatılmaz
// (audio.ts modül guard'ı). Auth/session kontrolü arka planda; intro
// bitince doğrudan uygulamaya geçilir.

import React, { useEffect, useRef } from "react";
import { Image, StyleSheet, View } from "react-native";
import Animated, {
  Easing,
  interpolate,
  runOnJS,
  useAnimatedStyle,
  useSharedValue,
  withDelay,
  withRepeat,
  withSequence,
  withTiming,
} from "react-native-reanimated";
import { useTheme } from "@/src/theme";
import {
  startSplashIntro,
  SPLASH_DURATION_MS,
} from "@/src/utils/audio";

const WORDMARK = require("../../assets/images/nsx-header-dark.png");
// Kullanıcının onayladığı yatay NSX logo/header asseti: 1240 × 380
const WORDMARK_RATIO = 380 / 1240;
const WORDMARK_W = 320;

// Giriş fazı sabitleri
const ENTER_OPACITY_MS = 600;
const ENTER_SCALE_MS = 700;
const ENTER_HALO_DELAY = 80;
const SHIMMER_DELAY = 700;
// Kapanış fazı — sesin son ~500 ms'sine bindirilir
const EXIT_MS = 500;
const ROOT_FADE_DELAY_MS = 100;
const ROOT_FADE_MS = 400;

interface Props {
  onFinish: () => void;
}

export function AnimatedSplash({ onFinish }: Props) {
  const { colors } = useTheme();

  const opacity = useSharedValue(0);
  const scale = useSharedValue(0.9);
  /** 0 = yok/soft küçük · 1 = canlı büyük */
  const halo = useSharedValue(0);
  const rootOpacity = useSharedValue(1);
  const shimmer = useSharedValue(0);

  const phase = useRef({ exiting: false, finished: false });

  useEffect(() => {
    const finishOnce = () => {
      if (phase.current.finished) return;
      phase.current.finished = true;
      onFinish();
    };

    const beginExit = () => {
      if (phase.current.exiting || phase.current.finished) return;
      phase.current.exiting = true;
      // Kalan halo varsa sessizce sönsün; logo fade + scale-down
      halo.value = withTiming(0, { duration: EXIT_MS, easing: Easing.in(Easing.cubic) });
      opacity.value = withTiming(0, { duration: EXIT_MS, easing: Easing.in(Easing.cubic) });
      scale.value = withTiming(0.88, { duration: EXIT_MS, easing: Easing.inOut(Easing.cubic) });
      rootOpacity.value = withDelay(
        ROOT_FADE_DELAY_MS,
        withTiming(0, { duration: ROOT_FADE_MS, easing: Easing.in(Easing.cubic) }, (done) => {
          if (done) runOnJS(finishOnce)();
        }),
      );
    };

    startSplashIntro({
      onStart: (durationMs) => {
        opacity.value = withTiming(1, {
          duration: ENTER_OPACITY_MS,
          easing: Easing.out(Easing.cubic),
        });
        scale.value = withTiming(1, {
          duration: ENTER_SCALE_MS,
          easing: Easing.out(Easing.cubic),
        });

        // Halo: soft→canlı büyü, tam ortada softlaşıp küçül.
        // Zamanlama ses süresine göre ölçeklenir.
        const usable = Math.max(1200, durationMs - EXIT_MS);
        const mid = usable / 2;
        const growMs = Math.min(1100, Math.max(520, mid * 0.72));
        const softShrinkMs = Math.min(900, Math.max(480, mid * 0.55));
        const peakHold = Math.max(60, mid - ENTER_HALO_DELAY - growMs);

        halo.value = withDelay(
          ENTER_HALO_DELAY,
          withSequence(
            // Soft → canlı büyüme
            withTiming(1, {
              duration: growMs,
              easing: Easing.out(Easing.cubic),
            }),
            // Zirvede kısa nefes, sonra softlaşıp küçül
            withDelay(
              peakHold,
              withTiming(0.08, {
                duration: softShrinkMs,
                easing: Easing.inOut(Easing.cubic),
              }),
            ),
          ),
        );

        const midSpan = Math.max(0, durationMs - EXIT_MS - SHIMMER_DELAY);
        const sweep = Math.min(800, Math.max(350, midSpan / 2));
        shimmer.value = withDelay(
          SHIMMER_DELAY,
          withRepeat(
            withSequence(
              withTiming(1, { duration: sweep, easing: Easing.inOut(Easing.cubic) }),
              withTiming(0, { duration: 0 }),
            ),
            2,
            false,
          ),
        );
      },
      onNearEnd: () => beginExit(),
      onEnd: () => {
        beginExit();
        setTimeout(finishOnce, EXIT_MS + ROOT_FADE_DELAY_MS + ROOT_FADE_MS + 120);
      },
    });

    const safety = setTimeout(() => {
      beginExit();
      setTimeout(finishOnce, EXIT_MS + ROOT_FADE_MS + 200);
    }, SPLASH_DURATION_MS + 1500);
    return () => clearTimeout(safety);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const rootStyle = useAnimatedStyle(() => ({ opacity: rootOpacity.value }));
  const logoStyle = useAnimatedStyle(() => ({
    opacity: opacity.value,
    transform: [{ scale: scale.value }],
  }));

  // Soft (düşük opacity, küçük) → canlı (yüksek opacity, büyük)
  const haloOuterStyle = useAnimatedStyle(() => {
    const t = halo.value;
    return {
      opacity: interpolate(t, [0, 0.35, 0.75, 1], [0, 0.18, 0.42, 0.58]),
      transform: [{ scale: interpolate(t, [0, 1], [0.42, 1.28]) }],
    };
  });
  const haloCoreStyle = useAnimatedStyle(() => {
    const t = halo.value;
    return {
      opacity: interpolate(t, [0, 0.25, 0.7, 1], [0, 0.2, 0.55, 0.82]),
      transform: [{ scale: interpolate(t, [0, 1], [0.55, 1.08]) }],
    };
  });

  const shimmerStyle = useAnimatedStyle(() => {
    const translate = interpolate(shimmer.value, [0, 1], [-WORDMARK_W * 0.6, WORDMARK_W * 1.1]);
    return {
      opacity: shimmer.value === 0 ? 0 : 0.5,
      transform: [{ translateX: translate }, { rotate: "18deg" }],
    };
  });

  return (
    <Animated.View
      style={[StyleSheet.absoluteFill, styles.root, rootStyle]}
      pointerEvents="none"
    >
      <Animated.View
        style={[styles.haloOuter, { backgroundColor: colors.brandPrimary }, haloOuterStyle]}
      />
      <Animated.View
        style={[styles.haloCore, { backgroundColor: colors.brandPrimary }, haloCoreStyle]}
      />
      <Animated.View style={[styles.logoWrap, logoStyle]}>
        <View style={styles.wordmarkMask}>
          <Image source={WORDMARK} style={styles.wordmark} resizeMode="contain" />
          <Animated.View style={[styles.shimmerBand, shimmerStyle]} />
        </View>
      </Animated.View>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  root: {
    backgroundColor: "#06152A",
    alignItems: "center",
    justifyContent: "center",
  },
  haloOuter: {
    position: "absolute",
    width: 380,
    height: 380,
    borderRadius: 190,
    opacity: 0,
  },
  haloCore: {
    position: "absolute",
    width: 240,
    height: 240,
    borderRadius: 120,
    opacity: 0,
  },
  logoWrap: { alignItems: "center" },
  wordmarkMask: {
    width: WORDMARK_W,
    height: WORDMARK_W * WORDMARK_RATIO,
    overflow: "hidden",
    alignItems: "center",
    justifyContent: "center",
  },
  wordmark: { width: WORDMARK_W, height: WORDMARK_W * WORDMARK_RATIO },
  shimmerBand: {
    position: "absolute",
    top: -120,
    bottom: -120,
    width: 26,
    backgroundColor: "rgba(255,255,255,0.8)",
    shadowColor: "#FFFFFF",
    shadowOpacity: 0.9,
    shadowRadius: 20,
  },
});
