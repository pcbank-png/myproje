// Görünüm — Sistem / Açık / Koyu tema seçici.
import React from "react";
import { View, Text, StyleSheet, Pressable, ScrollView } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { ScreenHeader } from "@/src/components/screen-header";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import { useThemePreference, type ThemePreference } from "@/src/theme-preference";
import { selectionHaptic } from "@/src/utils/haptics";

type IconName = React.ComponentProps<typeof Feather>["name"];

interface Option {
  key: ThemePreference;
  label: string;
  hint: string;
  icon: IconName;
  swatch: [string, string];
}

const OPTIONS: Option[] = [
  {
    key: "system",
    label: "Sistem",
    hint: "Cihaz temasına uy",
    icon: "smartphone",
    swatch: ["#F8FAFC", "#0F172A"],
  },
  {
    key: "light",
    label: "Açık",
    hint: "Her zaman açık tema",
    icon: "sun",
    swatch: ["#FFFFFF", "#DBEAFE"],
  },
  {
    key: "dark",
    label: "Koyu",
    hint: "Her zaman koyu tema",
    icon: "moon",
    swatch: ["#0F172A", "#1E3A8A"],
  },
];

export default function AppearanceScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const { preference, setPreference } = useThemePreference();

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="appearance-screen">
      <ScreenHeader title="Görünüm" />
      <ScrollView
        contentContainerStyle={{
          paddingHorizontal: spacing.lg,
          paddingBottom: insets.bottom + 32,
          paddingTop: spacing.md,
        }}
      >
        <Text style={[styles.sectionTitle, { color: colors.muted }]}>TEMA</Text>
        <View style={styles.options}>
          {OPTIONS.map((opt) => {
            const active = opt.key === preference;
            return (
              <Pressable
                key={opt.key}
                testID={`appearance-${opt.key}`}
                onPress={() => {
                  if (opt.key !== preference) {
                    selectionHaptic();
                    setPreference(opt.key);
                  }
                }}
                style={({ pressed }) => [
                  styles.option,
                  {
                    backgroundColor: active ? colors.brandTertiary : colors.surfaceSecondary,
                    borderColor: active ? colors.brandPrimary + "55" : colors.border,
                    opacity: pressed ? 0.9 : 1,
                  },
                ]}
              >
                <View style={styles.swatchRow}>
                  <View style={[styles.swatch, { backgroundColor: opt.swatch[0], borderColor: colors.border }]} />
                  <View style={[styles.swatch, { backgroundColor: opt.swatch[1], borderColor: colors.border, marginLeft: -8 }]} />
                </View>
                <View
                  style={[
                    styles.iconWrap,
                    {
                      backgroundColor: active ? colors.brandPrimary : colors.surfaceTertiary,
                    },
                  ]}
                >
                  <Feather
                    name={opt.icon}
                    size={16}
                    color={active ? colors.onBrandPrimary : colors.onSurface}
                  />
                </View>
                <View style={{ flex: 1 }}>
                  <Text
                    style={[
                      styles.label,
                      { color: active ? colors.onBrandTertiary : colors.onSurface },
                    ]}
                  >
                    {opt.label}
                  </Text>
                  <Text
                    style={[
                      styles.hint,
                      { color: active ? colors.onBrandTertiary : colors.muted, opacity: active ? 0.8 : 1 },
                    ]}
                  >
                    {opt.hint}
                  </Text>
                </View>
                {active ? (
                  <View style={[styles.check, { backgroundColor: colors.brandPrimary }]}>
                    <Feather name="check" size={12} color={colors.onBrandPrimary} />
                  </View>
                ) : (
                  <View style={[styles.checkOutline, { borderColor: colors.borderStrong }]} />
                )}
              </Pressable>
            );
          })}
        </View>

        <View
          style={[
            styles.note,
            { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
          ]}
        >
          <Feather name="info" size={14} color={colors.muted} />
          <Text style={[styles.noteText, { color: colors.muted }]}>
            Seçtiğiniz tema tüm ekranlarda anında uygulanır ve uygulamayı yeniden başlattığınızda hatırlanır.
          </Text>
        </View>
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  sectionTitle: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    marginTop: spacing.md,
    marginBottom: spacing.sm,
    marginLeft: 4,
  },
  options: { gap: 10 },
  option: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 14,
    borderRadius: radius.md,
    borderWidth: 1,
  },
  swatchRow: { flexDirection: "row", alignItems: "center" },
  swatch: {
    width: 18,
    height: 18,
    borderRadius: 6,
    borderWidth: 1,
  },
  iconWrap: {
    width: 36,
    height: 36,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  label: { fontFamily: fonts.bodySemiBold, fontSize: 15, letterSpacing: -0.2 },
  hint: { fontFamily: fonts.body, fontSize: 12, marginTop: 2 },
  check: {
    width: 22,
    height: 22,
    borderRadius: 11,
    alignItems: "center",
    justifyContent: "center",
  },
  checkOutline: {
    width: 22,
    height: 22,
    borderRadius: 11,
    borderWidth: 1.5,
  },
  note: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 8,
    padding: 12,
    borderRadius: radius.md,
    borderWidth: 1,
    marginTop: spacing.lg,
  },
  noteText: { flex: 1, fontFamily: fonts.body, fontSize: 12, lineHeight: 18 },
});
