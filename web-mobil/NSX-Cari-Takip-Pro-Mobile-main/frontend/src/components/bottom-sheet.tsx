// Native-hisse veren, taşınabilir bottom sheet.
import React, { useEffect } from "react";
import {
  Modal,
  Pressable,
  StyleSheet,
  View,
  useWindowDimensions,
} from "react-native";
import Animated, {
  Easing,
  useAnimatedStyle,
  useSharedValue,
  withTiming,
  runOnJS,
} from "react-native-reanimated";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useTheme } from "../theme";

interface Props {
  visible: boolean;
  onDismiss: () => void;
  children: React.ReactNode;
  /** Backdrop'a dokununca kapansın mı? Varsayılan: true */
  dismissible?: boolean;
  testID?: string;
}

const SHEET_HEIGHT_ESTIMATE = 320;

export function BottomSheet({
  visible,
  onDismiss,
  children,
  dismissible = true,
  testID,
}: Props) {
  const { colors, scheme } = useTheme();
  const insets = useSafeAreaInsets();
  const { height } = useWindowDimensions();

  const translateY = useSharedValue(SHEET_HEIGHT_ESTIMATE);
  const backdrop = useSharedValue(0);
  const [mounted, setMounted] = React.useState(visible);

  useEffect(() => {
    if (visible) {
      const mount = setTimeout(() => setMounted(true), 0);
      translateY.value = withTiming(0, {
        duration: 280,
        easing: Easing.out(Easing.cubic),
      });
      backdrop.value = withTiming(1, { duration: 220 });
      return () => clearTimeout(mount);
    } else if (mounted) {
      backdrop.value = withTiming(0, { duration: 180 });
      translateY.value = withTiming(
        SHEET_HEIGHT_ESTIMATE,
        { duration: 220, easing: Easing.in(Easing.cubic) },
        (finished) => {
          if (finished) runOnJS(setMounted)(false);
        },
      );
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);

  const sheetStyle = useAnimatedStyle(() => ({
    transform: [{ translateY: translateY.value }],
  }));
  const backdropStyle = useAnimatedStyle(() => ({
    opacity: backdrop.value * (scheme === "dark" ? 0.72 : 0.48),
  }));

  const handleBackdrop = () => {
    if (dismissible) onDismiss();
  };

  return (
    <Modal
      transparent
      statusBarTranslucent
      visible={mounted}
      animationType="none"
      onRequestClose={onDismiss}
    >
      <View style={styles.root} testID={testID}>
        <Animated.View
          style={[styles.backdrop, backdropStyle]}
          pointerEvents={visible ? "auto" : "none"}
        >
          <Pressable
            style={StyleSheet.absoluteFill}
            onPress={handleBackdrop}
            testID={testID ? `${testID}-backdrop` : undefined}
          />
        </Animated.View>
        <Animated.View
          style={[
            styles.sheet,
            {
              backgroundColor: colors.surfaceSecondary,
              borderColor: colors.border,
              paddingBottom: insets.bottom + 18,
              maxHeight: height * 0.9,
              shadowOpacity: scheme === "dark" ? 0.45 : 0.18,
            },
            sheetStyle,
          ]}
        >
          <View style={styles.handleWrap} pointerEvents="none">
            <View style={[styles.handle, { backgroundColor: colors.borderStrong }]} />
          </View>
          {children}
        </Animated.View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, justifyContent: "flex-end" },
  backdrop: {
    ...StyleSheet.absoluteFill,
    backgroundColor: "#020617",
  },
  sheet: {
    borderTopLeftRadius: 28,
    borderTopRightRadius: 28,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: 20,
    paddingTop: 6,
    shadowColor: "#0F172A",
    shadowRadius: 28,
    shadowOffset: { width: 0, height: -8 },
    elevation: 28,
  },
  handleWrap: { alignItems: "center", paddingVertical: 10 },
  handle: { width: 44, height: 5, borderRadius: 999 },
});
