import React, { useMemo, useState } from "react";
import {
  View,
  Text,
  FlatList,
  StyleSheet,
  Pressable,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter } from "expo-router";
import { nsxApi } from "@/src/api";
import { nsxApiErrorMessage } from "@/src/api/types";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import { initials } from "@/src/utils/format";
import { MoneyText } from "@/src/components/money-text";
import { TransactionRow } from "@/src/components/transaction-row";
import { EmptyState } from "@/src/components/empty-state";
import { ScreenHeader } from "@/src/components/screen-header";
import { SwipeableRow, type SwipeAction } from "@/src/components/swipeable-row";
import { ConfirmSheet } from "@/src/components/confirm-sheet";
import { CustomerDetailSkeleton, ListSkeleton } from "@/src/components/skeleton";
import { BrandPullSpinner, useBrandPull } from "@/src/components/brand-refresh";
import { refreshHaptic, warnHaptic, successHaptic, errorHaptic } from "@/src/utils/haptics";
import { playRefreshSound } from "@/src/utils/audio";

interface PendingTxDelete {
  id: string;
  kind: "debt" | "collection";
  version: number;
}

export default function CustomerDetailScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const router = useRouter();
  const qc = useQueryClient();

  const [manualRefreshing, setManualRefreshing] = useState(false);
  const [pendingTx, setPendingTx] = useState<PendingTxDelete | null>(null);
  const [customerDeleteOpen, setCustomerDeleteOpen] = useState(false);

  const customerQ = useQuery({
    queryKey: ["customer", id],
    queryFn: () => nsxApi.getCustomer(id!),
    enabled: !!id,
  });

  const txQ = useQuery({
    queryKey: ["customer-tx", id],
    queryFn: () => nsxApi.getCustomerTransactions(id!),
    enabled: !!id,
  });

  const onRefresh = async () => {
    if (manualRefreshing) return;
    setManualRefreshing(true);
    refreshHaptic();
    try {
      await Promise.all([customerQ.refetch(), txQ.refetch()]);
      playRefreshSound();
    } finally {
      setManualRefreshing(false);
    }
  };
  const { pullActive, pullScrollProps } = useBrandPull({ onRefresh });

  const deleteTxMut = useMutation({
    mutationFn: (v: { id: string; kind: "debt" | "collection"; version: number }) =>
      nsxApi.deleteTransaction(v.id, v.kind, v.version),
    onSuccess: () => {
      successHaptic();
      qc.invalidateQueries();
      setPendingTx(null);
    },
    onError: () => errorHaptic(),
  });
  const askTxDelete = (txId: string, kind: "debt" | "collection", version: number) => {
    warnHaptic();
    setPendingTx({ id: txId, kind, version });
  };

  const deleteMut = useMutation({
    mutationFn: () => nsxApi.deleteCustomer(id!, customerQ.data?.version),
    onSuccess: () => {
      successHaptic();
      // Önce bu ekrandan çık (silinecek query'lerin refetch'i önler),
      // sonra listeleri sessizce yenile.
      router.back();
      qc.invalidateQueries();
    },
    onError: () => errorHaptic(),
  });

  const balance = customerQ.data?.netBalance ?? 0;
  // PC: Net = Alacak − Tahsilat. >0 kalan alacak, <0 fazla tahsilat
  const hasReceivable = balance > 0;
  const overCollected = balance < 0;

  const summary = useMemo(() => {
    const list = txQ.data ?? [];
    const totalDebt = list.filter((t) => t.kind === "debt").reduce((s, t) => s + t.amount, 0);
    const totalColl = list.filter((t) => t.kind === "collection").reduce((s, t) => s + t.amount, 0);
    return { totalDebt, totalColl };
  }, [txQ.data]);

  const onDelete = () => {
    warnHaptic();
    setCustomerDeleteOpen(true);
  };

  if (customerQ.isPending) {
    return (
      <View style={[styles.root, { backgroundColor: colors.surface }]}>
        <ScreenHeader title="Cari Kart" />
        <CustomerDetailSkeleton />
      </View>
    );
  }
  const customer = customerQ.data;
  if (customerQ.isError || !customer) {
    return (
      <View style={[styles.center, { backgroundColor: colors.surface }]}>
        <ScreenHeader title="Bulunamadı" />
        <Text style={{ color: colors.muted, marginTop: 20 }}>Müşteri bulunamadı.</Text>
      </View>
    );
  }

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="customer-detail">
      <BrandPullSpinner active={pullActive} topInset={insets.top} />
      <ScreenHeader
        title="Cari Kart"
        right={
          <View style={{ flexDirection: "row", alignItems: "center", gap: 16 }}>
            <Pressable
              onPress={() => router.push(`/musteri-new?editId=${customer.id}`)}
              hitSlop={8}
              testID="customer-edit"
            >
              <Feather name="edit-2" size={18} color={colors.brandPrimary} />
            </Pressable>
            <Pressable onPress={onDelete} hitSlop={8} testID="customer-delete">
              <Feather name="trash-2" size={18} color={colors.error} />
            </Pressable>
          </View>
        }
      />
      <FlatList
        data={txQ.data ?? []}
        keyExtractor={(t) => t.id}
        contentContainerStyle={{ paddingBottom: insets.bottom + 96 }}
        {...pullScrollProps}
        ListHeaderComponent={
          <View>
            <View style={styles.identity}>
              <View style={[styles.avatar, { backgroundColor: colors.brandTertiary }]}>
                <Text style={[styles.avatarText, { color: colors.onBrandTertiary }]}>
                  {initials(customer.name)}
                </Text>
              </View>
              <Text style={[styles.name, { color: colors.onSurface }]}>{customer.name}</Text>
              {customer.phone ? (
                <View style={styles.metaRow}>
                  <Feather name="phone" size={12} color={colors.muted} />
                  <Text style={[styles.meta, { color: colors.muted }]}>{customer.phone}</Text>
                </View>
              ) : null}
              {customer.email ? (
                <View style={styles.metaRow}>
                  <Feather name="mail" size={12} color={colors.muted} />
                  <Text style={[styles.meta, { color: colors.muted }]}>{customer.email}</Text>
                </View>
              ) : null}
            </View>

            <View
              style={[
                styles.balanceCard,
                { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
              ]}
              testID="customer-balance-card"
            >
              <Text style={[styles.balanceLabel, { color: colors.muted }]}>Net Bakiye</Text>
              <MoneyText
                value={balance}
                size="hero"
                color={
                  hasReceivable
                    ? colors.success
                    : overCollected
                    ? colors.error
                    : colors.onSurface
                }
                adjustsFontSizeToFit
                style={{ marginTop: 4 }}
              />
              <View
                style={[
                  styles.statusPill,
                  {
                    backgroundColor: hasReceivable
                      ? colors.successSoft
                      : overCollected
                      ? colors.errorSoft
                      : colors.surfaceTertiary,
                  },
                ]}
              >
                <View
                  style={[
                    styles.statusDot,
                    {
                      backgroundColor: hasReceivable
                        ? colors.success
                        : overCollected
                        ? colors.error
                        : colors.muted,
                    },
                  ]}
                />
                <Text
                  style={[
                    styles.statusPillLabel,
                    {
                      color: hasReceivable
                        ? colors.success
                        : overCollected
                        ? colors.error
                        : colors.muted,
                    },
                  ]}
                >
                  {hasReceivable
                    ? "Kalan alacak"
                    : overCollected
                    ? "Fazla tahsilat"
                    : "Hesap kapalı"}
                </Text>
              </View>
              <View style={[styles.balanceFooter, { borderTopColor: colors.divider }]}>
                <View style={styles.balanceFooterItem}>
                  <Text style={[styles.balanceFooterLabel, { color: colors.muted }]}>
                    Toplam Borç
                  </Text>
                  <MoneyText value={summary.totalDebt} color={colors.error} size="md" />
                </View>
                <View style={{ width: StyleSheet.hairlineWidth, backgroundColor: colors.divider }} />
                <View style={styles.balanceFooterItem}>
                  <Text style={[styles.balanceFooterLabel, { color: colors.muted }]}>
                    Toplam Tahsilat
                  </Text>
                  <MoneyText value={summary.totalColl} color={colors.success} size="md" />
                </View>
              </View>
            </View>

            <View style={styles.actionRow}>
              <Pressable
                testID="detail-collection-btn"
                onPress={() =>
                  router.push({
                    pathname: "/hareket-new",
                    params: { kind: "collection", customerId: customer.id },
                  })
                }
                style={({ pressed }) => [
                  styles.actionBtn,
                  {
                    backgroundColor: colors.successSoft,
                    borderColor: colors.success + "44",
                    opacity: pressed ? 0.88 : 1,
                    transform: [{ scale: pressed ? 0.98 : 1 }],
                  },
                ]}
              >
                <View style={[styles.actionIcon, { backgroundColor: colors.success }]}>
                  <Feather name="arrow-down-left" size={16} color={colors.onSuccess} />
                </View>
                <Text style={[styles.actionLabel, { color: colors.success }]}>Tahsilat Yap</Text>
              </Pressable>
              <Pressable
                testID="detail-debt-btn"
                onPress={() =>
                  router.push({
                    pathname: "/hareket-new",
                    params: { kind: "debt", customerId: customer.id },
                  })
                }
                style={({ pressed }) => [
                  styles.actionBtn,
                  {
                    backgroundColor: colors.errorSoft,
                    borderColor: colors.error + "44",
                    opacity: pressed ? 0.88 : 1,
                    transform: [{ scale: pressed ? 0.98 : 1 }],
                  },
                ]}
              >
                <View style={[styles.actionIcon, { backgroundColor: colors.error }]}>
                  <Feather name="arrow-up-right" size={16} color={colors.onError} />
                </View>
                <Text style={[styles.actionLabel, { color: colors.error }]}>Borç Ekle</Text>
              </Pressable>
            </View>

            <Text style={[styles.sectionTitle, { color: colors.onSurface }]}>Hareketler</Text>
          </View>
        }
        renderItem={({ item }) => {
          const actions: SwipeAction[] = [
            {
              key: "edit",
              label: "Düzenle",
              icon: "edit-2",
              tone: "primary",
              onPress: () =>
                router.push({
                  pathname: "/hareket-new",
                  params: { editId: item.id, customerId: item.customerId, kind: item.kind },
                }),
              testID: `detail-tx-swipe-edit-${item.id}`,
            },
            {
              key: "delete",
              label: "Sil",
              icon: "trash-2",
              tone: "danger",
              onPress: () => askTxDelete(item.id, item.kind, item.version),
              testID: `detail-tx-swipe-delete-${item.id}`,
            },
          ];
          return (
            <SwipeableRow actions={actions}>
              <TransactionRow tx={item} testID={`detail-tx-${item.id}`} />
            </SwipeableRow>
          );
        }}
        ListEmptyComponent={
          txQ.isPending ? (
            <ListSkeleton rows={4} />
          ) : (
            <EmptyState
              testID="detail-empty"
              icon="inbox"
              title="Henüz hareket yok"
              description="Bu müşteri için ilk borç veya tahsilatı ekleyerek başlayın."
              actionLabel="Tahsilat Yap"
              onAction={() =>
                router.push({
                  pathname: "/hareket-new",
                  params: { kind: "collection", customerId: id },
                })
              }
              secondaryActionLabel="Borç Ekle"
              onSecondaryAction={() =>
                router.push({
                  pathname: "/hareket-new",
                  params: { kind: "debt", customerId: id },
                })
              }
            />
          )
        }
      />

      <ConfirmSheet
        visible={!!pendingTx}
        onDismiss={() => {
          if (!deleteTxMut.isPending) {
            setPendingTx(null);
            deleteTxMut.reset();
          }
        }}
        onConfirm={() =>
          pendingTx && deleteTxMut.mutate({ id: pendingTx.id, kind: pendingTx.kind, version: pendingTx.version })
        }
        loading={deleteTxMut.isPending}
        tone="danger"
        icon="trash-2"
        title="Kaydı Sil"
        description={
          pendingTx
            ? `Bu ${pendingTx.kind === "debt" ? "borç" : "tahsilat"} kaydı kalıcı olarak silinecek. Bu işlem geri alınamaz.`
            : ""
        }
        confirmLabel="Sil"
        errorMessage={deleteTxMut.error ? nsxApiErrorMessage(deleteTxMut.error, "delete") : null}
        testID="detail-tx-delete-sheet"
      />

      <ConfirmSheet
        visible={customerDeleteOpen}
        onDismiss={() => {
          if (!deleteMut.isPending) {
            setCustomerDeleteOpen(false);
            deleteMut.reset();
          }
        }}
        onConfirm={() => deleteMut.mutate()}
        loading={deleteMut.isPending}
        tone="danger"
        icon="trash-2"
        title="Müşteriyi Sil"
        description={`"${customer.name}" adlı müşteri ve tüm cari hareketleri kalıcı olarak silinecek. Bu işlem geri alınamaz.`}
        confirmLabel="Sil"
        errorMessage={deleteMut.error ? nsxApiErrorMessage(deleteMut.error, "delete") : null}
        testID="customer-detail-delete-sheet"
      />
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  center: { flex: 1, alignItems: "center", justifyContent: "center" },
  identity: { alignItems: "center", padding: spacing.lg, gap: 6 },
  avatar: {
    width: 68,
    height: 68,
    borderRadius: 22,
    alignItems: "center",
    justifyContent: "center",
    marginBottom: 6,
  },
  avatarText: { fontFamily: fonts.display, fontSize: 22 },
  name: {
    fontFamily: fonts.display,
    fontSize: 22,
    letterSpacing: -0.5,
    textAlign: "center",
  },
  metaRow: { flexDirection: "row", alignItems: "center", gap: 6, marginTop: 2 },
  meta: { fontFamily: fonts.body, fontSize: 13 },
  balanceCard: {
    marginHorizontal: spacing.lg,
    padding: spacing.xl,
    borderRadius: radius.lg,
    borderWidth: 1,
  },
  balanceLabel: {
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
  balanceFooter: {
    flexDirection: "row",
    marginTop: 20,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingTop: 16,
    gap: 12,
  },
  balanceFooterItem: { flex: 1, gap: 6 },
  balanceFooterLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.3,
    textTransform: "uppercase",
  },
  actionRow: {
    flexDirection: "row",
    gap: spacing.md,
    paddingHorizontal: spacing.lg,
    marginTop: spacing.lg,
  },
  actionBtn: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 10,
    height: 54,
    borderRadius: radius.md,
    borderWidth: 1,
  },
  actionIcon: {
    width: 28,
    height: 28,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
  },
  actionLabel: { fontFamily: fonts.bodyBold, fontSize: 14, letterSpacing: -0.2 },
  sectionTitle: {
    fontFamily: fonts.displayMedium,
    fontSize: 17,
    letterSpacing: -0.3,
    paddingHorizontal: spacing.lg,
    marginTop: spacing.xl,
    marginBottom: spacing.md,
  },
});
