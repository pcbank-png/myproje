import React from "react";
import { StyleSheet, Text, View } from "react-native";
import { useTheme, fonts } from "@/src/theme";

export type BadgeTone = "success" | "danger" | "muted" | "info";

interface Props {
  label: string;
  tone: BadgeTone;
  testID?: string;
}

/**
 * Tekil, kurumsal rozet stili. Light + dark temada soft token'lar.
 */
export function StatusBadge({ label, tone, testID }: Props) {
  const { colors, scheme } = useTheme();
  const palette: Record<BadgeTone, { bg: string; fg: string; border: string }> = {
    success: {
      bg: colors.successSoft,
      fg: colors.success,
      border: scheme === "dark" ? "rgba(34,197,94,0.28)" : "#A7F3D0",
    },
    danger: {
      bg: colors.errorSoft,
      fg: colors.error,
      border: scheme === "dark" ? "rgba(248,113,113,0.28)" : "#FECACA",
    },
    muted: {
      bg: colors.surfaceTertiary,
      fg: colors.muted,
      border: colors.border,
    },
    info: {
      bg: colors.brandTertiary,
      fg: colors.onBrandTertiary,
      border: scheme === "dark" ? "rgba(59,130,246,0.28)" : colors.brandTertiary,
    },
  };
  const c = palette[tone];
  return (
    <View
      testID={testID}
      style={[styles.badge, { backgroundColor: c.bg, borderColor: c.border }]}
    >
      <Text style={[styles.label, { color: c.fg }]}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  badge: {
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: 999,
    borderWidth: 1,
    alignSelf: "flex-start",
  },
  label: {
    fontFamily: fonts.bodyBold,
    fontSize: 11,
    letterSpacing: 0.2,
  },
});
