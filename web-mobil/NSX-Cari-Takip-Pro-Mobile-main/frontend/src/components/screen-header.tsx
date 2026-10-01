import React from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import Feather from "@react-native-vector-icons/feather";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useTheme, fonts } from "../theme";

interface Props {
  title: string;
  subtitle?: string;
  onBack?: () => void;
  right?: React.ReactNode;
  variant?: "surface" | "inverse";
}

export function ScreenHeader({ title, subtitle, onBack, right, variant = "surface" }: Props) {
  const insets = useSafeAreaInsets();
  const router = useRouter();
  const { colors } = useTheme();
  const inverse = variant === "inverse";
  const bg = inverse ? colors.surfaceInverse : colors.surfaceSecondary;
  const fg = inverse ? colors.onSurfaceInverse : colors.onSurface;
  const sub = inverse ? "rgba(255,255,255,0.7)" : colors.muted;
  return (
    <View
      style={[
        styles.wrap,
        {
          paddingTop: insets.top + 8,
          backgroundColor: bg,
          borderBottomColor: inverse ? "transparent" : colors.border,
        },
      ]}
    >
      <View style={styles.row}>
        {onBack !== undefined || router.canGoBack() ? (
          <Pressable
            onPress={onBack ?? (() => router.back())}
            hitSlop={12}
            style={styles.iconBtn}
            testID="header-back"
          >
            <Feather name="chevron-left" size={24} color={fg} />
          </Pressable>
        ) : (
          <View style={styles.iconBtn} />
        )}
        <View style={styles.titleWrap}>
          <Text style={[styles.title, { color: fg }]} numberOfLines={1}>
            {title}
          </Text>
          {subtitle ? (
            <Text style={[styles.subtitle, { color: sub }]} numberOfLines={1}>
              {subtitle}
            </Text>
          ) : null}
        </View>
        <View style={styles.iconBtn}>{right}</View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    paddingBottom: 12,
    paddingHorizontal: 8,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  row: { flexDirection: "row", alignItems: "center", minHeight: 40 },
  iconBtn: { width: 40, height: 40, alignItems: "center", justifyContent: "center" },
  titleWrap: { flex: 1, alignItems: "center" },
  title: { fontFamily: fonts.displayMedium, fontSize: 17, letterSpacing: -0.3 },
  subtitle: { fontFamily: fonts.body, fontSize: 12, marginTop: 2 },
});
