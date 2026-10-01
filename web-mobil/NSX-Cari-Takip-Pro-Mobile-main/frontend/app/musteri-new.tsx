import React, { useEffect, useState } from "react";
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
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter } from "expo-router";
import { nsxApi } from "@/src/api";
import { nsxApiErrorMessage } from "@/src/api/types";
import { useAuth } from "@/src/auth/auth-context";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import { successHaptic, errorHaptic } from "@/src/utils/haptics";

export default function NewCustomerModal() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const router = useRouter();
  const qc = useQueryClient();
  const params = useLocalSearchParams<{ editId?: string }>();
  const { activeCompanyId } = useAuth();
  const isEdit = !!params.editId;

  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [email, setEmail] = useState("");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [prefilled, setPrefilled] = useState(false);

  // Edit modunda: mevcut müşteriyi çek ve formu doldur.
  const customerQ = useQuery({
    queryKey: ["customer", params.editId],
    queryFn: () => nsxApi.getCustomer(params.editId!),
    enabled: isEdit,
  });

  useEffect(() => {
    if (!isEdit || prefilled) return;
    const c = customerQ.data;
    if (!c) return;
    let cancelled = false;
    void Promise.resolve().then(() => {
      if (cancelled) return;
    setName(c.name);
    setPhone(c.phone ?? "");
    setEmail(c.email ?? "");
    setNote(c.note ?? "");
    setPrefilled(true);
    });
    return () => { cancelled = true; };
  }, [customerQ.data, isEdit, prefilled]);

  const mut = useMutation({
    mutationFn: () => {
      const input = {
        name: name.trim(),
        phone: phone.trim() || undefined,
        email: email.trim() || undefined,
        note: note.trim() || undefined,
      };
      return isEdit
        ? nsxApi.updateCustomer(params.editId!, input, customerQ.data?.version)
        : nsxApi.createCustomer(activeCompanyId!, input);
    },
    onSuccess: (result) => {
      successHaptic();
      qc.invalidateQueries();
      // Edit sonrası detaya dön, create sonrası yeni karta git.
      if (isEdit) router.back();
      else router.replace(`/musteri/${result.id}`);
    },
    onError: (e) => {
      errorHaptic();
      setError(nsxApiErrorMessage(e, isEdit ? "update" : "create"));
    },
  });

  const canSubmit = name.trim().length > 1 && !mut.isPending && (!isEdit || prefilled);

  if (isEdit && customerQ.isPending) {
    return (
      <View style={[styles.root, styles.center, { backgroundColor: colors.surface }]}>
        <ActivityIndicator color={colors.brandPrimary} />
        <Text style={{ color: colors.muted, marginTop: 12, fontFamily: fonts.body, fontSize: 13 }}>
          Müşteri yükleniyor…
        </Text>
      </View>
    );
  }

  if (isEdit && !customerQ.isPending && !customerQ.data) {
    return (
      <View style={[styles.root, styles.center, { backgroundColor: colors.surface }]}>
        <Feather name="alert-circle" size={24} color={colors.error} />
        <Text style={{ color: colors.error, marginTop: 12, fontFamily: fonts.bodyMedium, fontSize: 14 }}>
          Müşteri bulunamadı.
        </Text>
        <Pressable onPress={() => router.back()} style={{ marginTop: 16 }}>
          <Text style={{ color: colors.brandPrimary, fontFamily: fonts.bodyBold }}>Geri Dön</Text>
        </Pressable>
      </View>
    );
  }

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="new-customer-modal">
      <View
        style={[
          styles.header,
          { paddingTop: insets.top + 8, borderBottomColor: colors.divider },
        ]}
      >
        <Pressable onPress={() => router.back()} hitSlop={8} testID="new-customer-close">
          <Feather name="x" size={22} color={colors.onSurface} />
        </Pressable>
        <Text style={[styles.title, { color: colors.onSurface }]}>
          {isEdit ? "Müşteriyi Düzenle" : "Yeni Müşteri"}
        </Text>
        <View style={{ width: 22 }} />
      </View>

      <KeyboardAvoidingView
        style={{ flex: 1 }}
        behavior={Platform.OS === "ios" ? "padding" : undefined}
      >
        <ScrollView
          contentContainerStyle={{ padding: spacing.lg, paddingBottom: 100 }}
          keyboardShouldPersistTaps="handled"
        >
          <View
            style={[
              styles.tip,
              { backgroundColor: colors.brandTertiary, borderColor: colors.brandPrimary + "33" },
            ]}
          >
            <View style={[styles.tipIcon, { backgroundColor: colors.brandPrimary }]}>
              <Feather name="user-plus" size={14} color={colors.onBrandPrimary} />
            </View>
            <Text style={[styles.tipText, { color: colors.onBrandTertiary }]}>
              {isEdit
                ? "İletişim bilgilerini güncelle; cari bakiyesi etkilenmez."
                : "Ad zorunlu. Telefon ve e-posta sonra da eklenebilir."}
            </Text>
          </View>

          <Field label="Müşteri Adı *">
            <TextInput
              testID="new-customer-name"
              style={[styles.input, { color: colors.onSurface, backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}
              value={name}
              onChangeText={setName}
              placeholder="Örn: Yılmaz Metal"
              placeholderTextColor={colors.muted}
              autoFocus={!isEdit}
            />
          </Field>
          <Field label="Telefon">
            <TextInput
              testID="new-customer-phone"
              style={[styles.input, { color: colors.onSurface, backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}
              value={phone}
              onChangeText={setPhone}
              placeholder="0555 000 00 00"
              placeholderTextColor={colors.muted}
              keyboardType="phone-pad"
            />
          </Field>
          <Field label="E-posta">
            <TextInput
              testID="new-customer-email"
              style={[styles.input, { color: colors.onSurface, backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}
              value={email}
              onChangeText={setEmail}
              placeholder="ornek@firma.com"
              placeholderTextColor={colors.muted}
              keyboardType="email-address"
              autoCapitalize="none"
            />
          </Field>
          <Field label="Not">
            <TextInput
              testID="new-customer-note"
              style={[
                styles.input,
                styles.multiline,
                { color: colors.onSurface, backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
              ]}
              value={note}
              onChangeText={setNote}
              placeholder="Ödeme koşulu, adres vb."
              placeholderTextColor={colors.muted}
              multiline
            />
          </Field>
          {error ? <Text style={[styles.error, { color: colors.error }]} testID="new-customer-error">{error}</Text> : null}
        </ScrollView>

        <View
          style={[
            styles.footer,
            { paddingBottom: insets.bottom + 12, backgroundColor: colors.surface, borderTopColor: colors.divider },
          ]}
        >
          <Pressable
            testID="new-customer-submit"
            onPress={() => mut.mutate()}
            disabled={!canSubmit}
            style={({ pressed }) => [
              styles.submit,
              {
                backgroundColor: colors.brandPrimary,
                opacity: !canSubmit ? 0.5 : pressed ? 0.9 : 1,
              },
            ]}
          >
            {mut.isPending ? (
              <ActivityIndicator color={colors.onBrandPrimary} />
            ) : (
              <>
                <Feather name="check" size={18} color={colors.onBrandPrimary} />
                <Text style={[styles.submitLabel, { color: colors.onBrandPrimary }]}>
                  {isEdit ? "Değişiklikleri Kaydet" : "Müşteriyi Kaydet"}
                </Text>
              </>
            )}
          </Pressable>
        </View>
      </KeyboardAvoidingView>
    </View>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  const { colors } = useTheme();
  return (
    <View style={{ marginBottom: spacing.md }}>
      <Text style={[styles.label, { color: colors.muted }]}>{label}</Text>
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  center: { alignItems: "center", justifyContent: "center" },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: spacing.lg,
    paddingBottom: 12,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  title: { fontFamily: fonts.displayMedium, fontSize: 18, letterSpacing: -0.3 },
  tip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    padding: 12,
    borderRadius: radius.md,
    borderWidth: 1,
    marginBottom: spacing.lg,
  },
  tipIcon: {
    width: 28,
    height: 28,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
  },
  tipText: { flex: 1, fontFamily: fonts.bodyMedium, fontSize: 13, lineHeight: 18 },
  label: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    textTransform: "uppercase",
    marginBottom: 6,
    marginLeft: 4,
  },
  input: {
    height: 50,
    paddingHorizontal: 14,
    borderRadius: radius.md,
    borderWidth: 1,
    fontFamily: fonts.body,
    fontSize: 15,
  },
  multiline: { height: 96, paddingTop: 12, textAlignVertical: "top" },
  error: { fontFamily: fonts.bodyMedium, fontSize: 13, marginTop: 8 },
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
});
