// Yedekleme & Senkronizasyon — mobil uygulama backend/database'e
// dokunmaz; burada yalnızca "durum" gösterilir:
// - Bulut senkronizasyon: SignalR bağlantı durumu (canlı ise "gerçek
//   zamanlı"), aksi halde "polling fallback aktif"
// - Son başarılı eşitleme: TanStack Query'de en son başarılı fetch
//   zamanı (dashboard status query'sinden okunur)
// - Backup: Mevcut backend'de mobil-tarafından tetiklenen bir backup
//   yok → dürüst şekilde "Bulut yedekleme sunucu tarafında otomatik
//   çalışır" bilgisi.

import React, { useEffect, useMemo, useState } from "react";
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  ActivityIndicator,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useQuery } from "@tanstack/react-query";
import { ScreenHeader } from "@/src/components/screen-header";
import { useTheme, spacing, fonts } from "@/src/theme";
import { useAuth } from "@/src/auth/auth-context";
import { isMockMode, nsxApi } from "@/src/api";
import { isRealtimeConnected } from "@/src/realtime/realtime-state";

function formatRelative(ms: number | null): string {
  if (!ms) return "Henüz eşitlenmedi";
  const diff = Date.now() - ms;
  if (diff < 60_000) return "Az önce";
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} dk önce`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} saat önce`;
  return `${Math.floor(diff / 86_400_000)} gün önce`;
}

export default function BackupScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const { activeCompanyId } = useAuth();
  const [tick, setTick] = useState(0);

  // Status query — cache'den okuyoruz; "son eşitleme" için dataUpdatedAt.
  const statusQ = useQuery({
    queryKey: ["status", activeCompanyId],
    queryFn: () => nsxApi.getStatus(activeCompanyId!),
    enabled: !!activeCompanyId,
  });

  // Realtime durumu 5 sn'de bir yeniden değerlendirilsin (UI etiketi
  // güncel kalsın; state modülü subscribe yok, sadece read).
  useEffect(() => {
    const i = setInterval(() => setTick((t) => t + 1), 5_000);
    return () => clearInterval(i);
  }, []);
  void tick;

  const realtime = !isMockMode && isRealtimeConnected();
  const lastSync = useMemo(
    () => (statusQ.dataUpdatedAt ? formatRelative(statusQ.dataUpdatedAt) : formatRelative(null)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [statusQ.dataUpdatedAt, tick],
  );

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="backup-screen">
      <ScreenHeader title="Yedekleme & Senkronizasyon" />
      <ScrollView
        contentContainerStyle={{
          paddingHorizontal: spacing.lg,
          paddingBottom: insets.bottom + 32,
          paddingTop: spacing.md,
        }}
      >
        {/* Bulut Senkronizasyonu */}
        <Card>
          <RowHeader
            icon="cloud"
            title="Bulut Senkronizasyonu"
            badge={
              isMockMode
                ? { label: "DEMO", color: colors.muted }
                : realtime
                ? { label: "GERÇEK ZAMANLI", color: colors.success }
                : { label: "POLLING", color: colors.warning }
            }
          />
          <Text style={[styles.desc, { color: colors.muted }]}>
            {isMockMode
              ? "Demo modunda tüm veriler yerel bellekte tutulur; bulut senkronizasyonu devre dışı."
              : realtime
              ? "SignalR gerçek zamanlı kanal aktif. Verileriniz değişikliği anında sessizce güncellenir."
              : "Gerçek zamanlı bağlantı yok; kısa aralıklarla sessiz polling ile veri taze tutulur."}
          </Text>
        </Card>

        {/* Son Eşitleme */}
        <Card>
          <RowHeader icon="refresh-cw" title="Son Başarılı Eşitleme" />
          <View style={styles.lastSyncRow}>
            {statusQ.isPending ? (
              <ActivityIndicator color={colors.brandPrimary} size="small" />
            ) : (
              <Feather
                name={statusQ.isError ? "alert-circle" : "check-circle"}
                size={18}
                color={statusQ.isError ? colors.error : colors.success}
              />
            )}
            <Text style={[styles.lastSync, { color: colors.onSurface }]}>
              {statusQ.isError ? "Eşitleme başarısız" : lastSync}
            </Text>
          </View>
          <Text style={[styles.desc, { color: colors.muted }]}>
            Ekranı aşağıya çekerek istediğiniz zaman manuel yenileyebilirsiniz.
          </Text>
        </Card>

        {/* Bulut Yedekleme */}
        <Card>
          <RowHeader icon="shield" title="Bulut Yedekleme" />
          <Text style={[styles.desc, { color: colors.muted }]}>
            Bulut yedekleme NSX sunucu tarafında otomatik olarak yönetilir. Mobil uygulamadan manuel bir veri yedekleme veya geri yükleme yapılmaz — güvenlik ve tutarlılık açısından bu işlemler yalnızca sunucu tarafında gerçekleşir.
          </Text>
          <View style={[styles.pendingChip, { borderColor: colors.border }]}>
            <Feather name="info" size={12} color={colors.muted} />
            <Text style={[styles.pendingChipText, { color: colors.muted }]}>
              Sunucu tarafında otomatik
            </Text>
          </View>
        </Card>
      </ScrollView>
    </View>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  const { colors } = useTheme();
  return (
    <View
      style={[
        styles.card,
        { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
      ]}
    >
      {children}
    </View>
  );
}

interface RowHeaderProps {
  icon: React.ComponentProps<typeof Feather>["name"];
  title: string;
  badge?: { label: string; color: string };
}

function RowHeader({ icon, title, badge }: RowHeaderProps) {
  const { colors } = useTheme();
  return (
    <View style={styles.rowHeader}>
      <View style={[styles.iconWrap, { backgroundColor: colors.brandTertiary }]}>
        <Feather name={icon} size={16} color={colors.onBrandTertiary} />
      </View>
      <Text style={[styles.rowTitle, { color: colors.onSurface }]}>{title}</Text>
      {badge ? (
        <View style={[styles.badge, { borderColor: badge.color }]}>
          <View style={[styles.dot, { backgroundColor: badge.color }]} />
          <Text style={[styles.badgeText, { color: badge.color }]}>{badge.label}</Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  card: {
    borderRadius: 18,
    borderWidth: 1,
    padding: 16,
    marginTop: spacing.md,
    gap: 10,
  },
  rowHeader: { flexDirection: "row", alignItems: "center", gap: 10 },
  iconWrap: {
    width: 34,
    height: 34,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  rowTitle: {
    flex: 1,
    fontFamily: fonts.bodySemiBold,
    fontSize: 15,
    letterSpacing: -0.2,
  },
  badge: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 9,
    paddingVertical: 5,
    borderRadius: 999,
    borderWidth: 1,
  },
  dot: { width: 6, height: 6, borderRadius: 3 },
  badgeText: {
    fontFamily: fonts.bodyBold,
    fontSize: 10,
    letterSpacing: 0.4,
  },
  desc: { fontFamily: fonts.body, fontSize: 13, lineHeight: 20 },
  lastSyncRow: { flexDirection: "row", alignItems: "center", gap: 10 },
  lastSync: { fontFamily: fonts.displayMedium, fontSize: 18, letterSpacing: -0.4 },
  pendingChip: {
    alignSelf: "flex-start",
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 10,
    paddingVertical: 5,
    borderRadius: 999,
    borderWidth: 1,
  },
  pendingChipText: { fontFamily: fonts.bodySemiBold, fontSize: 11 },
});
