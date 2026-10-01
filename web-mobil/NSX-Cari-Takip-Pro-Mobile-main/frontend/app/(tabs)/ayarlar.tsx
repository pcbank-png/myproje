import React, { useState } from "react";
import { View, Text, ScrollView, StyleSheet, Pressable, Alert } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useRouter } from "expo-router";
import { useAuth } from "@/src/auth/auth-context";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import { isMockMode } from "@/src/api";
import { useThemePreference } from "@/src/theme-preference";
import { ConfirmSheet } from "@/src/components/confirm-sheet";
import { BottomSheet } from "@/src/components/bottom-sheet";
import { warnHaptic, selectionHaptic } from "@/src/utils/haptics";

type IconName = React.ComponentProps<typeof Feather>["name"];

export default function SettingsScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const router = useRouter();
  const { session, companies, activeCompanyId, switchCompany, logout } = useAuth();
  const { preference } = useThemePreference();

  const [logoutOpen, setLogoutOpen] = useState(false);
  const [logoutLoading, setLogoutLoading] = useState(false);
  const [companySheetOpen, setCompanySheetOpen] = useState(false);

  const activeCompany = companies.find((c) => c.id === activeCompanyId);
  const pairedUser = session?.pairedBy?.displayName;

  const doLogout = async () => {
    setLogoutLoading(true);
    try {
      await logout();
      router.replace("/login");
    } catch {
      Alert.alert("Oturum", "Cihazdaki oturum bilgisi temizlenemedi. Lütfen tekrar deneyin.");
    } finally {
      setLogoutLoading(false);
      setLogoutOpen(false);
    }
  };

  const themeLabel =
    preference === "system" ? "Sistem" : preference === "light" ? "Açık" : "Koyu";

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="settings-screen">
      <ScrollView
        contentContainerStyle={{
          paddingTop: insets.top + 16,
          paddingHorizontal: spacing.lg,
          paddingBottom: insets.bottom + 32,
        }}
      >
        <Text style={[styles.title, { color: colors.onSurface }]}>Ayarlar</Text>

        <View
          style={[
            styles.profileCard,
            { backgroundColor: colors.surfaceInverse },
          ]}
        >
          <View style={[styles.profileAvatar, { backgroundColor: colors.brandPrimary }]}>
            <Text style={{ color: colors.onBrandPrimary, fontFamily: fonts.display, fontSize: 20 }}>
              {activeCompany?.name?.charAt(0) ?? "N"}
            </Text>
          </View>
          <View style={{ flex: 1, gap: 3 }}>
            <Text style={[styles.profileName, { color: colors.onSurfaceInverse }]} numberOfLines={1}>
              {activeCompany?.name ?? session?.companyName ?? "Firma seçilmedi"}
            </Text>
            <Text style={[styles.profileSub, { color: colors.onSurfaceInverse }]} numberOfLines={1}>
              {isMockMode ? "Demo Modu · Yerel Veri" : "NSX Cloud · Aktif Oturum"}
            </Text>
            {pairedUser ? (
              <Text
                style={[styles.profileSub, { color: colors.onSurfaceInverse }]}
                numberOfLines={1}
                testID="settings-paired-user"
              >
                Kullanıcı: {pairedUser}
              </Text>
            ) : null}
          </View>
          <View style={[styles.profileBadge, { backgroundColor: "rgba(255,255,255,0.12)" }]}>
            <Feather name="shield" size={16} color={colors.onSurfaceInverse} />
          </View>
        </View>

        <Section title="HESAP">
          <SettingsRow
            icon="briefcase"
            label="Firma Değiştir"
            hint={companies.length > 1 ? `${companies.length} firma` : "Tek firma"}
            onPress={() => setCompanySheetOpen(true)}
            disabled={companies.length < 2}
            testID="settings-switch-company"
          />
          <SettingsRow
            icon="key"
            label="Cihaz Eşleştirme"
            hint="QR ile yeniden bağla"
            onPress={() => router.push("/login?reauth=1")}
            testID="settings-pair"
            last
          />
        </Section>

        <Section title="MOBİL ERİŞİM">
          <SettingsRow
            icon="share-2"
            label="Cihaz Davet Et"
            hint="QR ile paylaş"
            onPress={() => router.push("/cihaz-davet")}
            testID="settings-invite-device"
          />
          <SettingsRow
            icon="smartphone"
            label="Bağlı Cihazlar"
            hint="Görüntüle"
            onPress={() => router.push("/bagli-cihazlar")}
            testID="settings-devices"
            last
          />
        </Section>

        <Section title="GÖRÜNÜM">
          <SettingsRow
            icon="sun"
            label="Tema"
            hint={themeLabel}
            onPress={() => router.push("/ayarlar/gorunum")}
            testID="settings-appearance"
            last
          />
        </Section>

        <Section title="UYGULAMA">
          <SettingsRow
            icon="bell"
            label="Bildirimler"
            hint="Tercihleri düzenle"
            onPress={() => router.push("/ayarlar/bildirimler")}
            testID="settings-notifications"
          />
          <SettingsRow
            icon="cloud"
            label="Yedekleme & Senkron"
            hint="Bağlantı durumu"
            onPress={() => router.push("/ayarlar/yedekleme")}
            testID="settings-backup"
            last
          />
        </Section>

        <Section title="DESTEK">
          <SettingsRow
            icon="help-circle"
            label="Yardım Merkezi"
            onPress={() => router.push("/ayarlar/yardim")}
            testID="settings-help"
          />
          <SettingsRow
            icon="info"
            label="Hakkında"
            hint="Sürüm bilgisi"
            onPress={() => router.push("/ayarlar/hakkinda")}
            testID="settings-info"
            last
          />
        </Section>

        <Pressable
          testID="settings-logout"
          onPress={() => {
            warnHaptic();
            setLogoutOpen(true);
          }}
          style={({ pressed }) => [
            styles.logout,
            { backgroundColor: colors.surfaceSecondary, borderColor: colors.border, opacity: pressed ? 0.85 : 1 },
          ]}
        >
          <Feather name="log-out" size={18} color={colors.error} />
          <Text style={[styles.logoutLabel, { color: colors.error }]}>Oturumu Kapat</Text>
        </Pressable>

        <Text style={[styles.footer, { color: colors.muted }]}>
          NSX Cari Takip Pro Bulut · Mobile v1.0
        </Text>
      </ScrollView>

      <ConfirmSheet
        visible={logoutOpen}
        onDismiss={() => !logoutLoading && setLogoutOpen(false)}
        onConfirm={doLogout}
        loading={logoutLoading}
        tone="danger"
        icon="log-out"
        title="Oturumu Kapat"
        description="Çıkış yapmak istediğinize emin misiniz? Yeniden giriş için QR kodunu okutmanız gerekir."
        confirmLabel="Çıkış"
        testID="settings-logout-sheet"
      />

      <BottomSheet
        visible={companySheetOpen}
        onDismiss={() => setCompanySheetOpen(false)}
        testID="settings-company-sheet"
      >
        <View style={{ paddingTop: 4, paddingBottom: 12 }}>
          <Text style={[styles.sheetTitle, { color: colors.onSurface }]}>Firma Seç</Text>
          {companies.map((c) => {
            const active = c.id === activeCompanyId;
            return (
              <Pressable
                key={c.id}
                testID={`settings-company-option-${c.id}`}
                onPress={() => {
                  selectionHaptic();
                  switchCompany(c.id);
                  setCompanySheetOpen(false);
                }}
                style={({ pressed }) => [
                  styles.companyOption,
                  { borderBottomColor: colors.divider, opacity: pressed ? 0.85 : 1 },
                ]}
              >
                <View style={[styles.companyBadge, { backgroundColor: colors.brandTertiary }]}>
                  <Text style={{ color: colors.onBrandTertiary, fontFamily: fonts.bodyBold }}>
                    {c.name.charAt(0)}
                  </Text>
                </View>
                <Text style={[styles.companyOptionLabel, { color: colors.onSurface }]}>
                  {c.name}
                </Text>
                {active ? <Feather name="check" size={18} color={colors.brandPrimary} /> : null}
              </Pressable>
            );
          })}
        </View>
      </BottomSheet>
    </View>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  const { colors } = useTheme();
  return (
    <View style={{ marginTop: 24 }}>
      <Text style={[styles.sectionTitle, { color: colors.muted }]}>{title}</Text>
      <View
        style={[
          styles.sectionCard,
          { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
        ]}
      >
        {children}
      </View>
    </View>
  );
}

