import { parseMoney } from "@/src/utils/parse-money";
import { BottomSheet } from "@/src/components/bottom-sheet";
import React, { useEffect, useMemo, useState } from "react";
import {
  View,
  Text,
  TextInput,
  StyleSheet,
  Pressable,
  ScrollView,
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  FlatList,
} from "react-native";
import DateTimePicker from "@react-native-community/datetimepicker";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter } from "expo-router";
import { nsxApi } from "@/src/api";
import { nsxApiErrorMessage } from "@/src/api/types";
import { useAuth } from "@/src/auth/auth-context";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import type { TransactionKind } from "@/src/api/types";
import { formatTRY, initials } from "@/src/utils/format";
import { successHaptic, errorHaptic } from "@/src/utils/haptics";

export default function NewTransactionModal() {
  const params = useLocalSearchParams<{
    kind?: TransactionKind;
    customerId?: string;
    editId?: string;
  }>();
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const router = useRouter();
  const qc = useQueryClient();
  const { activeCompanyId } = useAuth();
  const isEdit = !!params.editId;

  const [kind, setKind] = useState<TransactionKind>(params.kind ?? "debt");
  const [customerId, setCustomerId] = useState<string | undefined>(params.customerId);
  const [amountText, setAmountText] = useState("");
  const [description, setDescription] = useState("");
  const [dateISO, setDateISO] = useState<string>(new Date().toISOString());
  const [error, setError] = useState<string | null>(null);
  const [customerSearch, setCustomerSearch] = useState("");
  const [customerPickerOpen, setCustomerPickerOpen] = useState(!customerId && !isEdit);
  const [prefilled, setPrefilled] = useState(false);

  const customersQ = useQuery({
    queryKey: ["customers", activeCompanyId, ""],
    queryFn: () => nsxApi.getCustomers(activeCompanyId!, ""),
    enabled: !!activeCompanyId,
  });

  // Düzenleme modunda mevcut hareketi React Query cache'inden bul ve
  // formu prefill et. Cache'te yoksa müşterinin hareket listesini çek.
  const editSourceQ = useQuery({
    queryKey: ["customer-tx", params.customerId],
    queryFn: () => nsxApi.getCustomerTransactions(params.customerId!),
    enabled: isEdit && !!params.customerId,
  });

  useEffect(() => {
    if (!isEdit || prefilled) return;
    const tx = editSourceQ.data?.find((t) => t.id === params.editId);
    if (!tx) return;
    let cancelled = false;
    void Promise.resolve().then(() => {
      if (cancelled) return;
    setKind(tx.kind);
    setCustomerId(tx.customerId);
    // Türkçe ondalık: tutarı virgüllü metinle prefill.
    setAmountText(
      new Intl.NumberFormat("tr-TR", {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
        useGrouping: false,
      }).format(tx.amount),
    );
    setDescription(tx.description ?? "");
    setDateISO(tx.date);
    setPrefilled(true);
    });
    return () => { cancelled = true; };
  }, [isEdit, prefilled, editSourceQ.data, params.editId]);

  const selectedCustomer = useMemo(
    () => customersQ.data?.find((c) => c.id === customerId),
    [customersQ.data, customerId],
  );

  const filteredCustomers = useMemo(() => {
    const q = customerSearch.trim().toLocaleLowerCase("tr");
    return (customersQ.data ?? []).filter(
      (c) => !q || c.name.toLocaleLowerCase("tr").includes(q),
    );
  }, [customersQ.data, customerSearch]);

  const parsedAmount = useMemo(() => parseMoney(amountText), [amountText]);
  const amount = parsedAmount.amount;

  const mut = useMutation({
    mutationFn: () => {
      if (parsedAmount.error || amount <= 0) throw new Error(parsedAmount.error ?? "Geçerli bir tutar girin.");
      const input = {
        customerId: customerId!,
        kind,
        amount,
        description: description.trim() || undefined,
        date: dateISO,
      };
      return isEdit
        ? nsxApi.updateTransaction(
            params.editId!,
            input,
            editSourceQ.data?.find((t) => t.id === params.editId)?.version,
          )
        : nsxApi.createTransaction(activeCompanyId!, input);
    },
    onSuccess: (tx) => {
      successHaptic();
      qc.invalidateQueries();
      router.replace(`/musteri/${tx.customerId}`);
    },
    onError: (e) => {
      errorHaptic();
      setError(nsxApiErrorMessage(e, isEdit ? "update" : "create"));
    },
  });

  const canSubmit = !!customerId && amount > 0 && !parsedAmount.error && !mut.isPending;

  const title = isEdit
    ? kind === "debt"
      ? "Borç Düzenle"
      : "Tahsilat Düzenle"
    : kind === "debt"
    ? "Borç Ekle"
    : "Tahsilat Al";
  const submitColor = kind === "debt" ? colors.error : colors.success;
  const submitOn = kind === "debt" ? colors.onError : colors.onSuccess;

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="new-tx-modal">
      <View
        style={[
          styles.header,
          { paddingTop: insets.top + 8, borderBottomColor: colors.divider },
        ]}
      >
        <Pressable onPress={() => router.back()} hitSlop={8} testID="new-tx-close">
          <Feather name="x" size={22} color={colors.onSurface} />
        </Pressable>
        <Text style={[styles.title, { color: colors.onSurface }]}>{title}</Text>
        <View style={{ width: 22 }} />
      </View>

      <KeyboardAvoidingView
        style={{ flex: 1 }}
        behavior={Platform.OS === "ios" ? "padding" : undefined}
      >
        <ScrollView
          contentContainerStyle={{ padding: spacing.lg, paddingBottom: 120 }}
          keyboardShouldPersistTaps="handled"
        >
          {/* Kind toggle — soft tint */}
          <View style={[styles.segment, { backgroundColor: colors.surfaceTertiary }]}>
            <Pressable
              testID="tx-kind-debt"
              style={({ pressed }) => [
                styles.segmentBtn,
                {
                  backgroundColor: kind === "debt" ? colors.errorSoft : "transparent",
                  borderColor: kind === "debt" ? colors.error + "55" : "transparent",
                  opacity: pressed ? 0.9 : 1,
                },
              ]}
              onPress={() => setKind("debt")}
            >
              <View
                style={[
                  styles.kindIcon,
                  { backgroundColor: kind === "debt" ? colors.error : colors.surfaceSecondary },
                ]}
              >
                <Feather
                  name="arrow-up-right"
                  size={13}
                  color={kind === "debt" ? colors.onError : colors.muted}
                />
              </View>
              <Text
                style={{
                  color: kind === "debt" ? colors.error : colors.muted,
                  fontFamily: fonts.bodyBold,
                  fontSize: 13,
                }}
              >
                Borç
              </Text>
            </Pressable>
            <Pressable
              testID="tx-kind-collection"
              style={({ pressed }) => [
                styles.segmentBtn,
                {
                  backgroundColor: kind === "collection" ? colors.successSoft : "transparent",
                  borderColor: kind === "collection" ? colors.success + "55" : "transparent",
                  opacity: pressed ? 0.9 : 1,
                },
              ]}
              onPress={() => setKind("collection")}
            >
              <View
                style={[
                  styles.kindIcon,
                  {
                    backgroundColor:
                      kind === "collection" ? colors.success : colors.surfaceSecondary,
                  },
                ]}
              >
                <Feather
                  name="arrow-down-left"
                  size={13}
                  color={kind === "collection" ? colors.onSuccess : colors.muted}
                />
              </View>
              <Text
                style={{
                  color: kind === "collection" ? colors.success : colors.muted,
                  fontFamily: fonts.bodyBold,
                  fontSize: 13,
                }}
              >
                Tahsilat
              </Text>
            </Pressable>
          </View>

          {/* Amount */}
          <View style={{ marginTop: spacing.xl, alignItems: "center" }}>
            <Text style={[styles.amountLabel, { color: colors.muted }]}>Tutar (TL)</Text>
            <TextInput
              testID="tx-amount"
              style={[styles.amountInput, { color: colors.onSurface }]}
              value={amountText}
              onChangeText={setAmountText}
              placeholder="0,00"
              placeholderTextColor={colors.borderStrong}
              keyboardType="decimal-pad"
              maxLength={24}
              accessibilityLabel="Tutar (TL)"
            />
          </View>

          <Text accessibilityLiveRegion="polite" style={{ textAlign: "center", color: parsedAmount.error ? colors.error : colors.muted, marginTop: 8 }}>
            {parsedAmount.error ?? (amount > 0 ? `Kaydedilecek tutar: ${formatTRY(amount)}` : "Örnek: 100,50 veya 100.50")}
          </Text>

          {/* Customer picker */}
          <Text style={[styles.label, { color: colors.muted, marginTop: spacing.xl }]}>MÜŞTERİ</Text>
          <Pressable
            testID="tx-pick-customer"
            onPress={() => setCustomerPickerOpen((v) => !v)}
            style={[
              styles.customerRow,
              { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
            ]}
          >
            {selectedCustomer ? (
              <>
                <View style={[styles.avatar, { backgroundColor: colors.brandTertiary }]}>
                  <Text style={{ color: colors.onBrandTertiary, fontFamily: fonts.bodyBold }}>
                    {initials(selectedCustomer.name)}
                  </Text>
                </View>
                <Text style={[styles.customerName, { color: colors.onSurface }]} numberOfLines={1}>
                  {selectedCustomer.name}
                </Text>
              </>
            ) : (
              <>
                <View style={[styles.avatar, { backgroundColor: colors.surfaceTertiary }]}>
                  <Feather name="user" size={16} color={colors.muted} />
                </View>
                <Text style={[styles.customerName, { color: colors.muted }]}>Müşteri seçin</Text>
              </>
            )}
            <Feather
              name={customerPickerOpen ? "chevron-up" : "chevron-down"}
              size={18}
              color={colors.muted}
            />
          </Pressable>


          <Text style={[styles.label, { color: colors.muted, marginTop: spacing.lg }]}>TARİH & SAAT</Text>
          <DatePickerRow
            valueISO={dateISO}
            onChange={setDateISO}
          />

          <Text style={[styles.label, { color: colors.muted, marginTop: spacing.lg }]}>AÇIKLAMA</Text>
          <TextInput
            testID="tx-description"
            style={[
              styles.input,
              styles.multiline,
              { color: colors.onSurface, backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
            ]}
            value={description}
            onChangeText={setDescription}
            placeholder="Fatura no, açıklama..."
            placeholderTextColor={colors.muted}
            multiline
          />

          {error ? <Text style={[styles.error, { color: colors.error }]} testID="tx-error">{error}</Text> : null}
        </ScrollView>

        <View
          style={[
            styles.footer,
            { paddingBottom: insets.bottom + 12, backgroundColor: colors.surface, borderTopColor: colors.divider },
          ]}
        >
          <Pressable
            testID="tx-submit"
            onPress={() => mut.mutate()}
            disabled={!canSubmit}
            style={({ pressed }) => [
              styles.submit,
              { backgroundColor: submitColor, opacity: !canSubmit ? 0.5 : pressed ? 0.9 : 1 },
            ]}
          >
            {mut.isPending ? (
              <ActivityIndicator color={submitOn} />
            ) : (
              <>
                <Feather name="check" size={18} color={submitOn} />
                <Text style={[styles.submitLabel, { color: submitOn }]}>Kaydet</Text>
              </>
            )}
          </Pressable>
        </View>
      </KeyboardAvoidingView>
      <BottomSheet visible={customerPickerOpen} onDismiss={() => setCustomerPickerOpen(false)} testID="customer-picker-sheet">
            <View
              style={[
                styles.picker,
                { maxHeight: 440 },
                { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
              ]}
            >
              <View style={[styles.pickerSearch, { borderBottomColor: colors.divider }]}>
                <Feather name="search" size={14} color={colors.muted} />
                <TextInput
                  testID="tx-customer-search"
                  style={{ flex: 1, color: colors.onSurface, fontSize: 14 }}
                  value={customerSearch}
                  onChangeText={setCustomerSearch}
                  placeholder="Müşteri ara"
                  placeholderTextColor={colors.muted}
                />
              </View>
              <FlatList
                data={filteredCustomers}
                keyExtractor={(c) => c.id}
                style={{ maxHeight: 220 }}
                keyboardShouldPersistTaps="handled"
                renderItem={({ item }) => (
                  <Pressable
                    testID={`tx-customer-option-${item.id}`}
                    onPress={() => {
                      setCustomerId(item.id);
                      setCustomerPickerOpen(false);
                    }}
                    style={({ pressed }) => [
                      styles.pickerRow,
                      { borderBottomColor: colors.divider, opacity: pressed ? 0.85 : 1 },
                    ]}
                  >
                    <View style={[styles.avatar, { backgroundColor: colors.brandTertiary, width: 32, height: 32 }]}>
                      <Text style={{ color: colors.onBrandTertiary, fontFamily: fonts.bodyBold, fontSize: 12 }}>
                        {initials(item.name)}
                      </Text>
                    </View>
                    <Text style={[styles.pickerRowLabel, { color: colors.onSurface }]} numberOfLines={1}>
                      {item.name}
                    </Text>
                    {customerId === item.id ? (
                      <Feather name="check" size={16} color={colors.brandPrimary} />
                    ) : null}

                  </Pressable>
                )}
                ListEmptyComponent={
                  <View style={{ padding: 16, alignItems: "center" }}>
                    <Text style={{ color: colors.muted, fontSize: 13 }}>
                      Sonuç yok
                    </Text>
                  </View>
                }
              />
            </View>
      </BottomSheet>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: spacing.lg,
    paddingBottom: 12,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  title: { fontFamily: fonts.displayMedium, fontSize: 18, letterSpacing: -0.3 },
  segment: {
    flexDirection: "row",
    padding: 4,
    borderRadius: 14,
    gap: 4,
  },
  segmentBtn: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    paddingVertical: 12,
    borderRadius: 11,
    borderWidth: 1,
  },
  kindIcon: {
    width: 24,
    height: 24,
    borderRadius: 8,
    alignItems: "center",
    justifyContent: "center",
  },
  amountLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    textTransform: "uppercase",
    marginBottom: 8,
  },
  amountInput: {
    fontFamily: fonts.display,
    fontSize: 48,
    letterSpacing: -1.6,
    textAlign: "center",
    minWidth: 160,
    fontVariant: ["tabular-nums"],
  },
  label: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    textTransform: "uppercase",
    marginBottom: 6,
    marginLeft: 4,
  },
  customerRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 12,
    paddingVertical: 12,
    borderRadius: radius.md,
    borderWidth: 1,
  },
  avatar: {
    width: 40,
    height: 40,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
  },
  customerName: { flex: 1, fontFamily: fonts.bodySemiBold, fontSize: 15 },
  picker: {
    borderRadius: radius.md,
    borderWidth: 1,
    marginTop: 8,
    overflow: "hidden",
  },
  pickerSearch: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 12,
    height: 40,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  pickerRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  pickerRowLabel: { flex: 1, fontFamily: fonts.bodyMedium, fontSize: 14 },
  input: {
    height: 50,
    paddingHorizontal: 14,
    borderRadius: radius.md,
    borderWidth: 1,
    fontFamily: fonts.body,
    fontSize: 15,
  },
  multiline: { height: 90, paddingTop: 12, textAlignVertical: "top" },
  error: { fontFamily: fonts.bodyMedium, fontSize: 13, marginTop: 12, textAlign: "center" },
  footer: {
    paddingHorizontal: spacing.lg,
    paddingTop: 12,
    borderTopWidth: StyleSheet.hairlineWidth,
  },
  submit: {
    height: 54,
    borderRadius: 999,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
  },
  submitLabel: { fontFamily: fonts.bodyBold, fontSize: 16 },
  dateRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 14,
    height: 50,
    borderRadius: radius.md,
    borderWidth: 1,
  },
  dateRowLabel: { flex: 1, fontFamily: fonts.bodySemiBold, fontSize: 15 },
});

