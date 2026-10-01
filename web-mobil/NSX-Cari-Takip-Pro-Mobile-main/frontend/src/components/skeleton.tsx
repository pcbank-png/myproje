import React, { useEffect } from "react";
import { View, StyleSheet, type ViewStyle, type StyleProp } from "react-native";
import Animated, {
  Easing,
  useAnimatedStyle,
  useSharedValue,
  withRepeat,
  withTiming,
} from "react-native-reanimated";
import { useTheme, radius } from "@/src/theme";

interface BoneProps {
  width: number | `${number}%`;
  height: number;
  radius?: number;
  style?: StyleProp<ViewStyle>;
}

function Bone({ width, height, radius: r = 8, style }: BoneProps) {
  const { colors, scheme } = useTheme();
  const pulse = useSharedValue(0.45);

  useEffect(() => {
    pulse.value = withRepeat(
      withTiming(1, { duration: 900, easing: Easing.inOut(Easing.quad) }),
      -1,
      true,
    );
  }, [pulse]);

  const anim = useAnimatedStyle(() => ({
    opacity: 0.35 + pulse.value * 0.45,
  }));

  return (
    <Animated.View
      style={[
        {
          width,
          height,
          borderRadius: r,
          backgroundColor: scheme === "dark" ? colors.surfaceTertiary : colors.border,
        },
        anim,
        style,
      ]}
    />
  );
}

/** Dashboard hero + KPI + recent list shimmer. */
export function DashboardSkeleton() {
  const { colors } = useTheme();
  return (
    <View style={styles.pad} testID="dashboard-skeleton">
      <View style={styles.headerRow}>
        <View style={{ flex: 1, gap: 8 }}>
          <Bone width={72} height={10} />
          <Bone width="70%" height={22} radius={10} />
        </View>
        <Bone width={40} height={40} radius={20} />
      </View>

      <View
        style={[
          styles.hero,
          { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
        ]}
      >
        <Bone width={110} height={10} />
        <Bone width="62%" height={34} radius={12} style={{ marginTop: 14 }} />
        <Bone width={120} height={22} radius={999} style={{ marginTop: 12 }} />
        <View style={styles.analysisRow}>
          <Bone width="28%" height={56} radius={12} />
          <Bone width="28%" height={56} radius={12} />
          <Bone width="28%" height={56} radius={12} />
        </View>
        <View style={[styles.heroFooter, { borderTopColor: colors.divider }]}>
          <Bone width={90} height={12} />
          <Bone width={110} height={12} />
        </View>
      </View>

      <Bone width={120} height={16} radius={8} style={{ marginTop: 28, marginBottom: 12 }} />
      <View style={styles.actionRow}>
        <Bone width="48%" height={108} radius={radius.lg} />
        <Bone width="48%" height={108} radius={radius.lg} />
      </View>
      <View style={[styles.actionRow, { marginTop: 12 }]}>
        <Bone width="48%" height={108} radius={radius.lg} />
        <Bone width="48%" height={108} radius={radius.lg} />
      </View>

      <Bone width={130} height={16} radius={8} style={{ marginTop: 28, marginBottom: 12 }} />
      <View
        style={[
          styles.list,
          { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
        ]}
      >
        {[0, 1, 2].map((i) => (
          <View key={i} style={[styles.listRow, { borderBottomColor: colors.divider }]}>
            <Bone width={40} height={40} radius={14} />
            <View style={{ flex: 1, gap: 8 }}>
              <Bone width="55%" height={12} />
              <Bone width="35%" height={10} />
            </View>
            <Bone width={72} height={14} />
          </View>
        ))}
      </View>
    </View>
  );
}

/** Liste satırı shimmer — müşteri / hareket FlatList boş yükleme. */
export function ListSkeleton({ rows = 6 }: { rows?: number }) {
  const { colors } = useTheme();
  return (
    <View testID="list-skeleton">
      {Array.from({ length: rows }).map((_, i) => (
        <View
          key={i}
          style={[
            styles.listRow,
            {
              backgroundColor: colors.surfaceSecondary,
              borderBottomColor: colors.divider,
            },
          ]}
        >
          <Bone width={40} height={40} radius={14} />
          <View style={{ flex: 1, gap: 8 }}>
            <Bone width="58%" height={12} />
            <Bone width="38%" height={10} />
          </View>
          <Bone width={68} height={14} />
        </View>
      ))}
    </View>
  );
}

/** Müşteri detay üst kart + hareket listesi shimmer. */
export function CustomerDetailSkeleton() {
  const { colors } = useTheme();
  return (
    <View style={styles.detailPad} testID="customer-detail-skeleton">
      <View style={styles.detailIdentity}>
        <Bone width={68} height={68} radius={22} />
        <Bone width={160} height={20} radius={8} style={{ marginTop: 12 }} />
        <Bone width={110} height={12} radius={6} style={{ marginTop: 8 }} />
      </View>
      <View
        style={[
          styles.detailCard,
          { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
        ]}
      >
        <Bone width={80} height={10} />
        <Bone width="55%" height={34} radius={12} style={{ marginTop: 14 }} />
        <Bone width={140} height={22} radius={999} style={{ marginTop: 12 }} />
        <View style={[styles.detailFooter, { borderTopColor: colors.divider }]}>
          <View style={{ flex: 1, gap: 8 }}>
            <Bone width={70} height={10} />
            <Bone width={90} height={14} />
          </View>
          <View style={{ flex: 1, gap: 8 }}>
            <Bone width={90} height={10} />
            <Bone width={90} height={14} />
          </View>
        </View>
      </View>
      <View style={styles.detailActions}>
        <Bone width="48%" height={54} radius={12} />
        <Bone width="48%" height={54} radius={12} />
      </View>
      <Bone width={110} height={16} radius={8} style={{ marginTop: 28, marginBottom: 12 }} />
      <ListSkeleton rows={4} />
    </View>
  );
}

const styles = StyleSheet.create({
  pad: { paddingHorizontal: 16 },
  headerRow: {
    flexDirection: "row",
    alignItems: "flex-end",
    marginBottom: 16,
    gap: 12,
  },
  hero: {
    borderRadius: radius.lg,
    borderWidth: 1,
    padding: 24,
    minHeight: 160,
  },
  heroFooter: {
    flexDirection: "row",
    gap: 16,
    marginTop: 18,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingTop: 14,
  },
  analysisRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    marginTop: 16,
  },
  actionRow: { flexDirection: "row", justifyContent: "space-between" },
  list: {
    borderRadius: radius.lg,
    borderWidth: 1,
    overflow: "hidden",
  },
  listRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  detailPad: { paddingTop: 8 },
  detailIdentity: { alignItems: "center", paddingVertical: 20 },
  detailCard: {
    marginHorizontal: 16,
    borderRadius: radius.lg,
    borderWidth: 1,
    padding: 24,
  },
  detailFooter: {
    flexDirection: "row",
    gap: 16,
    marginTop: 18,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingTop: 16,
  },
  detailActions: {
    flexDirection: "row",
    justifyContent: "space-between",
    paddingHorizontal: 16,
    marginTop: 16,
  },
});
