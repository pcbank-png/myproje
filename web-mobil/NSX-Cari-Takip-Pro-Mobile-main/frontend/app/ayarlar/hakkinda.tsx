// Hakkında — uygulama sürümü, marka, yasal linkler.
import React from "react";
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  Pressable,
  Linking,
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import Constants from "expo-constants";
import { ScreenHeader } from "@/src/components/screen-header";
import { BrandAppIcon } from "@/src/components/brand-app-icon";
import { useTheme, spacing, radius, fonts } from "@/src/theme";

type IconName = React.ComponentProps<typeof Feather>["name"];

const NSX_WEB = "https://www.nsxyazilim.com";
const PRIVACY_URL = "https://www.nsxyazilim.com/gizlilik";
const TERMS_URL = "https://www.nsxyazilim.com/kullanim-kosullari";

export default function AboutScreen() {
  const insets = useSafeAreaInsets();
  const { colors, scheme } = useTheme();

  const appVersion = Constants.expoConfig?.version ?? "1.0.0";
  const runtimeVersion = Constants.expoConfig?.runtimeVersion ?? "—";
  const nativeAppVersion = Constants.nativeAppVersion ?? "—";
  const buildRef =
    typeof runtimeVersion === "string" ? runtimeVersion : "—";

  const glow =
    scheme === "light"
      ? (["rgba(37,99,235,0.16)", "rgba(37,99,235,0.04)", "transparent"] as const)
      : (["rgba(59,130,246,0.22)", "rgba(59,130,246,0.06)", "transparent"] as const);

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="about-screen">
      <ScreenHeader title="Hakkında" />
      <ScrollView
        contentContainerStyle={{
          paddingHorizontal: spacing.lg,
          paddingBottom: insets.bottom + 32,
          paddingTop: spacing.md,
        }}
      >
        {/* Brand hero */}
        <View
          style={[
            styles.hero,
            {
              backgroundColor: colors.surfaceInverse,
              borderColor: colors.border,
            },
          ]}
        >
          <LinearGradient
            colors={[...glow]}
            style={StyleSheet.absoluteFill}
            start={{ x: 0.5, y: 0 }}
            end={{ x: 0.5, y: 1 }}
          />
          <BrandAppIcon
            size={118}
            variant={scheme === "light" ? "dark" : "light"}
            surface={colors.surfaceInverse}
            testID="about-brand"
          />
          <Text style={[styles.heroTitle, { color: colors.onSurfaceInverse }]}>
            NSX Cari Takip Pro
          </Text>
          <Text style={[styles.heroSub, { color: colors.onSurfaceInverse }]}>
            Mobil Uygulama · v{appVersion}
          </Text>
        </View>

        <Text style={[styles.sectionTitle, { color: colors.muted }]}>SÜRÜM BİLGİSİ</Text>
        <View style={[styles.card, { backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}>
          <InfoRow label="Uygulama Sürümü" value={`v${appVersion}`} />
          <InfoRow label="Native Sürüm" value={nativeAppVersion} />
          <InfoRow label="Runtime" value={buildRef} last />
        </View>

        <Text style={[styles.sectionTitle, { color: colors.muted, marginTop: spacing.xl }]}>
          NSX YAZILIM
        </Text>
        <View style={[styles.card, { backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}>
          <LinkRow
            icon="globe"
            label="Resmi Web Sitesi"
            hint="nsxyazilim.com"
            onPress={() => Linking.openURL(NSX_WEB).catch(() => {})}
            testID="about-web"
          />
          <LinkRow
            icon="shield"
            label="Gizlilik Politikası"
            onPress={() => Linking.openURL(PRIVACY_URL).catch(() => {})}
            testID="about-privacy"
          />
          <LinkRow
            icon="file-text"
            label="Kullanım Koşulları"
            onPress={() => Linking.openURL(TERMS_URL).catch(() => {})}
            testID="about-terms"
            last
          />
        </View>

        <Text style={[styles.copyright, { color: colors.muted }]}>
          © {new Date().getFullYear()} NSX Yazılım · Tüm hakları saklıdır.
        </Text>
      </ScrollView>
    </View>
  );
}

function InfoRow({ label, value, last }: { label: string; value: string; last?: boolean }) {
  const { colors } = useTheme();
  return (
    <View
      style={[
        styles.infoRow,
        {
          borderBottomColor: colors.divider,
          borderBottomWidth: last ? 0 : StyleSheet.hairlineWidth,
        },
      ]}
    >
      <Text style={[styles.infoLabel, { color: colors.muted }]}>{label}</Text>
      <Text style={[styles.infoValue, { color: colors.onSurface }]}>{value}</Text>
    </View>
  );
}

interface LinkProps {
  icon: IconName;
  label: string;
  hint?: string;
  onPress: () => void;
  testID?: string;
  last?: boolean;
}

function LinkRow({ icon, label, hint, onPress, testID, last }: LinkProps) {
  const { colors } = useTheme();
  return (
    <Pressable
      testID={testID}
      onPress={onPress}
      style={({ pressed }) => [
        styles.linkRow,
        {
          borderBottomColor: colors.divider,
          borderBottomWidth: last ? 0 : StyleSheet.hairlineWidth,
          opacity: pressed ? 0.85 : 1,
        },
      ]}
    >
      <View style={[styles.iconWrap, { backgroundColor: colors.brandTertiary }]}>
        <Feather name={icon} size={16} color={colors.onBrandTertiary} />
      </View>
      <View style={{ flex: 1 }}>
        <Text style={[styles.linkLabel, { color: colors.onSurface }]}>{label}</Text>
        {hint ? <Text style={[styles.linkHint, { color: colors.muted }]}>{hint}</Text> : null}
      </View>
      <Feather name="external-link" size={16} color={colors.borderStrong} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  hero: {
    alignItems: "center",
    paddingVertical: spacing.xl + 4,
    paddingHorizontal: spacing.xl,
    borderRadius: 24,
    marginTop: spacing.md,
    gap: 6,
    borderWidth: 1,
    overflow: "hidden",
  },
  heroTitle: {
    fontFamily: fonts.display,
    fontSize: 20,
    letterSpacing: -0.5,
    marginTop: 4,
  },
  heroSub: {
    fontFamily: fonts.bodyMedium,
    fontSize: 12,
    opacity: 0.7,
  },
  sectionTitle: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    marginTop: spacing.lg,
    marginBottom: spacing.sm,
    marginLeft: 4,
  },
  card: { borderRadius: radius.md, borderWidth: 1, overflow: "hidden" },
  infoRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: 14,
    paddingVertical: 14,
  },
  infoLabel: { fontFamily: fonts.body, fontSize: 13 },
  infoValue: {
    fontFamily: fonts.bodyBold,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
  },
  linkRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 14,
  },
  iconWrap: {
    width: 32,
    height: 32,
    borderRadius: 10,
    alignItems: "center",
    justifyContent: "center",
  },
  linkLabel: { fontFamily: fonts.bodySemiBold, fontSize: 15 },
  linkHint: { fontFamily: fonts.body, fontSize: 12, marginTop: 2 },
  copyright: {
    fontFamily: fonts.body,
    fontSize: 11,
    textAlign: "center",
    marginTop: spacing.xl,
  },
});
