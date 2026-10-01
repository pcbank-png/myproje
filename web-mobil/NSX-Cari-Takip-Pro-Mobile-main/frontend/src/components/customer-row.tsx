import React from "react";
import { Pressable, Text, View, StyleSheet } from "react-native";
import Animated, {
  useAnimatedStyle,
  useSharedValue,
  withTiming,
} from "react-native-reanimated";
import Feather from "@react-native-vector-icons/feather";
import { useTheme, fonts } from "../theme";
import { initials } from "../utils/format";
import { StatusBadge } from "./status-badge";
import { MoneyText } from "./money-text";
import type { Customer } from "../api/types";

interface Props {
  customer: Customer;
  onPress?: (id: string) => void;
  testID?: string;
}

const AnimatedPressable = Animated.createAnimatedComponent(Pressable);

export function CustomerRow({ customer, onPress, testID }: Props) {
  const { colors } = useTheme();
  const scale = useSharedValue(1);
  // PC firma perspektifi: >0 kalan alacak, <0 fazla tahsilat
  const hasReceivable = customer.netBalance > 0;
  const overCollected = customer.netBalance < 0;
  const color = hasReceivable
    ? colors.success
    : overCollected
    ? colors.error
    : colors.muted;
  const tone = hasReceivable ? "success" : overCollected ? "danger" : "muted";
  const label = hasReceivable ? "Alacak" : overCollected ? "Borç" : "Kapalı";
  const avatarBg = hasReceivable
    ? colors.successSoft
    : overCollected
    ? colors.errorSoft
    : colors.brandTertiary;
  const avatarFg = hasReceivable
    ? colors.success
    : overCollected
    ? colors.error
    : colors.onBrandTertiary;

  const animStyle = useAnimatedStyle(() => ({
    transform: [{ scale: scale.value }],
  }));

  return (
    <AnimatedPressable
      testID={testID}
      onPress={() => onPress?.(customer.id)}
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
      <View
        style={[
          styles.avatar,
          {
            backgroundColor: avatarBg,
            borderColor: hasReceivable
              ? colors.success + "33"
              : overCollected
              ? colors.error + "33"
              : colors.brandPrimary + "33",
          },
        ]}
      >
        <Text style={[styles.avatarText, { color: avatarFg }]}>
          {initials(customer.name)}
        </Text>
      </View>
      <View style={styles.body}>
        <Text style={[styles.name, { color: colors.onSurface }]} numberOfLines={1}>
          {customer.name}
        </Text>
        {customer.phone ? (
          <Text style={[styles.sub, { color: colors.muted }]} numberOfLines={1}>
            {customer.phone}
          </Text>
        ) : null}
      </View>
      <View style={styles.amountCol}>
        <MoneyText value={Math.abs(customer.netBalance)} color={color} size="md" />
        <StatusBadge label={label} tone={tone} />
      </View>
      <Feather name="chevron-right" size={16} color={colors.borderStrong} />
    </AnimatedPressable>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: "row",
    alignItems: "center",
    paddingVertical: 14,
    paddingHorizontal: 16,
    borderBottomWidth: StyleSheet.hairlineWidth,
    gap: 12,
    minHeight: 68,
  },
  avatar: {
    width: 40,
    height: 40,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
  },
  avatarText: { fontFamily: fonts.bodyBold, fontSize: 13 },
  body: { flex: 1, minWidth: 0, gap: 4 },
  name: { fontFamily: fonts.bodySemiBold, fontSize: 15, letterSpacing: -0.2 },
  sub: { fontFamily: fonts.body, fontSize: 12 },
  amountCol: { alignItems: "flex-end", gap: 6 },
});
