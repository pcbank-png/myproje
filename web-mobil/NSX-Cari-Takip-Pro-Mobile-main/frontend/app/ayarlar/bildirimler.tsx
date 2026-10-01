// Bildirim Ayarları — soft çerçeve: solda NSX logo, sağda metin.
import React, { useEffect, useState } from "react";
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  Switch,
  Pressable,
  Alert,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { ScreenHeader } from "@/src/components/screen-header";
import { BrandAppIcon } from "@/src/components/brand-app-icon";
import { useTheme, spacing, fonts } from "@/src/theme";
import { selectionHaptic } from "@/src/utils/haptics";
import { useRouter } from "expo-router";
import { Swipeable } from "react-native-gesture-handler";
import {
  useNotificationInbox,
  formatInboxTime,
  clearInbox,
  deleteInboxItem,
  type InboxItem,
} from "@/src/notifications/inbox";
import {
  registerPushTokenWithServer,
  type PushRegistrationResult,
} from "@/src/notifications/push-register";
import {
  DEFAULT_NOTIFICATION_PREFS,
  hydrateNotificationPrefs,
  syncNotificationPrefsToServer,
  writeLocalNotificationPrefs,
} from "@/src/notifications/notification-prefs";
import type { NotificationPrefs } from "@/src/api/types";
import { isMockMode } from "@/src/api";

type Tone = "brand" | "success" | "error" | "warning";

interface Pref {
  key: string;
  label: string;
  hint: string;
  tone: Tone;
  defaultValue: boolean;
}

const MASTER: Pref = {
  key: "master",
  label: "Bildirimleri Aç",
  hint: "Tüm bildirim türlerini tek dokunuşla aç/kapat",
  tone: "brand",
  defaultValue: true,
};

const CHANNELS: Pref[] = [
  {
    key: "reminders",
    label: "Hatırlatmalar",
    hint: "Vadesi yaklaşan veya ödenmeyen kayıtlar için bildirim",
    tone: "warning",
    defaultValue: true,
  },
  {
    key: "collections",
    label: "Tahsilatlar",
    hint: "Yeni tahsilat kaydı oluşturulunca haber ver",
    tone: "success",
    defaultValue: true,
  },
  {
    key: "debts",
    label: "Borçlar",
    hint: "Yeni borç kaydı oluşturulunca haber ver",
    tone: "error",
    defaultValue: true,
  },
  {
    key: "system",
    label: "Sistem & Duyurular",
    hint: "Sürüm güncellemesi ve önemli duyurular",
    tone: "brand",
    defaultValue: true,
  },
];



function prefsToValues(prefs: NotificationPrefs): Record<string, boolean> {
  return {
    master: prefs.master,
    reminders: prefs.reminders,
    collections: prefs.collections,
    debts: prefs.debts,
    system: prefs.system,
  };
}

function valuesToPrefs(values: Record<string, boolean>): NotificationPrefs {
  return {
    master: values.master ?? DEFAULT_NOTIFICATION_PREFS.master,
    reminders: values.reminders ?? DEFAULT_NOTIFICATION_PREFS.reminders,
    collections: values.collections ?? DEFAULT_NOTIFICATION_PREFS.collections,
    debts: values.debts ?? DEFAULT_NOTIFICATION_PREFS.debts,
    system: values.system ?? DEFAULT_NOTIFICATION_PREFS.system,
  };
}

function useToneColors(tone: Tone) {
  const { colors } = useTheme();
  switch (tone) {
    case "success":
      return { fg: colors.success, soft: colors.successSoft, border: colors.success + "33" };
    case "error":
      return { fg: colors.error, soft: colors.errorSoft, border: colors.error + "33" };
    case "warning":
      return { fg: colors.warning, soft: colors.warningSoft, border: colors.warning + "33" };
    default:
      return {
        fg: colors.brandPrimary,
        soft: colors.brandTertiary,
        border: colors.brandPrimary + "33",
      };
  }
}

