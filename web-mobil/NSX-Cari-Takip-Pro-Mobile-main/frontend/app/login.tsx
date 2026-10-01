import React, { useEffect, useState } from "react";
import {
  View,
  Text,
  TextInput,
  Pressable,
  StyleSheet,
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { StatusBar } from "expo-status-bar";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useAuth } from "@/src/auth/auth-context";
import { useTheme, fonts, spacing, radius } from "@/src/theme";
import { isMockMode } from "@/src/api";
import { BrandAppIcon } from "@/src/components/brand-app-icon";
import { selectionHaptic, successHaptic, errorHaptic } from "@/src/utils/haptics";

export default function LoginScreen() {
  const { colors, scheme } = useTheme();
  const insets = useSafeAreaInsets();
  const router = useRouter();
  const { pair, session, ready } = useAuth();
  const params = useLocalSearchParams<{ reauth?: string }>();
  const [code, setCode] = useState(isMockMode ? "NSX-DEMO" : "");
  const [loading, setLoading] = useState(false);
  const [focused, setFocused] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (ready && session && !params.reauth) {
      router.replace("/(tabs)");
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready, session, params.reauth]);

  const onSubmit = async () => {
    if (loading) return;
    setError(null);
    setLoading(true);
    selectionHaptic();
    try {
      await pair(code.trim());
      successHaptic();
      router.dismissAll();
      router.replace("/(tabs)");
    } catch (e) {
      errorHaptic();
      setError(e instanceof Error ? e.message : "Bağlantı sırasında hata oluştu.");
    } finally {
      setLoading(false);
    }
  };

  const glow =
    scheme === "light"
      ? (["rgba(37,99,235,0.16)", "rgba(37,99,235,0.03)", "transparent"] as const)
      : (["rgba(59,130,246,0.22)", "rgba(59,130,246,0.05)", "transparent"] as const);

  const canSubmit = !loading && code.trim().length >= 4;

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="login-screen">
      <StatusBar style={scheme === "dark" ? "light" : "dark"} />
      <KeyboardAvoidingView
        style={{ flex: 1 }}
        behavior={Platform.OS === "ios" ? "padding" : undefined}
      >
        <ScrollView
          contentContainerStyle={{
            flexGrow: 1,
            paddingTop: insets.top + 24,
            paddingBottom: insets.bottom + 28,
            paddingHorizontal: spacing.xl,
          }}
          keyboardShouldPersistTaps="handled"
          showsVerticalScrollIndicator={false}
        >
          <View style={styles.brandWrap}>
            <View
              style={[
                styles.iconHalo,
                { backgroundColor: colors.brandTertiary, borderColor: colors.brandPrimary + "22" },
              ]}
            >
              <BrandAppIcon
                size={96}
                variant={scheme === "light" ? "main" : "dark"}
                surface={colors.brandTertiary}
                testID="login-brand"
              />
            </View>
            <Text style={[styles.brandTitle, { color: colors.onSurface }]}>NSX Cari Takip Pro</Text>
            <Text style={[styles.subtitle, { color: colors.muted }]}>
              Bulut hesabınıza güvenli mobil giriş
            </Text>
          </View>

          {params.reauth ? (
            <View
              style={[
                styles.banner,
                { backgroundColor: colors.warningSoft, borderColor: colors.warning + "44" },
              ]}
              testID="login-reauth-banner"
            >
              <View style={[styles.bannerIcon, { backgroundColor: colors.warning }]}>
                <Feather name="shield" size={14} color={colors.onWarning} />
              </View>
              <Text style={[styles.bannerText, { color: colors.onSurface }]}>
                Oturum yenilenmeli. QR veya kod ile tekrar bağlanın.
              </Text>
            </View>
          ) : null}

          <View style={styles.qrShell}>
            <LinearGradient
              colors={[...glow]}
              start={{ x: 0.5, y: 0 }}
              end={{ x: 0.5, y: 1 }}
              style={styles.qrGlow}
              pointerEvents="none"
            />
            <Pressable
              style={({ pressed }) => [
                styles.qrCard,
                {
                  backgroundColor: colors.surfaceSecondary,
                  borderColor: colors.brandPrimary + "28",
                  opacity: pressed ? 0.96 : 1,
                  transform: [{ scale: pressed ? 0.985 : 1 }],
                },
              ]}
              onPress={() => {
                selectionHaptic();
                router.push("/qr-scanner");
              }}
              testID="login-open-scanner"
            >
              <View
                style={[
                  styles.qrFrame,
                  {
                    borderColor: colors.brandPrimary + "55",
                    backgroundColor: colors.surfaceTertiary,
                  },
                ]}
              >
                <Feather name="maximize" size={56} color={colors.brandPrimary} style={{ opacity: 0.45 }} />
                <View style={[styles.qrDot, { backgroundColor: colors.brandPrimary }]} />
              </View>
              <View style={[styles.scanCta, { backgroundColor: colors.brandPrimary }]}>
                <Feather name="camera" size={16} color={colors.onBrandPrimary} />
                <Text style={[styles.scanCtaLabel, { color: colors.onBrandPrimary }]}>
                  QR Kodu Tara
                </Text>
              </View>
              <Text style={[styles.qrHint, { color: colors.muted }]}>
                Masaüstünde{" "}
                <Text style={{ color: colors.onSurface, fontFamily: fonts.bodySemiBold }}>
                  QR ile Bağla
                </Text>{" "}
                menüsünden kod oluşturun.
              </Text>
            </Pressable>
          </View>

          <View style={styles.dividerRow}>
            <View style={[styles.dividerLine, { backgroundColor: colors.divider }]} />
            <Text style={[styles.dividerLabel, { color: colors.muted }]}>veya kod girin</Text>
            <View style={[styles.dividerLine, { backgroundColor: colors.divider }]} />
          </View>

          <View style={styles.inputSection}>
            <Text style={[styles.inputLabel, { color: colors.muted }]}>Eşleştirme Kodu</Text>
            <View
              style={[
                styles.inputWrap,
                {
                  backgroundColor: colors.surfaceSecondary,
                  borderColor: focused
                    ? colors.brandPrimary
                    : error
                    ? colors.error + "66"
                    : colors.border,
                },
              ]}
            >
              <View
                style={[
                  styles.inputIcon,
                  { backgroundColor: focused ? colors.brandTertiary : colors.surfaceTertiary },
                ]}
              >
                <Feather
                  name="key"
                  size={16}
                  color={focused ? colors.brandPrimary : colors.muted}
                />
              </View>
              <TextInput
                testID="pairing-code-input"
                style={[styles.input, { color: colors.onSurface }]}
                value={code}
                onChangeText={(t) => {
                  setCode(t);
                  if (error) setError(null);
                }}
                onFocus={() => setFocused(true)}
                onBlur={() => setFocused(false)}
                placeholder="Örn: NSX-1234"
                placeholderTextColor={colors.muted}
                autoCapitalize="characters"
                autoCorrect={false}
                editable={!loading}
                returnKeyType="go"
                onSubmitEditing={() => {
                  if (canSubmit) void onSubmit();
                }}
              />
            </View>
            {error ? (
              <View
                style={[
                  styles.errorChip,
                  { backgroundColor: colors.errorSoft, borderColor: colors.error + "33" },
                ]}
                testID="pairing-error"
              >
                <Feather name="alert-circle" size={14} color={colors.error} />
                <Text style={[styles.errorText, { color: colors.error }]}>{error}</Text>
              </View>
            ) : null}
          </View>

          <Pressable
            testID="pairing-submit"
            onPress={onSubmit}
            disabled={!canSubmit}
            style={({ pressed }) => [
              styles.cta,
              {
                backgroundColor: colors.brandPrimary,
                opacity: !canSubmit ? 0.45 : pressed ? 0.92 : 1,
                transform: [{ scale: pressed && canSubmit ? 0.98 : 1 }],
              },
            ]}
          >
            {loading ? (
              <ActivityIndicator color={colors.onBrandPrimary} />
            ) : (
              <>
                <Text style={[styles.ctaLabel, { color: colors.onBrandPrimary }]}>
                  Güvenli Giriş
                </Text>
                <Feather name="arrow-right" size={18} color={colors.onBrandPrimary} />
              </>
            )}
          </Pressable>

          {isMockMode ? (
            <View
              style={[
                styles.demoNote,
                { backgroundColor: colors.surfaceTertiary, borderColor: colors.border },
              ]}
              testID="mock-mode-note"
            >
              <Feather name="info" size={14} color={colors.brandPrimary} />
              <Text style={[styles.demoNoteText, { color: colors.muted }]}>
                Demo modu: herhangi bir kod ile giriş yapabilirsiniz.
              </Text>
            </View>
          ) : null}

          <View style={{ flex: 1, minHeight: 24 }} />

          <Text style={[styles.footer, { color: colors.muted }]}>
            {isMockMode
              ? "v1.0 · Demo modu — production sistemine bağlanmıyor"
              : "v1.0 · NSX Cloud"}
          </Text>
        </ScrollView>
      </KeyboardAvoidingView>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  brandWrap: { alignItems: "center", marginBottom: spacing.xl, gap: 6 },
  iconHalo: {
    width: 112,
    height: 112,
    borderRadius: 28,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
    marginBottom: 6,
    overflow: "hidden",
  },
  brandTitle: {
    fontFamily: fonts.display,
    fontSize: 22,
    letterSpacing: -0.6,
    textAlign: "center",
  },
  subtitle: {
    fontFamily: fonts.body,
    fontSize: 14,
    lineHeight: 20,
    textAlign: "center",
    paddingHorizontal: 12,
  },
  banner: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    padding: 12,
    borderRadius: radius.md,
    borderWidth: 1,
    marginBottom: spacing.lg,
  },
  bannerIcon: {
    width: 28,
    height: 28,
    borderRadius: 10,
    alignItems: "center",
    justifyContent: "center",
  },
  bannerText: {
    flex: 1,
    fontFamily: fonts.bodyMedium,
    fontSize: 13,
    lineHeight: 18,
  },
  qrShell: { position: "relative" },
  qrGlow: {
    position: "absolute",
    top: -14,
    left: 8,
    right: 8,
    height: 100,
    borderRadius: 36,
  },
  qrCard: {
    borderRadius: 22,
    padding: 20,
    borderWidth: 1,
    alignItems: "center",
  },
  qrFrame: {
    width: 132,
    height: 132,
    borderRadius: 18,
    borderWidth: 1.5,
    borderStyle: "dashed",
    alignItems: "center",
    justifyContent: "center",
    marginBottom: 14,
  },
  qrDot: {
    position: "absolute",
    width: 8,
    height: 8,
    borderRadius: 4,
    opacity: 0.7,
  },
  scanCta: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 20,
    height: 44,
    borderRadius: radius.pill,
    marginBottom: 12,
  },
  scanCtaLabel: {
    fontFamily: fonts.bodyBold,
    fontSize: 14,
    letterSpacing: -0.2,
  },
  qrHint: {
    fontFamily: fonts.body,
    fontSize: 13,
    lineHeight: 19,
    textAlign: "center",
  },
  dividerRow: {
    marginTop: 22,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  dividerLine: { flex: 1, height: StyleSheet.hairlineWidth },
  dividerLabel: { fontFamily: fonts.bodySemiBold, fontSize: 12 },
  inputSection: { marginTop: 20, gap: 8 },
  inputLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.6,
    textTransform: "uppercase",
  },
  inputWrap: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 16,
    borderWidth: 1.5,
  },
  inputIcon: {
    width: 34,
    height: 34,
    borderRadius: 11,
    alignItems: "center",
    justifyContent: "center",
  },
  input: {
    flex: 1,
    fontFamily: fonts.bodySemiBold,
    fontSize: 16,
    letterSpacing: 0.8,
    paddingVertical: 6,
  },
  errorChip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 12,
    borderWidth: 1,
  },
  errorText: { flex: 1, fontFamily: fonts.bodyMedium, fontSize: 13, lineHeight: 18 },
  cta: {
    marginTop: 22,
    height: 54,
    borderRadius: radius.pill,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
  },
  ctaLabel: {
    fontFamily: fonts.bodyBold,
    fontSize: 16,
    letterSpacing: -0.2,
  },
  demoNote: {
    marginTop: 16,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 14,
    paddingVertical: 12,
    borderRadius: 14,
    borderWidth: 1,
  },
  demoNoteText: { flex: 1, fontFamily: fonts.body, fontSize: 12, lineHeight: 17 },
  footer: {
    fontFamily: fonts.body,
    fontSize: 11,
    textAlign: "center",
    marginTop: 20,
  },
});
