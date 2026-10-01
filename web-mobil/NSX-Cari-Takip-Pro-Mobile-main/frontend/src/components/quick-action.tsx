import React from "react";
import { Pressable, Text, View, StyleSheet } from "react-native";
import Feather from "@react-native-vector-icons/feather";
import { useTheme, fonts, radius } from "../theme";

type IconName = React.ComponentProps<typeof Feather>["name"];

interface Props {
  label: string;
  icon: IconName;
  tone?: "primary" | "success" | "error" | "neutral";
  onPress: () => void;
  testID?: string;
}

/**
 * Soft-tint hızlı işlem kartı — dolu renk bloğu yerine soft zemin +
 * renkli ikon rozeti. Dashboard'da daha premium okunur.
 */
export function QuickAction({ label, icon, tone = "primary", onPress, testID }: Props) {
  const { colors } = useTheme();

  const accent =
    tone === "success"
      ? colors.success
      : tone === "error"
      ? colors.error
      : tone === "neutral"
      ? colors.muted
      : colors.brandPrimary;

  const softBg =
    tone === "success"
      ? colors.successSoft
      : tone === "error"
      ? colors.errorSoft
      : tone === "neutral"
      ? colors.surfaceTertiary
      : colors.brandTertiary;

  // Rozet zemini + ikon rengi — kontrast zorunlu (neutral'da siyah-üzerine-siyah olmasın)
  const coreBg =
    tone === "success"
      ? colors.success
      : tone === "error"
      ? colors.error
      : tone === "neutral"
      ? colors.surfaceInverse
      : colors.brandPrimary;

  const coreFg =
    tone === "success"
      ? colors.onSuccess
      : tone === "error"
      ? colors.onError
      : tone === "neutral"
      ? colors.onSurfaceInverse
      : colors.onBrandPrimary;

  return (
    <Pressable
      testID={testID}
      onPress={onPress}
      android_ripple={{ color: colors.surfaceTertiary }}
      style={({ pressed }) => [
        styles.card,
        {
          backgroundColor: colors.surfaceSecondary,
          borderColor: colors.border,
          opacity: pressed ? 0.92 : 1,
          transform: [{ scale: pressed ? 0.985 : 1 }],
        },
      ]}
    >
      <View style={[styles.iconWrap, { backgroundColor: softBg }]}>
        <View style={[styles.iconCore, { backgroundColor: coreBg }]}>
          <Feather name={icon} size={16} color={coreFg} />
        </View>
      </View>
      <Text style={[styles.label, { color: colors.onSurface }]} numberOfLines={1}>
        {label}
      </Text>
      <Text style={[styles.hint, { color: accent }]} numberOfLines={1}>
        {tone === "success"
          ? "Tahsilat"
          : tone === "error"
          ? "Borç"
          : tone === "neutral"
          ? "Ara"
          : "Yeni"}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  card: {
    flex: 1,
    borderRadius: radius.lg,
    padding: 10,
    minHeight: 80,
    borderWidth: 1,
    justifyContent: "space-between",
    gap: 6,
  },
  iconWrap: {
    width: 32,
    height: 32,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
  },
  iconCore: {
    width: 28,
    height: 28,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
  },
  label: {
    fontFamily: fonts.bodyBold,
    fontSize: 14,
    letterSpacing: -0.2,
  },
  hint: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.3,
    textTransform: "uppercase",
  },
});
