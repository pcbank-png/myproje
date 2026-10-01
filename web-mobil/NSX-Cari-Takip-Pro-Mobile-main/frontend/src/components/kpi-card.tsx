import React from "react";
import { View, Text, StyleSheet } from "react-native";
import Feather from "@react-native-vector-icons/feather";
import { useTheme, radius, fonts } from "../theme";
import { MoneyText } from "./money-text";

type IconName = React.ComponentProps<typeof Feather>["name"];

interface Props {
  label: string;
  value: number;
  tone?: "neutral" | "positive" | "negative";
  icon?: IconName;
  testID?: string;
}

/**
 * Premium KPI kart — soft tint + tabular tutar. Dark tema soft
 * yüzeyleri theme token'larından alır.
 */
export function KpiCard({ label, value, tone = "neutral", icon, testID }: Props) {
  const { colors, scheme } = useTheme();
  const isPositive = tone === "positive";
  const isNegative = tone === "negative";
  const accent = isPositive ? colors.success : isNegative ? colors.error : colors.brandPrimary;
  const softBg = isPositive
    ? colors.successSoft
    : isNegative
    ? colors.errorSoft
    : scheme === "dark"
    ? colors.brandTertiary
    : colors.brandTertiary;

  return (
    <View
      style={[
        styles.card,
        {
          backgroundColor: colors.surfaceSecondary,
          borderColor: colors.border,
        },
      ]}
      testID={testID}
    >
      <View style={styles.header}>
        <Text style={[styles.label, { color: colors.muted }]}>{label}</Text>
        {icon ? (
          <View style={[styles.iconBadge, { backgroundColor: softBg }]}>
            <Feather name={icon} size={12} color={accent} />
          </View>
        ) : null}
      </View>
      <MoneyText
        value={value}
        color={accent}
        size="lg"
        adjustsFontSizeToFit
      />
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    borderRadius: radius.lg,
    padding: 16,
    borderWidth: 1,
    flex: 1,
    minHeight: 96,
    justifyContent: "space-between",
    gap: 10,
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  label: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    textTransform: "uppercase",
    flex: 1,
  },
  iconBadge: {
    width: 22,
    height: 22,
    borderRadius: 7,
    alignItems: "center",
    justifyContent: "center",
  },
});