export default function NotificationsScreen() {
  const insets = useSafeAreaInsets();
  const router = useRouter();
  const { colors, scheme } = useTheme();
  const [values, setValues] = useState<Record<string, boolean>>(() =>
    prefsToValues(DEFAULT_NOTIFICATION_PREFS),
  );
  const [hydrated, setHydrated] = useState(false);

  useEffect(() => {
    void (async () => {
      try {
        const prefs = await hydrateNotificationPrefs();
        setValues(prefsToValues(prefs));
      } catch {
        // yok say
      } finally {
        setHydrated(true);
      }
    })();
  }, []);

  const toggle = (key: string) => {
    setValues((prev) => {
      const next = { ...prev, [key]: !prev[key] };
      selectionHaptic();
      const prefs = valuesToPrefs(next);
      void writeLocalNotificationPrefs(prefs);
      void syncNotificationPrefsToServer(prefs);
      return next;
    });
  };

  const master = values.master;
  const activeCount = CHANNELS.filter((c) => values[c.key] && master).length;
  const logoVariant = scheme === "dark" ? "dark" : "main";
  const { items: inbox, unreadCount, markRead } = useNotificationInbox();
  const [pushReg, setPushReg] = useState<PushRegistrationResult | null>(null);
  const [pushBusy, setPushBusy] = useState(false);

  useEffect(() => {
    void registerPushTokenWithServer().then(setPushReg);
  }, []);

  const markAllInboxRead = () => {
    selectionHaptic();
    void markRead().catch(() => Alert.alert("Kaydedilemedi", "Okundu bilgisi kaydedilemedi. Tekrar deneyin."));
  };

  const confirmClearInbox = () => {
    if (inbox.length === 0) return;
    selectionHaptic();
    Alert.alert(
      "Tümünü sil",
      `${inbox.length} bildirim kalıcı olarak silinecek.`,
      [
        { text: "Vazgeç", style: "cancel" },
        {
          text: "Tümünü sil",
          style: "destructive",
          onPress: () => {
            void clearInbox().catch(() => Alert.alert("Silinemedi", "Bildirimler silinemedi. Bağlantınızı kontrol edin."));
          },
        },
      ],
    );
  };

  const removeOne = (id: string) => {
    selectionHaptic();
    void deleteInboxItem(id).catch(() => Alert.alert("Silinemedi", "Bildirim silinemedi. Tekrar deneyin."));
  };

  const enablePush = async () => {
    if (pushBusy) return;
    setPushBusy(true);
    selectionHaptic();
    const res = await registerPushTokenWithServer();
    setPushReg(res);
    setPushBusy(false);
  };

  const pushActive = pushReg?.status === "registered" || pushReg?.status === "mock";
  const pushPill = pushActive
    ? { label: pushReg?.status === "mock" ? "DEMO" : "AKTİF", bg: colors.successSoft, fg: colors.success }
    : pushReg?.status === "denied"
      ? { label: "İZİN YOK", bg: colors.errorSoft, fg: colors.error }
      : pushReg?.status === "error"
        ? { label: "HATA", bg: colors.errorSoft, fg: colors.error }
        : pushReg?.status === "no_token"
          ? { label: "KURULUM", bg: colors.warningSoft, fg: colors.warning }
          : { label: "BEKLEMEDE", bg: colors.warningSoft, fg: colors.warning };

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="notifications-screen">
      <ScreenHeader title="Bildirim Ayarları" />
      <ScrollView
        contentContainerStyle={{
          paddingHorizontal: spacing.lg,
          paddingBottom: insets.bottom + 36,
          paddingTop: spacing.md,
          gap: 10,
        }}
        showsVerticalScrollIndicator={false}
      >
        {/* Soft info frame — logo + metin + kırmızı sayı */}
        <View
          style={[
            styles.softFrame,
            {
              backgroundColor: colors.surfaceSecondary,
              borderColor: colors.brandPrimary + "28",
            },
          ]}
          testID="notif-pending-banner"
        >
          <View
            style={[
              styles.logoChip,
              { backgroundColor: colors.brandTertiary, borderColor: colors.brandPrimary + "22" },
            ]}
          >
            <BrandAppIcon size={40} variant={logoVariant} surface={colors.brandTertiary} />
          </View>
          <View style={styles.textCol}>
            <View style={styles.titleRow}>
              <Text style={[styles.frameTitle, { color: colors.onSurface }]}>Gelen bildirimler</Text>
              {inbox.length > 0 ? (
                <View
                  style={[styles.countBadge, { backgroundColor: colors.error }]}
                  testID="notif-inbox-count"
                >
                  <Text style={[styles.countBadgeText, { color: colors.onError }]}>
                    {inbox.length > 99 ? "99+" : String(inbox.length)}
                  </Text>
                </View>
              ) : null}
              <View style={[styles.statusPill, { backgroundColor: pushPill.bg }]}>
                <Text style={[styles.statusPillText, { color: pushPill.fg }]}>{pushPill.label}</Text>
              </View>
            </View>
            <Text style={[styles.frameBody, { color: colors.muted }]}>
              {pushActive
                ? pushReg?.status === "mock"
                  ? "Demo modunda token kaydı simüle edildi. Canlı push için development build + live API gerekir."
                  : "Push aktif. Tahsilat, borç ve hatırlatmalar (kanal tercihlerine göre) bu cihaza iletilir."
                : pushReg?.message ??
                  (isMockMode
                    ? "Demo kutusu aktif. Gerçek push için live mod ve telefon build’i kullanın."
                    : "Bildirim izni verin; Expo push token sunucuya kaydedilir.")}
            </Text>
            {!pushActive && !isMockMode ? (
              <Pressable
                testID="notif-enable-push"
                onPress={enablePush}
                disabled={pushBusy || pushReg?.status === "no_token"}
                style={({ pressed }) => [
                  styles.enablePushBtn,
                  {
                    backgroundColor: colors.brandPrimary,
                    opacity: pushBusy ? 0.6 : pressed ? 0.92 : 1,
                  },
                ]}
              >
                <Feather name="bell" size={14} color={colors.onBrandPrimary} />
                <Text style={[styles.enablePushLabel, { color: colors.onBrandPrimary }]}>
                  {pushBusy
                    ? "Kaydediliyor…"
                    : pushReg?.status === "no_token"
                      ? "Önce EAS projectId (aşağıdaki metne bakın)"
                      : "Push bildirimlerini etkinleştir"}
                </Text>
              </Pressable>
            ) : null}
          </View>
        </View>

        {inbox.length > 0 ? (
          <>
            <View style={styles.kutuHeader}>
              <Text style={[styles.sectionTitle, { color: colors.muted, marginBottom: 0 }]}>
                KUTU
              </Text>
              <View style={styles.kutuActions}>
                {unreadCount > 0 ? (
                  <Pressable onPress={markAllInboxRead} testID="inbox-mark-all-read">
                    <Text style={[styles.markAllRead, { color: colors.brandPrimary }]}>
                      Okundu ({unreadCount})
                    </Text>
                  </Pressable>
                ) : null}
                <Pressable onPress={confirmClearInbox} testID="inbox-clear-all" hitSlop={8}>
                  <Text style={[styles.markAllRead, { color: colors.error }]}>Tümünü sil</Text>
                </Pressable>
              </View>
            </View>
            <Text style={[styles.swipeHint, { color: colors.muted }]}>
              Detay için dokun · Silmek için sola kaydır
            </Text>
            {inbox.map((item) => (
              <InboxRow
                key={item.id}
                item={item}
                logoVariant={logoVariant}
                onOpen={() => router.push(`/ayarlar/bildirim/${item.id}`)}
                onDelete={() => removeOne(item.id)}
              />
            ))}
          </>
        ) : (
          <View
            style={[
              styles.emptyKutu,
              { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
            ]}
          >
            <Feather name="inbox" size={22} color={colors.muted} />
            <Text style={[styles.emptyKutuTitle, { color: colors.onSurface }]}>Kutu boş</Text>
            <Text style={[styles.emptyKutuBody, { color: colors.muted }]}>
              Yeni borç, tahsilat ve hatırlatmalar burada listelenir.
            </Text>
          </View>
        )}

        <Text style={[styles.sectionTitle, { color: colors.muted }]}>GENEL</Text>

        <NotifFrame
          logoVariant={logoVariant}
          title={MASTER.label}
          body={
            hydrated
              ? master
                ? `${MASTER.hint} · ${activeCount}/${CHANNELS.length} kanal açık`
                : `${MASTER.hint} · Tümü kapalı`
              : MASTER.hint
          }
          tone="brand"
          on={!!master}
          onToggle={() => toggle("master")}
          testID="notif-row-master"
        />

        <Text style={[styles.sectionTitle, { color: colors.muted, marginTop: 8 }]}>KANALLAR</Text>

        {CHANNELS.map((p) => {
          const disabled = !master;
          const on = !!values[p.key] && master;
          return (
            <NotifFrame
              key={p.key}
              logoVariant={logoVariant}
              title={p.label}
              body={p.hint}
              tone={p.tone}
              on={on}
              disabled={disabled}
              onToggle={() => !disabled && toggle(p.key)}
              testID={`notif-row-${p.key}`}
            />
          );
        })}

        {!master ? (
          <View
            style={[
              styles.softFrame,
              { backgroundColor: colors.surfaceTertiary, borderColor: colors.border },
            ]}
          >
            <View
              style={[
                styles.logoChip,
                { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
              ]}
            >
              <Feather name="bell-off" size={18} color={colors.muted} />
            </View>
            <Text style={[styles.frameBody, { color: colors.muted, flex: 1 }]}>
              Ana anahtar kapalıyken kanallar sessize alınır. Tercihler korunur.
            </Text>
          </View>
        ) : null}

        <View style={styles.footerNote}>
          <Feather name="smartphone" size={12} color={colors.muted} />
          <Text style={[styles.footerNoteText, { color: colors.muted }]}>
            Tercihler bu cihaz için sunucuya kaydedilir.
          </Text>
        </View>
      </ScrollView>
    </View>
  );
}

function InboxRow({
  item,
  logoVariant,
  onOpen,
  onDelete,
}: {
  item: InboxItem;
  logoVariant: "main" | "dark" | "light";
  onOpen: () => void;
  onDelete: () => void;
}) {
  const { colors } = useTheme();

  const renderRight = () => (
    <Pressable
      onPress={onDelete}
      testID={`inbox-swipe-delete-${item.id}`}
      style={[styles.swipeDelete, { backgroundColor: colors.error }]}
    >
      <Feather name="trash-2" size={18} color={colors.onError} />
      <Text style={[styles.swipeDeleteText, { color: colors.onError }]}>Sil</Text>
    </Pressable>
  );

  return (
    <Swipeable
      overshootRight={false}
      friction={2}
      rightThreshold={40}
      renderRightActions={renderRight}
    >
      <Pressable
        onPress={onOpen}
        style={({ pressed }) => [
          styles.softFrame,
          {
            backgroundColor: item.read ? colors.surfaceSecondary : colors.brandTertiary,
            borderColor: item.read ? colors.border : colors.brandPrimary + "33",
            opacity: pressed ? 0.94 : 1,
          },
        ]}
        testID={`inbox-item-${item.id}`}
      >
        <View
          style={[
            styles.logoChip,
            {
              backgroundColor: colors.surfaceSecondary,
              borderColor: colors.border,
            },
          ]}
        >
          <BrandAppIcon size={40} variant={logoVariant} surface={colors.surfaceSecondary} />
        </View>
        <View style={styles.textCol}>
          <View style={styles.titleRow}>
            <Text
              style={[styles.frameTitle, { color: colors.onSurface, flex: 1 }]}
              numberOfLines={1}
            >
              {item.title}
            </Text>
            <Text style={[styles.timeLabel, { color: colors.muted }]}>
              {formatInboxTime(item.createdAt)}
            </Text>
          </View>
          <Text style={[styles.frameBody, { color: colors.muted }]} numberOfLines={2}>
            {item.body}
          </Text>
        </View>
        <Feather name="chevron-right" size={16} color={colors.muted} />
      </Pressable>
    </Swipeable>
  );
}

function NotifFrame({
  logoVariant,
  title,
  body,
  tone,
  on,
  disabled,
  onToggle,
  testID,
}: {
  logoVariant: "main" | "dark" | "light";
  title: string;
  body: string;
  tone: Tone;
  on: boolean;
  disabled?: boolean;
  onToggle: () => void;
  testID: string;
}) {
  const { colors } = useTheme();
  const toneColors = useToneColors(tone);

  return (
    <Pressable
      onPress={onToggle}
      disabled={disabled}
      style={({ pressed }) => [
        styles.softFrame,
        {
          backgroundColor: on ? toneColors.soft : colors.surfaceSecondary,
          borderColor: on ? toneColors.border : colors.border,
          opacity: disabled ? 0.45 : pressed ? 0.94 : 1,
        },
      ]}
      testID={testID}
    >
      <View
        style={[
          styles.logoChip,
          {
            backgroundColor: on ? colors.surfaceSecondary : colors.surfaceTertiary,
            borderColor: on ? toneColors.border : colors.border,
          },
        ]}
      >
        <BrandAppIcon
          size={40}
          variant={logoVariant}
          surface={on ? colors.surfaceSecondary : colors.surfaceTertiary}
        />
      </View>
      <View style={styles.textCol}>
        <Text style={[styles.frameTitle, { color: colors.onSurface }]} numberOfLines={1}>
          {title}
        </Text>
        <Text style={[styles.frameBody, { color: colors.muted }]} numberOfLines={2}>
          {body}
        </Text>
      </View>
      <Switch
        testID={testID.replace("row", "toggle")}
        value={on}
        onValueChange={onToggle}
        disabled={disabled}
        trackColor={{ false: colors.borderStrong, true: toneColors.fg }}
        thumbColor="#FFFFFF"
        ios_backgroundColor={colors.borderStrong}
      />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  sectionTitle: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.6,
    marginLeft: 4,
    marginBottom: 2,
    marginTop: 6,
  },
  kutuHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginTop: 6,
    marginBottom: 2,
    paddingHorizontal: 4,
  },
  kutuActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: 14,
  },
  markAllRead: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 12,
  },
  swipeHint: {
    fontFamily: fonts.body,
    fontSize: 11,
    marginLeft: 4,
    marginBottom: 6,
  },
  swipeDelete: {
    width: 76,
    marginLeft: 8,
    borderRadius: 18,
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
  },
  swipeDeleteText: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
  },
  emptyKutu: {
    borderRadius: 18,
    borderWidth: 1,
    paddingVertical: 28,
    paddingHorizontal: 20,
    alignItems: "center",
    gap: 6,
    marginTop: 4,
  },
  emptyKutuTitle: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 15,
    marginTop: 4,
  },
  emptyKutuBody: {
    fontFamily: fonts.body,
    fontSize: 12,
    textAlign: "center",
    lineHeight: 18,
  },
  softFrame: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingVertical: 12,
    paddingHorizontal: 12,
    borderRadius: 18,
    borderWidth: 1,
  },
  logoChip: {
    width: 48,
    height: 48,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
    overflow: "hidden",
  },
  textCol: {
    flex: 1,
    gap: 6,
    paddingRight: 4,
  },
  titleRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    flexWrap: "wrap",
  },
  frameTitle: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 15,
    letterSpacing: -0.2,
  },
  frameBody: {
    fontFamily: fonts.body,
    fontSize: 12,
    lineHeight: 17,
  },
  statusPill: {
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: 999,
  },
  statusPillText: {
    fontFamily: fonts.bodyBold,
    fontSize: 10,
    letterSpacing: 0.5,
  },
  countBadge: {
    minWidth: 20,
    height: 20,
    borderRadius: 10,
    paddingHorizontal: 6,
    alignItems: "center",
    justifyContent: "center",
  },
  countBadgeText: {
    fontFamily: fonts.bodyBold,
    fontSize: 11,
    lineHeight: 13,
  },
  timeLabel: {
    fontFamily: fonts.bodyMedium,
    fontSize: 11,
  },
  enablePushBtn: {
    flexDirection: "row",
    alignItems: "center",
    alignSelf: "flex-start",
    gap: 8,
    paddingHorizontal: 14,
    height: 36,
    borderRadius: 999,
    marginTop: 4,
  },
  enablePushLabel: {
    fontFamily: fonts.bodyBold,
    fontSize: 12,
  },
  footerNote: {
    marginTop: spacing.lg,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
  },
  footerNoteText: {
    fontFamily: fonts.body,
    fontSize: 11,
  },
});
