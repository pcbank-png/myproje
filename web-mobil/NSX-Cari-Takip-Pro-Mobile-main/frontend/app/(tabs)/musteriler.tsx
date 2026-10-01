import React, { useMemo, useState } from "react";
import {
  View,
  Text,
  TextInput,
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
import { useTheme, spacing, fonts } from "@/src/theme";
import { CustomerRow } from "@/src/components/customer-row";
import { EmptyState, ErrorState } from "@/src/components/empty-state";
import { SwipeableRow } from "@/src/components/swipeable-row";
import { ConfirmSheet } from "@/src/components/confirm-sheet";
import { ListSkeleton } from "@/src/components/skeleton";
import { BrandPullSpinner, useBrandPull } from "@/src/components/brand-refresh";
import { refreshHaptic, warnHaptic, successHaptic, errorHaptic } from "@/src/utils/haptics";
import { playRefreshSound } from "@/src/utils/audio";
import { nsxApiErrorMessage } from "@/src/api/types";

type Filter = "all" | "debt" | "credit";

interface PendingDelete {
  id: string;
  name: string;
  version: number;
}

export default function CustomersScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const router = useRouter();
  const { activeCompanyId } = useAuth();
  const [search, setSearch] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [manualRefreshing, setManualRefreshing] = useState(false);
  const [pending, setPending] = useState<PendingDelete | null>(null);
  const qc = useQueryClient();
  const deleteMut = useMutation({
    mutationFn: (v: { id: string; version: number }) =>
      nsxApi.deleteCustomer(v.id, v.version),
    onSuccess: () => {
      successHaptic();
      qc.invalidateQueries();
      setPending(null);
    },
    onError: () => {
      errorHaptic();
    },
  });
  const askDelete = (id: string, name: string, version: number) => {
    warnHaptic();
    setPending({ id, name, version });
  };

  const q = useQuery({
    queryKey: ["customers", activeCompanyId, search],
    queryFn: () => nsxApi.getCustomers(activeCompanyId!, search),
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
    // PC: Alacaklı = firmanın kalan alacağı (>0). Borçlu = fazla tahsilat (<0).
    if (filter === "credit") return list.filter((c) => c.netBalance > 0);
    if (filter === "debt") return list.filter((c) => c.netBalance < 0);
    return list;
  }, [q.data, filter]);

  const filters: { key: Filter; label: string; count: number }[] = useMemo(() => {
    const list = q.data ?? [];
    return [
      { key: "all", label: "Tümü", count: list.length },
      { key: "credit", label: "Alacaklı", count: list.filter((c) => c.netBalance > 0).length },
      { key: "debt", label: "Borçlu", count: list.filter((c) => c.netBalance < 0).length },
    ];
  }, [q.data]);

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="customers-screen">
      <BrandPullSpinner active={pullActive} topInset={insets.top} />
      <View
        style={[
          styles.header,
          { paddingTop: insets.top + 12, backgroundColor: colors.surface, borderBottomColor: colors.divider },
        ]}
      >
        <View style={styles.titleRow}>
          <Text style={[styles.title, { color: colors.onSurface }]}>Müşteriler</Text>
          <Pressable
            testID="customers-add-btn"
            onPress={() => router.push("/musteri-new")}
            style={[styles.addBtn, { backgroundColor: colors.brandPrimary }]}
          >
            <Feather name="plus" size={18} color={colors.onBrandPrimary} />
          </Pressable>
        </View>
        <View
          style={[
            styles.searchBox,
            { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
          ]}
        >
          <Feather name="search" size={16} color={colors.muted} />
          <TextInput
            testID="customers-search-input"
            style={[styles.searchInput, { color: colors.onSurface }]}
            value={search}
            onChangeText={setSearch}
            placeholder="İsim veya telefon ile ara"
            placeholderTextColor={colors.muted}
            autoCorrect={false}
          />
          {search ? (
            <Pressable onPress={() => setSearch("")} hitSlop={8} testID="customers-clear-search">
              <Feather name="x" size={16} color={colors.muted} />
            </Pressable>
          ) : null}
        </View>
        <View style={styles.chipRow}>
          {filters.map((f) => {
            const selected = f.key === filter;
            const accent =
              f.key === "credit"
                ? colors.success
                : f.key === "debt"
                ? colors.error
                : colors.brandPrimary;
            const soft =
              f.key === "credit"
                ? colors.successSoft
                : f.key === "debt"
                ? colors.errorSoft
                : colors.brandTertiary;
            return (
              <Pressable
                key={f.key}
                testID={`customers-filter-${f.key}`}
                onPress={() => setFilter(f.key)}
                style={({ pressed }) => [
                  styles.chip,
                  {
                    backgroundColor: selected ? soft : colors.surfaceSecondary,
                    borderColor: selected ? accent + "55" : colors.border,
                    opacity: pressed ? 0.9 : 1,
                    transform: [{ scale: pressed ? 0.97 : 1 }],
                  },
                ]}
              >
                <Text
                  style={[
                    styles.chipLabel,
                    { color: selected ? accent : colors.onSurface },
                  ]}
                >
                  {f.label}
                </Text>
                <View
                  style={[
                    styles.chipCount,
                    {
                      backgroundColor: selected ? accent + "22" : colors.surfaceTertiary,
                    },
                  ]}
                >
                  <Text
                    style={{
                      color: selected ? accent : colors.muted,
                      fontFamily: fonts.bodyBold,
                      fontSize: 11,
                    }}
                  >
                    {f.count}
                  </Text>
                </View>
              </Pressable>
            );
          })}
        </View>
      </View>

      {q.isPending ? (
        <ListSkeleton rows={8} />
      ) : q.isError && !q.data ? (
        <ErrorState
          testID="customers-error"
          title="Müşteriler yüklenemedi"
          description={nsxApiErrorMessage(q.error, "read")}
          onRetry={() => q.refetch()}
        />
      ) : (
        <FlatList
          data={filtered}
          keyExtractor={(item) => item.id}
          contentContainerStyle={{ paddingBottom: 24 }}
          {...pullScrollProps}
          renderItem={({ item }) => (
            <SwipeableRow
              actions={[
                {
                  key: "edit",
                  label: "Düzenle",
                  icon: "edit-2",
                  tone: "primary",
                  onPress: () => router.push(`/musteri-new?editId=${item.id}`),
                  testID: `customer-swipe-edit-${item.id}`,
                },
                {
                  key: "delete",
                  label: "Sil",
                  icon: "trash-2",
                  tone: "danger",
                  onPress: () => askDelete(item.id, item.name, item.version),
                  testID: `customer-swipe-delete-${item.id}`,
                },
              ]}
            >
              <CustomerRow
                customer={item}
                testID={`customer-row-${item.id}`}
                onPress={(id) => router.push(`/musteri/${id}`)}
              />
            </SwipeableRow>
          )}
          ListEmptyComponent={
            <EmptyState
              testID="customers-empty"
              icon="users"
              title={search ? "Sonuç bulunamadı" : "Henüz müşteri yok"}
              description={
                search
                  ? "Farklı bir isim veya telefon ile tekrar deneyin."
                  : "İlk müşterinizi ekleyerek cari takibe başlayın."
              }
              actionLabel={search ? undefined : "İlk Müşteriyi Ekle"}
              onAction={search ? undefined : () => router.push("/musteri-new")}
            />
          }
        />
      )}

      <ConfirmSheet
        visible={!!pending}
        onDismiss={() => {
          if (!deleteMut.isPending) {
            setPending(null);
            deleteMut.reset();
          }
        }}
        onConfirm={() => pending && deleteMut.mutate({ id: pending.id, version: pending.version })}
        loading={deleteMut.isPending}
        tone="danger"
        icon="trash-2"
        title="Müşteriyi Sil"
        description={
          pending
            ? `"${pending.name}" adlı müşteri ve tüm cari hareketleri kalıcı olarak silinecek. Bu işlem geri alınamaz.`
            : ""
        }
        confirmLabel="Sil"
        errorMessage={deleteMut.error ? nsxApiErrorMessage(deleteMut.error, "delete") : null}
        testID="customer-delete-sheet"
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
  addBtn: {
    width: 40,
    height: 40,
    borderRadius: 20,
    alignItems: "center",
    justifyContent: "center",
  },
  searchBox: {
    flexDirection: "row",
    alignItems: "center",
    paddingHorizontal: 14,
    height: 46,
    borderRadius: 14,
    borderWidth: 1,
    gap: 8,
  },
  searchInput: { flex: 1, fontFamily: fonts.body, fontSize: 15 },
  chipRow: { flexDirection: "row", gap: 8, marginTop: 12 },
  chip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 12,
    height: 36,
    borderRadius: 999,
    borderWidth: 1,
    flexShrink: 0,
  },
  chipLabel: { fontFamily: fonts.bodySemiBold, fontSize: 13 },
  chipCount: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 999,
    minWidth: 20,
    alignItems: "center",
  },
});
