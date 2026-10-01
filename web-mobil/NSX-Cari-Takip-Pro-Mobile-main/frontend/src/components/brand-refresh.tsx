// Özel pull-to-refresh: native RefreshControl yok (spinner’ı yutuyor / web’de görünmez).
// Liste en üstteyken aşağı çek → ince halka dön; bırak → kaybol + onRefresh.
import React, { useCallback, useEffect, useRef, useState } from "react";
import {
  Animated,
  Easing,
  View,
  StyleSheet,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
  type GestureResponderEvent,
} from "react-native";
import { useTheme } from "@/src/theme";

const SHOW_DY = 28;
const FIRE_DY = 64;

/** Açık: ince açık gri ~%30. Koyu: ince açık/beyaz ~%30 (tersi). */
function useThinSpinnerStyle() {
  const { scheme } = useTheme();
  const light = scheme !== "dark";
  return {
    // İnce stroke
    borderWidth: 1.5,
    size: 28,
    // Track neredeyse görünmez; aktif yay %30 opak
    track: light ? "rgba(60,60,67,0.08)" : "rgba(242,242,247,0.08)",
    arc: light ? "rgba(142,142,147,0.30)" : "rgba(235,235,240,0.30)",
  };
}

function ThinPullRing() {
  const [spin] = useState(() => new Animated.Value(0));
  const style = useThinSpinnerStyle();

  useEffect(() => {
    const loop = Animated.loop(
      Animated.timing(spin, {
        toValue: 1,
        duration: 900,
        easing: Easing.linear,
        useNativeDriver: true,
      }),
    );
    loop.start();
    return () => loop.stop();
  }, [spin]);

  const rotate = spin.interpolate({
    inputRange: [0, 1],
    outputRange: ["0deg", "360deg"],
  });

  const box = {
    width: style.size,
    height: style.size,
    borderRadius: style.size / 2,
    borderWidth: style.borderWidth,
  };

  return (
    <View style={styles.ringWrap}>
      {/* Soft track */}
      <View style={[box, { borderColor: style.track, position: "absolute" }]} />
      {/* İnce yay — sadece iki kenar boyalı */}
      <Animated.View
        style={[
          box,
          {
            borderColor: "transparent",
            borderTopColor: style.arc,
            borderRightColor: style.arc,
            transform: [{ rotate }],
          },
        ]}
      />
    </View>
  );
}

/** Ekranın üstünde dönen ince load. */
export function BrandPullSpinner({
  active,
  topInset = 0,
}: {
  active: boolean;
  topInset?: number;
}) {
  if (!active) return null;
  return (
    <View
      pointerEvents="none"
      style={[styles.overlay, { paddingTop: topInset + 40 }]}
      testID="brand-pull-spinner"
    >
      <ThinPullRing />
    </View>
  );
}

type PullOpts = {
  onRefresh: () => void | Promise<void>;
  enabled?: boolean;
};

/**
 * `pullScrollProps` → ScrollView / FlatList
 * `pullActive` → BrandPullSpinner
 * Yenileme eşiği: aşağı ~64px çekip bırak.
 */
export function useBrandPull({ onRefresh, enabled = true }: PullOpts) {
  const [pullActive, setPullActive] = useState(false);
  const atTopRef = useRef(true);
  const touchingRef = useRef(false);
  const startYRef = useRef(0);
  const dyRef = useRef(0);
  const lockRef = useRef(false);
  const onRefreshRef = useRef(onRefresh);
  useEffect(() => { onRefreshRef.current = onRefresh; }, [onRefresh]);

  const fire = useCallback(async () => {
    if (lockRef.current || !enabled) return;
    lockRef.current = true;
    try {
      await onRefreshRef.current();
    } finally {
      lockRef.current = false;
    }
  }, [enabled]);

  const hide = useCallback(() => {
    const shouldFire = touchingRef.current && dyRef.current >= FIRE_DY && atTopRef.current;
    touchingRef.current = false;
    dyRef.current = 0;
    setPullActive(false);
    if (shouldFire) void fire();
  }, [fire]);

  const onScroll = useCallback((e: NativeSyntheticEvent<NativeScrollEvent>) => {
    const y = e.nativeEvent.contentOffset.y;
    atTopRef.current = y <= 2;
    if (touchingRef.current && y < -SHOW_DY) {
      dyRef.current = Math.max(dyRef.current, -y);
      setPullActive(true);
    } else if (y > 2) {
      setPullActive(false);
      dyRef.current = 0;
    }
  }, []);

  const onScrollBeginDrag = useCallback((e: NativeSyntheticEvent<NativeScrollEvent>) => {
    touchingRef.current = true;
    atTopRef.current = e.nativeEvent.contentOffset.y <= 2;
    startYRef.current = 0;
    dyRef.current = 0;
  }, []);

  const onScrollEndDrag = useCallback(
    (e: NativeSyntheticEvent<NativeScrollEvent>) => {
      const y = e.nativeEvent.contentOffset.y;
      const deepPull = y < -FIRE_DY || dyRef.current >= FIRE_DY;
      touchingRef.current = false;
      dyRef.current = 0;
      setPullActive(false);
      if (deepPull && enabled) void fire();
    },
    [enabled, fire],
  );

  const onTouchStart = useCallback((e: GestureResponderEvent) => {
    touchingRef.current = true;
    startYRef.current = e.nativeEvent.pageY;
    dyRef.current = 0;
  }, []);

  const onTouchMove = useCallback((e: GestureResponderEvent) => {
    if (!touchingRef.current || !atTopRef.current) return;
    const dy = e.nativeEvent.pageY - startYRef.current;
    dyRef.current = Math.max(0, dy);
    setPullActive(dy > SHOW_DY);
  }, []);

  const pullScrollProps = {
    onScroll,
    onScrollBeginDrag,
    onScrollEndDrag,
    onMomentumScrollEnd: () => setPullActive(false),
    onTouchStart,
    onTouchMove,
    onTouchEnd: hide,
    onTouchCancel: hide,
    scrollEventThrottle: 16 as const,
    bounces: true,
    alwaysBounceVertical: true,
    overScrollMode: "always" as const,
  };

  return { pullActive, pullScrollProps };
}

/** Eski API uyumu — artık no-op; useBrandPull kullan. */
export function BrandRefreshControl(_props: {
  refreshing?: boolean;
  onRefresh?: () => void;
  enabled?: boolean;
  testID?: string;
}) {
  return null;
}

const styles = StyleSheet.create({
  overlay: {
    position: "absolute",
    top: 0,
    left: 0,
    right: 0,
    zIndex: 50,
    elevation: 50,
    alignItems: "center",
  },
  ringWrap: {
    width: 28,
    height: 28,
    alignItems: "center",
    justifyContent: "center",
  },
});