function formatDateTimeTR(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "-";
  return new Intl.DateTimeFormat("tr-TR", {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(d);
}

interface DatePickerRowProps {
  valueISO: string;
  onChange: (iso: string) => void;
}

function DatePickerRow({ valueISO, onChange }: DatePickerRowProps) {
  const { colors } = useTheme();
  const [showDate, setShowDate] = useState(false);
  const [showTime, setShowTime] = useState(false);
  const value = useMemo(() => new Date(valueISO), [valueISO]);

  const isAndroid = Platform.OS === "android";

  return (
    <>
      <Pressable
        testID="tx-date"
        onPress={() => {
          if (isAndroid) setShowDate(true);
          else setShowDate((v) => !v);
        }}
        style={[
          styles.dateRow,
          { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
        ]}
      >
        <Feather name="calendar" size={16} color={colors.muted} />
        <Text style={[styles.dateRowLabel, { color: colors.onSurface }]} numberOfLines={1}>
          {formatDateTimeTR(valueISO)}
        </Text>
        <Feather name="chevron-right" size={16} color={colors.borderStrong} />
      </Pressable>

      {showDate ? (
        <DateTimePicker
          value={value}
          mode={isAndroid ? "date" : "datetime"}
          display={isAndroid ? "default" : "spinner"}
          onChange={(_, d) => {
            if (isAndroid) setShowDate(false);
            if (d) {
              onChange(d.toISOString());
              if (isAndroid) setShowTime(true);
            }
          }}
        />
      ) : null}
      {isAndroid && showTime ? (
        <DateTimePicker
          value={value}
          mode="time"
          display="default"
          onChange={(_, d) => {
            setShowTime(false);
            if (d) onChange(d.toISOString());
          }}
        />
      ) : null}
    </>
  );
}
