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
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useAuth } from "@/src/auth/auth-context";
import { nsxApi } from "@/src/api";
import { nsxApiErrorMessage } from "@/src/api/types";
import { useTheme, spacing, fonts } from "@/src/theme";
import { TransactionRow } from "@/src/components/transaction-row";
import { EmptyState, ErrorState } from "@/src/components/empty-state";
import { SwipeableRow, type SwipeAction } from "@/src/components/swipeable-row";
import { ConfirmSheet } from "@/src/components/confirm-sheet";
import { ListSkeleton } from "@/src/components/skeleton";
import { BrandPullSpinner, useBrandPull } from "@/src/components/brand-refresh";
import {
  refreshHaptic,
  warnHaptic,
  successHaptic,
  errorHaptic,
  selectionHaptic,
} from "@/src/utils/haptics";
import { playRefreshSound } from "@/src/utils/audio";

type Filter = "all" | "debt" | "collection";

interface PendingDelete {
  id: string;
  label: string;
  kind: "debt" | "collection";
  version: number;
}

export default function TransactionsScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const router = useRouter();
  const { activeCompanyId } = useAuth();
  const [filter, setFilter] = useState<Filter>("all");
  const [manualRefreshing, setManualRefreshing] = useState(false);
  const [pending, setPending] = useState<PendingDelete | null>(null);
  const qc = useQueryClient();
  const deleteMut = useMutation({
    mutationFn: (v: { id: string; kind: "debt" | "collection"; version: number }) =>
      nsxApi.deleteTransaction(v.id, v.kind, v.version),
    onSuccess: () => {
      successHaptic();
      qc.invalidateQueries();
      setPending(null);
    },
    onError: () => errorHaptic(),
  });
  const askDelete = (item: PendingDelete) => {
    warnHaptic();
    setPending(item);
  };

  const q = useQuery({
    queryKey: ["recent-full", activeCompanyId],
    queryFn: () => nsxApi.getRecentTransactions(activeCompanyId!, 200),
    enabled: !!activeCompanyId,
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

  const filtered = useMemo(() => {
    const list = q.data ?? [];
    if (filter === "all") return list;
    return list.filter((t) => t.kind === filter);
  }, [q.data, filter]);

  const filters: { key: Filter; label: string }[] = [
    { key: "all", label: "Tümü" },
    { key: "collection", label: "Tahsilatlar" },
    { key: "debt", label: "Borçlar" },
  ];

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="transactions-screen">
      <BrandPullSpinner active={pullActive} topInset={insets.top} />
      <View
        style={[
          styles.header,
          { paddingTop: insets.top + 12, backgroundColor: colors.surface, borderBottomColor: colors.divider },
        ]}
      >
        <View style={styles.titleRow}>
          <Text style={[styles.title, { color: colors.onSurface }]}>Hareketler</Text>
        </View>
        <View style={[styles.segment, { backgroundColor: colors.surfaceTertiary }]}>
          {filters.map((f) => {
            const selected = f.key === filter;
            const accent =
              f.key === "collection"
                ? colors.success
                : f.key === "debt"
                ? colors.error
                : colors.brandPrimary;
            const soft =
              f.key === "collection"
                ? colors.successSoft
                : f.key === "debt"
                ? colors.errorSoft
                : colors.surfaceSecondary;
            return (
              <Pressable
                key={f.key}
                testID={`tx-filter-${f.key}`}
                onPress={() => {
                  if (f.key !== filter) selectionHaptic();
                  setFilter(f.key);
                }}
                style={({ pressed }) => [
                  styles.segmentBtn,
                  {
                    backgroundColor: selected ? soft : "transparent",
                    borderColor: selected ? accent + "44" : "transparent",
                    opacity: pressed ? 0.9 : 1,
                    transform: [{ scale: pressed ? 0.98 : 1 }],
                  },
                ]}
              >
                <Text
                  style={{
                    color: selected ? accent : colors.muted,
                    fontFamily: selected ? fonts.bodyBold : fonts.bodyMedium,
                    fontSize: 13,
                    letterSpacing: -0.1,
                  }}
                >
                  {f.label}
                </Text>
              </Pressable>
            );
          })}
        </View>
      </View>

      {q.isPending ? (
        <ListSkeleton rows={8} />
      ) : q.isError && !q.data ? (
        <ErrorState
          testID="tx-error"
          title="Hareketler yüklenemedi"
          description={nsxApiErrorMessage(q.error, "read")}
          onRetry={() => q.refetch()}
        />
      ) : (
        <FlatList
          data={filtered}
          keyExtractor={(item) => item.id}
          contentContainerStyle={{ paddingBottom: 100 }}
          {...pullScrollProps}
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
                testID: `tx-swipe-edit-${item.id}`,
              },
              {
                key: "delete",
                label: "Sil",
                icon: "trash-2",
                tone: "danger",
                onPress: () =>
                  askDelete({
                    id: item.id,
                    label: `${item.customerName} · ${item.kind === "debt" ? "Borç" : "Tahsilat"}`,
                    kind: item.kind,
                    version: item.version,
                  }),
                testID: `tx-swipe-delete-${item.id}`,
              },
            ];
            return (
              <SwipeableRow actions={actions}>
                <TransactionRow
                  tx={item}
                  showCustomer
                  testID={`tx-row-${item.id}`}
                  onPress={(tx) => router.push(`/musteri/${tx.customerId}`)}
                />
              </SwipeableRow>
            );
          }}
          ListEmptyComponent={
            <EmptyState
              testID="tx-empty"
              icon="activity"
              title={
                filter === "all"
                  ? "Henüz hareket yok"
                  : filter === "debt"
                  ? "Borç kaydı yok"
                  : "Tahsilat kaydı yok"
              }
              description={
                filter === "all"
                  ? "İlk tahsilat veya borç kaydıyla cari akışı başlatın."
                  : "Bu filtreye uyan işlem bulunamadı. Filtreyi değiştirin veya yeni kayıt ekleyin."
              }
              actionLabel={filter === "debt" ? "Borç Ekle" : "Tahsilat Yap"}
              onAction={() =>
                router.push({
                  pathname: "/hareket-new",
                  params: { kind: filter === "debt" ? "debt" : "collection" },
                })
              }
              secondaryActionLabel={
                filter === "all" ? "Borç Ekle" : filter === "debt" ? "Tahsilat Yap" : "Borç Ekle"
              }
              onSecondaryAction={() =>
                router.push({
                  pathname: "/hareket-new",
                  params: {
                    kind:
                      filter === "all" || filter === "collection" ? "debt" : "collection",
                  },
                })
              }
            />
          }
        />
      )}

      <Pressable
        testID="tx-fab"
        onPress={() =>
          router.push({ pathname: "/hareket-new", params: { kind: "collection" } })
        }
        style={({ pressed }) => [
          styles.fab,
          {
            backgroundColor: colors.brandPrimary,
            bottom: insets.bottom + 18,
            opacity: pressed ? 0.92 : 1,
            transform: [{ scale: pressed ? 0.96 : 1 }],
          },
        ]}
      >
        <Feather name="plus" size={22} color={colors.onBrandPrimary} />
      </Pressable>

      <ConfirmSheet
        visible={!!pending}
        onDismiss={() => {
          if (!deleteMut.isPending) {
            setPending(null);
            deleteMut.reset();
          }
        }}
        onConfirm={() =>
          pending && deleteMut.mutate({ id: pending.id, kind: pending.kind, version: pending.version })
        }
        loading={deleteMut.isPending}
        tone="danger"
        icon="trash-2"
        title="Kaydı Sil"
        description={
          pending
            ? `${pending.label} kaydı kalıcı olarak silinecek. Bu işlem geri alınamaz.`
            : ""
        }
        confirmLabel="Sil"
        errorMessage={deleteMut.error ? nsxApiErrorMessage(deleteMut.error, "delete") : null}
        testID="tx-delete-sheet"
      />
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  header: { paddingHorizontal: spacing.lg, paddingBottom: 12, borderBottomWidth: StyleSheet.hairlineWidth },
  titleRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginBottom: 12,
  },
  title: {
    fontFamily: fonts.display,
    fontSize: 28,
    letterSpacing: -0.7,
  },
  segment: {
    flexDirection: "row",
    padding: 4,
    borderRadius: 14,
    gap: 2,
  },
  segmentBtn: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    paddingVertical: 9,
    borderRadius: 10,
    borderWidth: 1,
  },
  fab: {
    position: "absolute",
    right: 20,
    width: 56,
    height: 56,
    borderRadius: 18,
    alignItems: "center",
    justifyContent: "center",
    shadowColor: "#2563EB",
    shadowOpacity: 0.28,
    shadowRadius: 16,
    shadowOffset: { width: 0, height: 8 },
    elevation: 8,
  },
});
