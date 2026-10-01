// Bağlı Cihazlar — GET /api/mobile/devices ile canlı liste.
// Cihaz adı, platform, son görülme ve aktif/pasif durum gösterilir.
// (Revoke aksiyonu ileride ayrı bir contract ile açılacak; şimdi
// yalnızca görüntüleme.)

import React from "react";
import {
  View,
  Text,
  StyleSheet,
  FlatList,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { ScreenHeader } from "@/src/components/screen-header";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import { nsxApi } from "@/src/api";
import { nsxApiErrorMessage, type ConnectedDevice } from "@/src/api/types";
import { EmptyState, ErrorState } from "@/src/components/empty-state";
import { ListSkeleton } from "@/src/components/skeleton";
import { BrandPullSpinner, useBrandPull } from "@/src/components/brand-refresh";
import { refreshHaptic } from "@/src/utils/haptics";
import { playRefreshSound } from "@/src/utils/audio";
import { adoptNativeDeviceId } from "@/src/auth/device-identity";

function formatRelative(iso?: string): string {
  if (!iso) return "-";
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return "-";
  const diff = Date.now() - ms;
  if (diff < 60_000) return "Şu an";
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} dk önce`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} saat önce`;
  return `${Math.floor(diff / 86_400_000)} gün önce`;
}

export default function ConnectedDevicesScreen() {
  const insets = useSafeAreaInsets();
  const router = useRouter();
  const { colors } = useTheme();
  const [manualRefreshing, setManualRefreshing] = React.useState(false);

  const q = useQuery({
    queryKey: ["devices"],
    queryFn: () => nsxApi.getDevices(),
  });

  const onRefresh = async () => {
    if (manualRefreshing) return;
    setManualRefreshing(true);
    refreshHaptic();
    try {
      await q.refetch();
      playRefreshSound();
    } finally {
      setManualRefreshing(false);
    }
  };
  const { pullActive, pullScrollProps } = useBrandPull({ onRefresh });

  const errorMessage = q.error ? nsxApiErrorMessage(q.error, "read") : null;

  // Existing live sessions were created before the stable device-id field was
  // added. When this screen sees the server's current row, adopt its id so a
  // future re-pair reuses the same record instead of creating another one.
  React.useEffect(() => {
    const current = q.data?.find((d) => d.self);
    if (current?.id) void adoptNativeDeviceId(current.id);
  }, [q.data]);

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="devices-screen">
      <BrandPullSpinner active={pullActive} topInset={insets.top} />
      <ScreenHeader title="Bağlı Cihazlar" />
      {q.isPending ? (
        <ListSkeleton rows={5} />
      ) : errorMessage && (!q.data || q.data.length === 0) ? (
        <ErrorState
          testID="devices-error"
          title="Cihaz listesi alınamadı"
          description={errorMessage}
          onRetry={() => q.refetch()}
        />
      ) : (
        <FlatList
          data={q.data ?? []}
          keyExtractor={(d) => d.id}
          contentContainerStyle={{
            paddingHorizontal: spacing.lg,
            paddingBottom: insets.bottom + 32,
            paddingTop: spacing.md,
          }}
          ItemSeparatorComponent={() => <View style={{ height: 10 }} />}
          {...pullScrollProps}
          ListHeaderComponent={
            <View
              style={[
                styles.tip,
                { backgroundColor: colors.brandTertiary, borderColor: colors.brandPrimary + "44" },
              ]}
            >
              <View style={[styles.tipIcon, { backgroundColor: colors.brandPrimary }]}>
                <Feather name="shield" size={14} color={colors.onBrandPrimary} />
              </View>
              <Text style={[styles.tipText, { color: colors.onBrandTertiary }]}>
                Bu hesaba bağlı mobil oturumlar. Yeni cihaz için Ayarlar’dan davet oluşturun.
              </Text>
            </View>
          }
          renderItem={({ item }) => <DeviceRow device={item} />}
          ListEmptyComponent={
            <EmptyState
              testID="devices-empty"
              icon="smartphone"
              title="Bağlı cihaz yok"
              description="Davet oluşturarak başka bir mobil cihazı bu hesaba güvenle eşleştirin."
              actionLabel="Cihaz Davet Et"
              onAction={() => router.push("/cihaz-davet")}
            />
          }
        />
      )}
    </View>
  );
}

function DeviceRow({ device }: { device: ConnectedDevice }) {
  const { colors } = useTheme();
  const active = device.status === "active";
  const platformLabel =
    device.platform === "ios" ? "iOS" : device.platform === "android" ? "Android" : "Web";
  return (
    <View
      testID={`device-row-${device.id}`}
      style={[
        styles.row,
        {
          backgroundColor: device.self ? colors.brandTertiary : colors.surfaceSecondary,
          borderColor: device.self ? colors.brandPrimary + "44" : colors.border,
        },
      ]}
    >
      <View
        style={[
          styles.deviceIcon,
          {
            backgroundColor: device.self ? colors.brandPrimary : colors.surfaceTertiary,
          },
        ]}
      >
        <Feather
          name="smartphone"
          size={18}
          color={device.self ? colors.onBrandPrimary : colors.onSurface}
        />
      </View>
      <View style={{ flex: 1, minWidth: 0 }}>
        <View style={{ flexDirection: "row", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
          <Text
            style={[
              styles.deviceName,
              { color: device.self ? colors.onBrandTertiary : colors.onSurface },
            ]}
            numberOfLines={1}
          >
            {device.name}
          </Text>
          {device.self ? (
            <View style={[styles.selfBadge, { backgroundColor: colors.brandPrimary }]}>
              <Text style={[styles.selfBadgeText, { color: colors.onBrandPrimary }]}>BU CİHAZ</Text>
            </View>
          ) : null}
        </View>
        <Text
          style={[
            styles.deviceMeta,
            { color: device.self ? colors.onBrandTertiary : colors.muted, opacity: device.self ? 0.8 : 1 },
          ]}
        >
          {platformLabel}
          {device.appVersion ? ` · v${device.appVersion}` : ""}
          {" · Son görülme: "}
          {formatRelative(device.lastSeenAt)}
        </Text>
      </View>
      <View
        style={[
          styles.statusPill,
          { backgroundColor: active ? colors.successSoft : colors.surfaceTertiary },
        ]}
      >
        <View
          style={[
            styles.statusDot,
            { backgroundColor: active ? colors.success : colors.borderStrong },
          ]}
        />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  tip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    padding: 12,
    borderRadius: radius.md,
    borderWidth: 1,
    marginBottom: spacing.md,
  },
  tipIcon: {
    width: 28,
    height: 28,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
  },
  tipText: { flex: 1, fontFamily: fonts.bodyMedium, fontSize: 13, lineHeight: 18 },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    padding: 14,
    borderRadius: 16,
    borderWidth: 1,
  },
  deviceIcon: {
    width: 42,
    height: 42,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
  },
  deviceName: { fontFamily: fonts.bodySemiBold, fontSize: 15, letterSpacing: -0.2 },
  deviceMeta: { fontFamily: fonts.body, fontSize: 12, marginTop: 2 },
  selfBadge: {
    paddingHorizontal: 7,
    paddingVertical: 3,
    borderRadius: 6,
  },
  selfBadgeText: {
    fontFamily: fonts.bodyBold,
    fontSize: 10,
    letterSpacing: 0.4,
  },
  statusPill: {
    width: 22,
    height: 22,
    borderRadius: 11,
    alignItems: "center",
    justifyContent: "center",
  },
  statusDot: { width: 8, height: 8, borderRadius: 4 },
});
