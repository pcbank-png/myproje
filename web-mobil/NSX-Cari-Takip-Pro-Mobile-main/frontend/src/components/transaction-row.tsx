import React from "react";
import { Pressable, Text, View, StyleSheet } from "react-native";
import Animated, {
  useAnimatedStyle,
  useSharedValue,
  withTiming,
} from "react-native-reanimated";
import Feather from "@react-native-vector-icons/feather";
import { useTheme, fonts, radius } from "../theme";
import { formatRelativeDate } from "../utils/format";
import { MoneyText } from "./money-text";
import type { Transaction } from "../api/types";

interface Props {
  tx: Transaction;
  showCustomer?: boolean;
  onPress?: (tx: Transaction) => void;
  testID?: string;
}

const AnimatedPressable = Animated.createAnimatedComponent(Pressable);

export function TransactionRow({ tx, showCustomer, onPress, testID }: Props) {
  const { colors, scheme } = useTheme();
  const scale = useSharedValue(1);
  const isDebt = tx.kind === "debt";
  const accent = isDebt ? colors.error : colors.success;
  const softBg = isDebt ? colors.errorSoft : colors.successSoft;
  const chipBg =
    scheme === "dark"
      ? softBg
      : isDebt
      ? "#FEE2E2"
      : "#D1FAE5";
  const iconName = isDebt ? "arrow-up-right" : "arrow-down-left";
  const kindLabel = isDebt ? "Borç" : "Tahsilat";
  const line = showCustomer ? tx.customerName : tx.description ?? kindLabel;

  const animStyle = useAnimatedStyle(() => ({
    transform: [{ scale: scale.value }],
  }));

  return (
    <AnimatedPressable
      testID={testID}
      onPress={() => onPress?.(tx)}
      onPressIn={() => {
        scale.set(withTiming(0.985, { duration: 90 }));
      }}
      onPressOut={() => {
        scale.set(withTiming(1, { duration: 140 }));
      }}
      android_ripple={{ color: colors.surfaceTertiary }}
      style={[
        styles.row,
        {
          backgroundColor: colors.surfaceSecondary,
          borderBottomColor: colors.divider,
        },
        animStyle,
      ]}
    >
      <View style={[styles.iconWrap, { backgroundColor: softBg }]}>
        <Feather name={iconName as "arrow-up-right"} size={17} color={accent} />
      </View>
      <View style={styles.body}>
        <Text
          style={[styles.title, { color: colors.onSurface }]}
          numberOfLines={1}
        >
          {line}
        </Text>
        <View style={styles.metaRow}>
          <View style={[styles.chip, { backgroundColor: chipBg }]}>
            <Text style={[styles.chipLabel, { color: accent }]}>{kindLabel}</Text>
          </View>
          <Text style={[styles.sub, { color: colors.muted }]} numberOfLines={1}>
            {showCustomer && tx.description ? `${tx.description} · ` : ""}
            {formatRelativeDate(tx.date)}
          </Text>
        </View>
      </View>
      <MoneyText
        value={tx.amount}
        color={accent}
        size="md"
        signed={isDebt ? "debt" : "collection"}
      />
    </AnimatedPressable>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: "row",
    alignItems: "center",
    paddingVertical: 13,
    paddingHorizontal: 16,
    borderBottomWidth: StyleSheet.hairlineWidth,
    gap: 12,
  },
  iconWrap: {
    width: 40,
    height: 40,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
  },
  body: { flex: 1, minWidth: 0, gap: 5 },
  title: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 14,
    letterSpacing: -0.2,
  },
  metaRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  chip: {
    paddingHorizontal: 8,
    paddingVertical: 2,
    borderRadius: radius.pill,
  },
  chipLabel: {
    fontFamily: fonts.bodyBold,
    fontSize: 10,
    letterSpacing: 0.3,
    textTransform: "uppercase",
  },
  sub: {
    fontFamily: fonts.body,
    fontSize: 12,
    flexShrink: 1,
  },
});
