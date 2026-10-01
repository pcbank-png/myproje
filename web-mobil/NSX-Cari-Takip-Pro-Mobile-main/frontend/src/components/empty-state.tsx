import React from "react";
import { View, Text, Pressable, StyleSheet } from "react-native";
import Feather from "@react-native-vector-icons/feather";
import { useTheme, radius, fonts } from "../theme";

type IconName = React.ComponentProps<typeof Feather>["name"];

interface Props {
  icon?: IconName;
  title: string;
  description?: string;
  actionLabel?: string;
  onAction?: () => void;
  /** Varsayılan: brand. Hata panelleri için "danger". */
  tone?: "brand" | "danger";
  /** CTA ikonu — retry için "refresh-cw", empty için "plus". */
  actionIcon?: IconName;
  secondaryActionLabel?: string;
  onSecondaryAction?: () => void;
  testID?: string;
}

export function EmptyState({
  icon = "inbox",
  title,
  description,
  actionLabel,
  onAction,
  tone = "brand",
  actionIcon = "plus",
  secondaryActionLabel,
  onSecondaryAction,
  testID,
}: Props) {
  const { colors } = useTheme();
  const accent = tone === "danger" ? colors.error : colors.brandPrimary;
  const soft = tone === "danger" ? colors.errorSoft : colors.brandTertiary;
  const onSoft = tone === "danger" ? colors.error : colors.onBrandTertiary;

  return (
    <View style={styles.wrap} testID={testID}>
      <View style={styles.iconOuter}>
        <View style={[styles.halo, { backgroundColor: accent, opacity: 0.12 }]} />
        <View style={[styles.iconRing, { borderColor: accent + "33" }]} />
        <View
          style={[
            styles.iconInner,
            {
              backgroundColor: soft,
              borderColor: accent + "28",
            },
          ]}
        >
          <Feather name={icon} size={26} color={onSoft} />
        </View>
      </View>
      <Text style={[styles.title, { color: colors.onSurface }]}>{title}</Text>
      {description ? (
        <Text style={[styles.desc, { color: colors.muted }]}>{description}</Text>
      ) : null}
      {actionLabel && onAction ? (
        <Pressable
          onPress={onAction}
          testID={`${testID ?? "empty"}-cta`}
          style={({ pressed }) => [
            styles.cta,
            {
              backgroundColor: tone === "danger" ? colors.brandPrimary : accent,
              opacity: pressed ? 0.9 : 1,
              transform: [{ scale: pressed ? 0.98 : 1 }],
            },
          ]}
        >
          <Feather name={actionIcon} size={16} color={colors.onBrandPrimary} />
          <Text style={[styles.ctaLabel, { color: colors.onBrandPrimary }]}>{actionLabel}</Text>
        </Pressable>
      ) : null}
      {secondaryActionLabel && onSecondaryAction ? (
        <Pressable
          onPress={onSecondaryAction}
          testID={`${testID ?? "empty"}-cta-secondary`}
          style={({ pressed }) => [
            styles.ctaGhost,
            {
              borderColor: colors.borderStrong,
              opacity: pressed ? 0.85 : 1,
            },
          ]}
        >
          <Text style={[styles.ctaGhostLabel, { color: colors.onSurface }]}>
            {secondaryActionLabel}
          </Text>
        </Pressable>
      ) : null}
    </View>
  );
}

/** Liste/query hata paneli — EmptyState danger tonu. */
export function ErrorState({
  title,
  description,
  onRetry,
  testID = "error-state",
}: {
  title: string;
  description?: string;
  onRetry?: () => void;
  testID?: string;
}) {
  return (
    <EmptyState
      icon="alert-circle"
      tone="danger"
      title={title}
      description={description}
      actionLabel={onRetry ? "Tekrar Dene" : undefined}
      actionIcon="refresh-cw"
      onAction={onRetry}
      testID={testID}
    />
  );
}

const styles = StyleSheet.create({
  wrap: {
    alignItems: "center",
    justifyContent: "center",
    paddingVertical: 56,
    paddingHorizontal: 32,
    gap: 12,
  },
  iconOuter: {
    width: 96,
    height: 96,
    alignItems: "center",
    justifyContent: "center",
    marginBottom: 6,
  },
  halo: {
    position: "absolute",
    width: 96,
    height: 96,
    borderRadius: 48,
  },
  iconRing: {
    ...StyleSheet.absoluteFill,
    borderRadius: 48,
    borderWidth: 1.5,
    borderStyle: "dashed",
    margin: 4,
  },
  iconInner: {
    width: 64,
    height: 64,
    borderRadius: 32,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
  },
  title: {
    fontFamily: fonts.display,
    fontSize: 18,
    letterSpacing: -0.4,
    textAlign: "center",
  },
  desc: {
    fontFamily: fonts.body,
    fontSize: 14,
    lineHeight: 21,
    textAlign: "center",
    maxWidth: 280,
  },
  cta: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 20,
    height: 44,
    borderRadius: radius.pill,
    marginTop: 10,
  },
  ctaLabel: {
    fontFamily: fonts.bodyBold,
    fontSize: 14,
    letterSpacing: -0.2,
  },
  ctaGhost: {
    marginTop: 2,
    height: 40,
    paddingHorizontal: 16,
    borderRadius: radius.pill,
    borderWidth: 1,
    alignItems: "center",
    justifyContent: "center",
  },
  ctaGhostLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 13,
  },
});
