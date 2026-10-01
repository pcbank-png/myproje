import React, { useState } from "react";
import {
  View,
  Text,
  ScrollView,
  StyleSheet,
  Pressable,
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useAuth } from "@/src/auth/auth-context";
import { nsxApi, isMockMode } from "@/src/api";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import { QuickAction } from "@/src/components/quick-action";
import { TransactionRow } from "@/src/components/transaction-row";
import { MoneyText } from "@/src/components/money-text";
import { EmptyState } from "@/src/components/empty-state";
import { DashboardSkeleton } from "@/src/components/skeleton";
import { BrandPullSpinner, useBrandPull } from "@/src/components/brand-refresh";
import { useNotificationInbox } from "@/src/notifications/inbox";
import { useInboxPolling } from "@/src/notifications/use-inbox-polling";
import { isRealtimeConnected } from "@/src/realtime/realtime-state";
import { useTabNav } from "@/src/navigation/tab-nav";
import { refreshHaptic, selectionHaptic } from "@/src/utils/haptics";
import { playRefreshSound } from "@/src/utils/audio";

export default function DashboardScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const router = useRouter();
  const { jumpTo } = useTabNav();
  const { activeCompanyId, companies, switchCompany, session } = useAuth();

  const activeCompany = companies.find((c) => c.id === activeCompanyId);
  const pairedUser = session?.pairedBy?.displayName;

  const statusQuery = useQuery({
    queryKey: ["status", activeCompanyId],
    queryFn: () => nsxApi.getStatus(activeCompanyId!),
    enabled: !!activeCompanyId,
  });

  const recentQuery = useQuery({
    queryKey: ["recent", activeCompanyId],
    queryFn: () => nsxApi.getRecentTransactions(activeCompanyId!, 30),
    enabled: !!activeCompanyId,
  });

  // Otomatik refetch ile manuel pull-to-refresh tamamen ayrı: spinner
  // yalnızca kullanıcı parmağıyla aşağı çektiğinde görünür.
  const [manualRefreshing, setManualRefreshing] = useState(false);
  const onRefresh = async () => {
    if (manualRefreshing) return;
    setManualRefreshing(true);
    refreshHaptic();
    try {
      await Promise.all([statusQuery.refetch(), recentQuery.refetch()]);
      playRefreshSound();
    } finally {
      setManualRefreshing(false);
    }
  };

  const cycleCompany = () => {
    if (companies.length < 2 || !activeCompanyId) return;
    const idx = companies.findIndex((c) => c.id === activeCompanyId);
    const next = companies[(idx + 1) % companies.length];
    switchCompany(next.id);
  };

  const status = statusQuery.data;
  const bootLoading = statusQuery.isPending && !statusQuery.data;
  // PC: Alacak (brüt) · Tahsilat · Net Bakiye (kalan alacak)
  const alacak = status?.totalReceivable ?? 0;
  const tahsilat = status?.totalCollected ?? 0;
  const net = status?.netBalance ?? alacak - tahsilat;
  const heroGlow =
    net > 0
      ? (["rgba(16,185,129,0.14)", "rgba(16,185,129,0.02)", "transparent"] as const)
      : net < 0
      ? (["rgba(239,68,68,0.14)", "rgba(239,68,68,0.02)", "transparent"] as const)
      : (["rgba(37,99,235,0.12)", "rgba(37,99,235,0.02)", "transparent"] as const);
  const live = !isMockMode && isRealtimeConnected();
  const { pullActive, pullScrollProps } = useBrandPull({ onRefresh });
  const { unreadCount } = useNotificationInbox();
  useInboxPolling();

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="dashboard-screen">
      <BrandPullSpinner active={pullActive} topInset={insets.top} />
      <ScrollView
        contentContainerStyle={{
          paddingTop: insets.top + 16,
          paddingBottom: 32,
        }}
        {...pullScrollProps}
      >
        {bootLoading ? (
          <DashboardSkeleton />
        ) : (
          <>
        {/* Company selector header */}
        <View style={styles.headerRow}>
          <View style={{ flex: 1 }}>
            <Text style={[styles.hello, { color: colors.muted }]}>Aktif firma</Text>
            <Pressable
              onPress={cycleCompany}
              disabled={companies.length < 2}
              style={styles.companyRow}
              testID="dashboard-company-switch"
            >
              <Text
                style={[styles.companyName, { color: colors.onSurface }]}
                numberOfLines={1}
              >
                {activeCompany?.name ?? session?.companyName ?? "Firma seçiniz"}
              </Text>
              {companies.length > 1 ? (
                <Feather name="chevron-down" size={18} color={colors.muted} />
              ) : null}
            </Pressable>
            {pairedUser ? (
              <Text
                style={[styles.pairedUser, { color: colors.muted }]}
                numberOfLines={1}
                testID="dashboard-paired-user"
              >
                Eşleşen kullanıcı: {pairedUser}
              </Text>
            ) : null}
          </View>
          <Pressable
            style={({ pressed }) => [
              styles.notifBtn,
              {
                backgroundColor: colors.brandTertiary,
                borderColor: colors.brandPrimary + "33",
                opacity: pressed ? 0.88 : 1,
                transform: [{ scale: pressed ? 0.96 : 1 }],
              },
            ]}
            hitSlop={8}
            onPress={() => router.push("/ayarlar/bildirimler")}
            testID="dashboard-notification-btn"
          >
            <Feather name="bell" size={18} color={colors.onBrandTertiary} />
            {unreadCount > 0 ? (
              <View
                style={[
                  styles.notifBadge,
                  { backgroundColor: colors.error, borderColor: colors.surface },
                ]}
                testID="dashboard-notif-badge"
              >
                <Text style={[styles.notifBadgeText, { color: colors.onError }]}>
                  {unreadCount > 99 ? "99+" : String(unreadCount)}
                </Text>
              </View>
            ) : null}
          </Pressable>
        </View>

        {/* Hero balance — para hissi + soft glow */}
        <View
          style={[
            styles.hero,
            { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
          ]}
          testID="dashboard-hero"
        >
          <LinearGradient
            colors={[...heroGlow]}
            style={StyleSheet.absoluteFill}
            start={{ x: 0.5, y: 0 }}
            end={{ x: 0.5, y: 1 }}
          />
          <Text style={[styles.heroLabel, { color: colors.muted }]}>Net Bakiye</Text>
          {statusQuery.isPending && !status ? (
            <View style={{ marginTop: 12, gap: 12 }}>
              <View style={{ height: 34, width: "60%", borderRadius: 10, backgroundColor: colors.border }} />
            </View>
          ) : (
            <>
              <MoneyText
                value={net}
                size="hero"
                color={
                  net > 0 ? colors.success : net < 0 ? colors.error : colors.onSurface
                }
                adjustsFontSizeToFit
                style={{ marginTop: 6 }}
              />
              <View
                style={[
                  styles.statusPill,
                  {
                    backgroundColor:
                      net > 0
                        ? colors.successSoft
                        : net < 0
                        ? colors.errorSoft
                        : colors.surfaceTertiary,
                  },
                ]}
              >
                <View
                  style={[
                    styles.statusDot,
                    {
                      backgroundColor:
                        net > 0 ? colors.success : net < 0 ? colors.error : colors.muted,
                    },
                  ]}
                />
                <Text
                  style={[
                    styles.statusPillLabel,
                    {
                      color:
                        net > 0 ? colors.success : net < 0 ? colors.error : colors.muted,
                    },
                  ]}
                >
                  {net > 0
                    ? "Kalan alacak"
                    : net < 0
                    ? "Fazla tahsilat"
                    : "Bakiye kapalı"}
                </Text>
              </View>

              {/* PC üçlüsü: Alacak · Tahsilat · Net Bakiye */}
              <View
                style={[
                  styles.analysisPanel,
                  { backgroundColor: colors.surface, borderColor: colors.border },
                ]}
                testID="dashboard-analysis"
              >
                <AnalysisCol
                  label="Alacak"
                  value={alacak}
                  color={colors.brandPrimary}
                  soft={colors.brandTertiary}
                  testID="analysis-receivable"
                />
                <View style={[styles.analysisDivider, { backgroundColor: colors.divider }]} />
                <AnalysisCol
                  label="Tahsilat"
                  value={tahsilat}
                  color={colors.success}
                  soft={colors.successSoft}
                  testID="analysis-collection"
                />
                <View style={[styles.analysisDivider, { backgroundColor: colors.divider }]} />
                <AnalysisCol
                  label="Net Bakiye"
                  value={net}
                  color={
                    net > 0 ? colors.success : net < 0 ? colors.error : colors.muted
                  }
                  soft={
                    net > 0
                      ? colors.successSoft
                      : net < 0
                      ? colors.errorSoft
                      : colors.surfaceTertiary
                  }
                  testID="analysis-net"
                />
              </View>
            </>
          )}
          <View style={[styles.heroFooter, { borderTopColor: colors.divider }]}>
            <View style={styles.heroFooterItem}>
              <Feather name="users" size={12} color={colors.muted} />
              <Text style={[styles.heroFooterLabel, { color: colors.muted }]}>
                {status?.customerCount ?? 0} müşteri
              </Text>
            </View>
            <View style={styles.heroFooterItem}>
              <View
                style={[
                  styles.liveDot,
                  { backgroundColor: live ? colors.success : colors.muted },
                ]}
              />
              <Text style={[styles.heroFooterLabel, { color: colors.muted }]}>
                {live ? "Canlı" : "Şimdi güncellendi"}
              </Text>
            </View>
          </View>
        </View>

        {/* Quick actions */}
        <View style={styles.sectionHeader}>
          <Text style={[styles.sectionTitle, { color: colors.onSurface }]}>Hızlı İşlemler</Text>
        </View>
        <View style={styles.actionGrid}>
          <QuickAction
            testID="qa-collection"
            label="Tahsilat Yap"
            icon="arrow-down-left"
            tone="success"
            onPress={() => router.push({ pathname: "/hareket-new", params: { kind: "collection" } })}
          />
          <QuickAction
            testID="qa-debt"
            label="Borç Ekle"
            icon="arrow-up-right"
            tone="error"
            onPress={() => router.push({ pathname: "/hareket-new", params: { kind: "debt" } })}
          />
        </View>
        <View style={[styles.actionGrid, { marginTop: 12 }]}>
          <QuickAction
            testID="qa-new-customer"
            label="Yeni Müşteri"
            icon="user-plus"
            tone="primary"
            onPress={() => router.push("/musteri-new")}
          />
          <QuickAction
            testID="qa-search-customer"
            label="Müşteri Ara"
            icon="search"
            tone="neutral"
            onPress={() => {
              selectionHaptic();
              jumpTo("musteriler");
            }}
          />
        </View>

        {/* Recent transactions */}
        <View style={[styles.sectionHeader, { marginTop: 18 }]}>
          <Text style={[styles.sectionTitle, { color: colors.onSurface }]}>Son Hareketler</Text>
          <Pressable
            onPress={() => {
              selectionHaptic();
              jumpTo("hareketler");
            }}
            testID="dashboard-see-all"
          >
            <Text
              style={{
                color: colors.brandPrimary,
                fontFamily: fonts.bodySemiBold,
                fontSize: 13,
              }}
            >
              Tümü
            </Text>
          </Pressable>
        </View>
        <View style={[styles.listCard, { backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}>
          {recentQuery.isPending ? (
            <View style={{ paddingVertical: 8 }}>
              {[0, 1, 2].map((i) => (
                <View
                  key={i}
                  style={{
                    flexDirection: "row",
                    alignItems: "center",
                    gap: 12,
                    paddingHorizontal: 16,
                    paddingVertical: 14,
                  }}
                >
                  <View
                    style={{
                      width: 40,
                      height: 40,
                      borderRadius: 14,
                      backgroundColor: colors.border,
                      opacity: 0.55,
                    }}
                  />
                  <View style={{ flex: 1, gap: 8 }}>
                    <View style={{ width: "55%", height: 12, borderRadius: 6, backgroundColor: colors.border, opacity: 0.55 }} />
                    <View style={{ width: "35%", height: 10, borderRadius: 5, backgroundColor: colors.border, opacity: 0.4 }} />
                  </View>
                </View>
              ))}
            </View>
          ) : recentQuery.data && recentQuery.data.length > 0 ? (
            recentQuery.data.slice(0, 8).map((tx) => (
              <TransactionRow
                key={tx.id}
                tx={tx}
                showCustomer
                testID={`recent-tx-${tx.id}`}
                onPress={() => router.push(`/musteri/${tx.customerId}`)}
              />
            ))
          ) : (
            <EmptyState
              testID="dashboard-recent-empty"
              icon="activity"
              title="Henüz hareket yok"
              description="İlk tahsilat veya borç kaydıyla cari takibi başlatın."
              actionLabel="Tahsilat Yap"
              onAction={() =>
                router.push({ pathname: "/hareket-new", params: { kind: "collection" } })
              }
              secondaryActionLabel="Borç Ekle"
              onSecondaryAction={() =>
                router.push({ pathname: "/hareket-new", params: { kind: "debt" } })
              }
            />
          )}
        </View>
          </>
        )}
      </ScrollView>
    </View>
  );
}

function AnalysisCol({
  label,
  value,
  color,
  soft,
  testID,
}: {
  label: string;
  value: number;
  color: string;
  soft: string;
  testID?: string;
}) {
  return (
    <View style={styles.analysisCol} testID={testID}>
      <View style={[styles.analysisDot, { backgroundColor: soft }]}>
        <View style={[styles.analysisDotCore, { backgroundColor: color }]} />
      </View>
      <Text style={[styles.analysisLabel, { color }]} numberOfLines={1}>
        {label}
      </Text>
      <MoneyText
        value={value}
        color={color}
        size="sm"
        adjustsFontSizeToFit
        style={styles.analysisValue}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  headerRow: {
    flexDirection: "row",
    alignItems: "flex-end",
    paddingHorizontal: spacing.lg,
    paddingBottom: spacing.lg,
    gap: spacing.md,
  },
  hello: { fontFamily: fonts.bodySemiBold, fontSize: 12 },
  companyRow: { flexDirection: "row", alignItems: "center", gap: 6, marginTop: 4 },
  companyName: {
    fontFamily: fonts.display,
    fontSize: 22,
    letterSpacing: -0.5,
  },
  pairedUser: { fontFamily: fonts.body, fontSize: 12, marginTop: 2 },
  notifBtn: {
    width: 40,
    height: 40,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
  },
  notifBadge: {
    position: "absolute",
    top: -4,
    right: -4,
    minWidth: 18,
    height: 18,
    borderRadius: 9,
    paddingHorizontal: 4,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 2,
  },
  notifBadgeText: {
    fontFamily: fonts.bodyBold,
    fontSize: 10,
    lineHeight: 12,
  },
  hero: {
    marginHorizontal: spacing.lg,
    padding: spacing.lg,
    borderRadius: radius.lg,
    borderWidth: 1,
    minHeight: 140,
    justifyContent: "space-between",
    overflow: "hidden",
  },
  heroLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    textTransform: "uppercase",
  },
  statusPill: {
    alignSelf: "flex-start",
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 10,
    paddingVertical: 5,
    borderRadius: 999,
    marginTop: 10,
  },
  statusDot: { width: 6, height: 6, borderRadius: 3 },
  statusPillLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.2,
  },
  analysisPanel: {
    flexDirection: "column",
    alignItems: "stretch",
    marginTop: 10,
    borderRadius: 14,
    borderWidth: 1,
    overflow: "hidden",
  },
  analysisCol: {
    flexDirection: "row",
    alignItems: "center",
    paddingVertical: 6,
    paddingHorizontal: 6,
    gap: 6,
  },
  analysisDivider: {
    height: StyleSheet.hairlineWidth,
    alignSelf: "stretch",
    marginHorizontal: 8,
  },
  analysisDot: {
    width: 18,
    height: 18,
    borderRadius: 6,
    alignItems: "center",
    justifyContent: "center",
  },
  analysisDotCore: {
    width: 8,
    height: 8,
    borderRadius: 4,
  },
  analysisLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 10,
    letterSpacing: 0.4,
    textTransform: "uppercase",
  },
  analysisValue: {
    textAlign: "right",
    flex: 1,
    minWidth: 0,
  },
  heroFooter: { flexDirection: "row", gap: 16, marginTop: 16, borderTopWidth: 1, paddingTop: 12 },
  heroFooterItem: { flexDirection: "row", alignItems: "center", gap: 6 },
  heroFooterLabel: { fontFamily: fonts.bodyMedium, fontSize: 12 },
  liveDot: { width: 7, height: 7, borderRadius: 4 },
  sectionHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: spacing.lg,
    marginTop: spacing.xl,
    marginBottom: spacing.md,
  },
  sectionTitle: {
    fontFamily: fonts.displayMedium,
    fontSize: 17,
    letterSpacing: -0.3,
  },
  actionGrid: {
    flexDirection: "row",
    gap: spacing.md,
    paddingHorizontal: spacing.lg,
  },
  listCard: {
    marginHorizontal: spacing.lg,
    borderRadius: radius.lg,
    borderWidth: 1,
    overflow: "hidden",
  },
});
