// Bildirim detayı — okundu işaretle + sil.
import React, { useCallback, useEffect, useState } from "react";
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  Pressable,
  Alert,
  ActivityIndicator,
} from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { ScreenHeader } from "@/src/components/screen-header";
import { BrandAppIcon } from "@/src/components/brand-app-icon";
import { useTheme, spacing, fonts, radius } from "@/src/theme";
import { selectionHaptic } from "@/src/utils/haptics";
import { traceNotificationTap } from "@/src/notifications/notification-debug";
import {
  categoryLabel,
  deleteInboxItem,
  formatInboxTime,
  getInboxItem,
  markInboxItemRead,
  type InboxItem,
} from "@/src/notifications/inbox";

function formatFullTime(iso: string): string {
  const d = new Date(iso);
  if (!Number.isFinite(d.getTime())) return formatInboxTime(iso);
  try {
    return d.toLocaleString("tr-TR", {
      day: "2-digit",
      month: "long",
      year: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return formatInboxTime(iso);
  }
}

export default function NotificationDetailScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const { colors, scheme } = useTheme();
  const [item, setItem] = useState<InboxItem | null>(null);
  const [loading, setLoading] = useState(true);
  const [deleting, setDeleting] = useState(false);

  const logoVariant = scheme === "dark" ? "dark" : "main";

  const load = useCallback(async () => {
    if (!id) {
      setLoading(false);
      return;
    }
    setLoading(true);
    traceNotificationTap("detail_loading", { id });
    const found = await getInboxItem(id);
    traceNotificationTap("detail_loaded", { id, found: !!found, category: found?.category });
    setItem(found);
    setLoading(false);
    if (found && !found.read) void markInboxItemRead(found.id).catch(() => Alert.alert("Bildirim", "Okundu bilgisi kaydedilemedi. Bağlantınızı kontrol edin."));
  }, [id]);

  useEffect(() => {
    void Promise.resolve().then(load).catch(() => {
      setLoading(false);
      Alert.alert("Bildirim açılamadı", "Bildirim okunamadı. Lütfen yeniden deneyin.");
    });
  }, [load]);

  const confirmDelete = () => {
    if (!item || deleting) return;
    selectionHaptic();
    Alert.alert("Bildirimi sil", "Bu bildirim kutudan kaldırılacak.", [
      { text: "Vazgeç", style: "cancel" },
      {
        text: "Sil",
        style: "destructive",
        onPress: () => {
          void (async () => {
            setDeleting(true);
            try {
              await deleteInboxItem(item.id);
              router.back();
            } catch { Alert.alert("Silinemedi", "Bildirim silinemedi. Bağlantınızı kontrol edip tekrar deneyin."); }
            finally { setDeleting(false); }
          })();
        },
      },
    ]);
  };

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="notif-detail-screen">
      <ScreenHeader
        title="Bildirim"
        right={
          item ? (
            <Pressable
              onPress={confirmDelete}
              disabled={deleting}
              hitSlop={10}
              testID="notif-detail-delete"
              style={({ pressed }) => [{ opacity: deleting ? 0.4 : pressed ? 0.7 : 1 }]}
            >
              <Feather name="trash-2" size={20} color={colors.error} />
            </Pressable>
          ) : null
        }
      />

      {loading ? (
        <View style={styles.center}>
          <ActivityIndicator color={colors.brandPrimary} />
        </View>
      ) : !item ? (
        <View style={styles.center}>
          <Feather name="bell-off" size={28} color={colors.muted} />
          <Text style={[styles.emptyTitle, { color: colors.onSurface }]}>Bildirim bulunamadı</Text>
          <Text style={[styles.emptyBody, { color: colors.muted }]}>
            Silinmiş veya senkron dışı olabilir.
          </Text>
          <Pressable
            onPress={() => router.back()}
            style={[styles.backBtn, { backgroundColor: colors.brandPrimary }]}
          >
            <Text style={[styles.backBtnText, { color: colors.onBrandPrimary }]}>Geri dön</Text>
          </Pressable>
        </View>
      ) : (
        <ScrollView
          contentContainerStyle={{
            paddingHorizontal: spacing.lg,
            paddingTop: spacing.lg,
            paddingBottom: insets.bottom + 40,
            gap: spacing.md,
          }}
          showsVerticalScrollIndicator={false}
        >
          <View
            style={[
              styles.hero,
              {
                backgroundColor: colors.surfaceSecondary,
                borderColor: colors.brandPrimary + "28",
              },
            ]}
          >
            <View
              style={[
                styles.logoChip,
                { backgroundColor: colors.brandTertiary, borderColor: colors.brandPrimary + "22" },
              ]}
            >
              <BrandAppIcon size={48} variant={logoVariant} surface={colors.brandTertiary} />
            </View>
            <View style={[styles.chip, { backgroundColor: colors.brandTertiary }]}>
              <Text style={[styles.chipText, { color: colors.brandPrimary }]}>
                {categoryLabel(item.category)}
              </Text>
            </View>
            <Text style={[styles.title, { color: colors.onSurface }]}>{item.title}</Text>
            <Text style={[styles.when, { color: colors.muted }]}>{formatFullTime(item.createdAt)}</Text>
          </View>

          <View
            style={[
              styles.bodyCard,
              { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
            ]}
          >
            <Text style={[styles.bodyLabel, { color: colors.muted }]}>İÇERİK</Text>
            <Text style={[styles.body, { color: colors.onSurface }]}>{item.body}</Text>
          </View>

          <Pressable
            testID="notif-detail-delete-btn"
            onPress={confirmDelete}
            disabled={deleting}
            style={({ pressed }) => [
              styles.deleteRow,
              {
                backgroundColor: colors.errorSoft,
                borderColor: colors.error + "33",
                opacity: deleting ? 0.55 : pressed ? 0.9 : 1,
              },
            ]}
          >
            <Feather name="trash-2" size={18} color={colors.error} />
            <Text style={[styles.deleteLabel, { color: colors.error }]}>
              {deleting ? "Siliniyor…" : "Bildirimi sil"}
            </Text>
          </Pressable>
        </ScrollView>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  center: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: spacing.xl,
    gap: 10,
  },
  emptyTitle: { fontFamily: fonts.displayMedium, fontSize: 17, marginTop: 8 },
  emptyBody: { fontFamily: fonts.body, fontSize: 13, textAlign: "center" },
  backBtn: {
    marginTop: 12,
    paddingHorizontal: 18,
    paddingVertical: 10,
    borderRadius: radius.md,
  },
  backBtnText: { fontFamily: fonts.bodySemiBold, fontSize: 14 },
  hero: {
    borderRadius: radius.lg,
    borderWidth: 1,
    padding: spacing.lg,
    alignItems: "flex-start",
    gap: 10,
  },
  logoChip: {
    width: 64,
    height: 64,
    borderRadius: 18,
    borderWidth: 1,
    alignItems: "center",
    justifyContent: "center",
  },
  chip: {
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 999,
  },
  chipText: { fontFamily: fonts.bodySemiBold, fontSize: 11, letterSpacing: 0.3 },
  title: { fontFamily: fonts.displayMedium, fontSize: 22, letterSpacing: -0.4 },
  when: { fontFamily: fonts.body, fontSize: 13 },
  bodyCard: {
    borderRadius: radius.lg,
    borderWidth: StyleSheet.hairlineWidth,
    padding: spacing.lg,
    gap: 8,
  },
  bodyLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.6,
  },
  body: { fontFamily: fonts.body, fontSize: 15, lineHeight: 22 },
  deleteRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    paddingVertical: 14,
    borderRadius: radius.lg,
    borderWidth: 1,
  },
  deleteLabel: { fontFamily: fonts.bodySemiBold, fontSize: 15 },
});