interface RowProps {
  icon: IconName;
  label: string;
  hint?: string;
  onPress: () => void;
  disabled?: boolean;
  testID?: string;
  last?: boolean;
}

function SettingsRow({ icon, label, hint, onPress, disabled, testID, last }: RowProps) {
  const { colors } = useTheme();
  return (
    <Pressable
      testID={testID}
      onPress={onPress}
      disabled={disabled}
      android_ripple={{ color: colors.surfaceTertiary }}
      style={({ pressed }) => [
        styles.row,
        {
          borderBottomColor: colors.divider,
          borderBottomWidth: last ? 0 : StyleSheet.hairlineWidth,
          opacity: disabled ? 0.5 : pressed ? 0.88 : 1,
          transform: [{ scale: pressed && !disabled ? 0.995 : 1 }],
        },
      ]}
    >
      <View style={[styles.rowIcon, { backgroundColor: colors.brandTertiary }]}>
        <Feather name={icon} size={15} color={colors.onBrandTertiary} />
      </View>
      <Text style={[styles.rowLabel, { color: colors.onSurface }]}>{label}</Text>
      {hint ? <Text style={[styles.rowHint, { color: colors.muted }]}>{hint}</Text> : null}
      {!disabled ? (
        <Feather name="chevron-right" size={16} color={colors.borderStrong} />
      ) : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  title: {
    fontFamily: fonts.display,
    fontSize: 28,
    letterSpacing: -0.7,
    marginBottom: 20,
  },
  profileCard: {
    flexDirection: "row",
    alignItems: "center",
    gap: 14,
    padding: 18,
    borderRadius: radius.lg,
  },
  profileAvatar: {
    width: 52,
    height: 52,
    borderRadius: 16,
    alignItems: "center",
    justifyContent: "center",
  },
  profileBadge: {
    width: 34,
    height: 34,
    borderRadius: 11,
    alignItems: "center",
    justifyContent: "center",
  },
  profileName: { fontFamily: fonts.displayMedium, fontSize: 16, letterSpacing: -0.3 },
  profileSub: { fontFamily: fonts.body, fontSize: 12, opacity: 0.72 },
  sectionTitle: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.6,
    marginBottom: 8,
    marginLeft: 4,
  },
  sectionCard: { borderRadius: radius.lg, borderWidth: 1, overflow: "hidden" },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 14,
  },
  rowIcon: {
    width: 34,
    height: 34,
    borderRadius: 10,
    alignItems: "center",
    justifyContent: "center",
  },
  rowLabel: { flex: 1, fontFamily: fonts.bodySemiBold, fontSize: 15, letterSpacing: -0.2 },
  rowHint: { fontFamily: fonts.body, fontSize: 13 },
  logout: {
    marginTop: 24,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    height: 52,
    borderRadius: radius.md,
    borderWidth: 1,
  },
  logoutLabel: { fontFamily: fonts.bodyBold, fontSize: 15 },
  footer: { fontFamily: fonts.body, fontSize: 12, textAlign: "center", marginTop: 24 },
  sheetTitle: {
    fontFamily: fonts.display,
    fontSize: 20,
    letterSpacing: -0.4,
    paddingHorizontal: 4,
    marginBottom: 12,
  },
  companyOption: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingVertical: 14,
    paddingHorizontal: 4,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  companyBadge: {
    width: 36,
    height: 36,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  companyOptionLabel: { flex: 1, fontFamily: fonts.bodySemiBold, fontSize: 15 },
});
