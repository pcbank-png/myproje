// Modern onay bottom sheet — Alert.alert yerine kullanılır.
// Örn: silme onayı, oturumu kapatma onayı gibi kritik aksiyonlar.
// - Görsel: büyük ikon, başlık, açıklama, iki buton (danger + secondary)
// - `tone="danger"` → primary buton kırmızı; `tone="primary"` → mavi
// - Yükleme durumu için `loading` prop (submit sırasında butonları
//   disable eder, spinner gösterir)

import React from "react";
import { ActivityIndicator, Pressable, StyleSheet, Text, View } from "react-native";
import Feather from "@react-native-vector-icons/feather";
import { BottomSheet } from "./bottom-sheet";
import { useTheme, radius, spacing, fonts } from "../theme";

type IconName = React.ComponentProps<typeof Feather>["name"];

interface Props {
  visible: boolean;
  onDismiss: () => void;
  onConfirm: () => void;
  title: string;
  description?: string;
  confirmLabel: string;
  cancelLabel?: string;
  tone?: "danger" | "primary";
  icon?: IconName;
  loading?: boolean;
  /** Aksiyon sırasında sunucudan gelen hata mesajı — inline gösterilir. */
  errorMessage?: string | null;
  testID?: string;
}

export function ConfirmSheet({
  visible,
  onDismiss,
  onConfirm,
  title,
  description,
  confirmLabel,
  cancelLabel = "Vazgeç",
  tone = "danger",
  icon,
  loading,
  errorMessage,
  testID,
}: Props) {
  const { colors } = useTheme();
  const confirmBg = tone === "danger" ? colors.error : colors.brandPrimary;
  const confirmFg = tone === "danger" ? colors.onError : colors.onBrandPrimary;
  const iconBg = tone === "danger" ? colors.errorSoft : colors.brandTertiary;
  const iconColor = tone === "danger" ? colors.error : colors.brandPrimary;
  const resolvedIcon: IconName = icon ?? (tone === "danger" ? "alert-triangle" : "info");

  return (
    <BottomSheet visible={visible} onDismiss={onDismiss} testID={testID} dismissible={!loading}>
      <View style={styles.body}>
        <View style={[styles.iconWrap, { backgroundColor: iconBg }]}>
          <Feather name={resolvedIcon} size={24} color={iconColor} />
        </View>
        <Text style={[styles.title, { color: colors.onSurface }]}>{title}</Text>
        {description ? (
          <Text style={[styles.desc, { color: colors.muted }]}>{description}</Text>
        ) : null}
        {errorMessage ? (
          <View
            style={[
              styles.errorBox,
              { backgroundColor: colors.errorSoft, borderColor: colors.error + "44" },
            ]}
          >
            <Feather name="alert-circle" size={14} color={colors.error} />
            <Text style={[styles.errorText, { color: colors.error }]} numberOfLines={3}>
              {errorMessage}
            </Text>
          </View>
        ) : null}
        <View style={styles.actions}>
          <Pressable
            testID={testID ? `${testID}-cancel` : undefined}
            onPress={onDismiss}
            disabled={loading}
            style={({ pressed }) => [
              styles.btn,
              styles.btnSecondary,
              {
                backgroundColor: colors.surfaceTertiary,
                opacity: loading ? 0.5 : pressed ? 0.85 : 1,
              },
            ]}
          >
            <Text style={[styles.btnLabel, { color: colors.onSurface }]}>{cancelLabel}</Text>
          </Pressable>
          <Pressable
            testID={testID ? `${testID}-confirm` : undefined}
            onPress={onConfirm}
            disabled={loading}
            style={({ pressed }) => [
              styles.btn,
              {
                backgroundColor: confirmBg,
                opacity: loading ? 0.7 : pressed ? 0.9 : 1,
              },
            ]}
          >
            {loading ? (
              <ActivityIndicator color={confirmFg} />
            ) : (
              <Text style={[styles.btnLabel, { color: confirmFg }]}>{confirmLabel}</Text>
            )}
          </Pressable>
        </View>
      </View>
    </BottomSheet>
  );
}

const styles = StyleSheet.create({
  body: { alignItems: "center", paddingTop: spacing.md, paddingBottom: spacing.sm },
  iconWrap: {
    width: 56,
    height: 56,
    borderRadius: 18,
    alignItems: "center",
    justifyContent: "center",
    marginBottom: spacing.md,
  },
  title: {
    fontFamily: fonts.display,
    fontSize: 20,
    letterSpacing: -0.4,
    textAlign: "center",
    marginBottom: 6,
  },
  desc: {
    fontFamily: fonts.body,
    fontSize: 14,
    lineHeight: 21,
    textAlign: "center",
    paddingHorizontal: spacing.sm,
    marginBottom: spacing.lg,
  },
  actions: {
    flexDirection: "row",
    gap: spacing.md,
    width: "100%",
    marginTop: 4,
  },
  btn: {
    flex: 1,
    height: 52,
    borderRadius: radius.md,
    alignItems: "center",
    justifyContent: "center",
  },
  btnSecondary: {},
  btnLabel: {
    fontFamily: fonts.bodyBold,
    fontSize: 15,
    letterSpacing: -0.2,
  },
  errorBox: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 8,
    padding: 12,
    borderRadius: 12,
    borderWidth: 1,
    width: "100%",
    marginBottom: spacing.md,
  },
  errorText: {
    flex: 1,
    fontFamily: fonts.bodyMedium,
    fontSize: 13,
    lineHeight: 18,
  },
});
