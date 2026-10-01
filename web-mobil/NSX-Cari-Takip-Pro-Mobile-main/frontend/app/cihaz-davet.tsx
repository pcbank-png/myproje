// Cihaz Davet Et — mobil erişim için ikinci cihazı bu firma/tenant
// hesabına bağlayacak, tek kullanımlık ve kısa süreli eşleştirme
// QR/kodunu backend'den alır.
//
// GÜVENLİK:
// - Mevcut oturum QR'ı, access token, refresh token veya SecureStore
//   verisi ASLA paylaşılmaz.
// - Her davet için sunucudan yeni, tek kullanımlık pairing token
//   `POST /api/mobile/pair/invite` ile alınır. Client tarafında sahte
//   üretim yoktur.

import React, { useEffect, useMemo, useRef, useState } from "react";
import {
  View,
  Text,
  StyleSheet,
  ActivityIndicator,
  Image,
  Pressable,
  Share,
  ScrollView,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import QRCode from "react-native-qrcode-svg";
import * as Sharing from "expo-sharing";
import { captureRef } from "react-native-view-shot";
import { useMutation } from "@tanstack/react-query";
import { ScreenHeader } from "@/src/components/screen-header";
import { useTheme, spacing, radius, fonts } from "@/src/theme";
import { useAuth } from "@/src/auth/auth-context";
import { nsxApi } from "@/src/api";
import { ErrorState } from "@/src/components/empty-state";
import { nsxApiErrorMessage, type InviteResult } from "@/src/api/types";
import { selectionHaptic, successHaptic, errorHaptic } from "@/src/utils/haptics";

export default function InviteDeviceScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const { session, activeCompanyId, companies } = useAuth();

  const [invite, setInvite] = useState<InviteResult | null>(null);
  const [remainingMs, setRemainingMs] = useState(0);
  const [shareBusy, setShareBusy] = useState(false);
  const shareCardRef = useRef<View>(null);

  const activeCompany =
    companies.find((c) => c.id === activeCompanyId)?.name ??
    session?.companyName ??
    "NSX Cari";

  const inviteMut = useMutation({
    mutationFn: () => nsxApi.createInvite(activeCompanyId ?? ""),
    onSuccess: (res) => {
      successHaptic();
      setInvite(res);
    },
    onError: () => errorHaptic(),
  });

  // Ekran açıldığında ilk daveti al.
  useEffect(() => {
    if (session) inviteMut.mutate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session?.tenantId]);

  // Kalan süre sayacı — her saniyede güncellenir.
  useEffect(() => {
    if (!invite) return;
    const target = Date.parse(invite.expiresAt);
    if (!Number.isFinite(target)) {
      const reset = setTimeout(() => setRemainingMs(0), 0);
      return () => clearTimeout(reset);
    }
    const tick = () => setRemainingMs(Math.max(0, target - Date.now()));
    const firstTick = setTimeout(tick, 0);
    const i = setInterval(tick, 1000);
    return () => { clearTimeout(firstTick); clearInterval(i); };
  }, [invite]);

  const generateInvite = () => {
    if (!session || inviteMut.isPending) return;
    inviteMut.mutate();
  };

  const shareInvite = async () => {
    if (!invite || expired || shareBusy) return;
    selectionHaptic();
    setShareBusy(true);

    const remainingMinutes = Math.max(1, Math.ceil(remainingMs / 60_000));
    const message =
      `${activeCompany} — Mobil Eşleştirme Daveti\n\n` +
      `Eşleştirme Kodu: ${invite.code}\n` +
      `Bu kod yaklaşık ${remainingMinutes} dk geçerlidir ve tek kullanımlıktır. ` +
      `NSX Cari Takip Pro mobil uygulamasında "QR ile Bağla" ekranından QR'ı okutun; ` +
      `gerekirse kodu manuel girin.`;

    try {
      const canShareFile = await Sharing.isAvailableAsync();

      // Native tarafta profesyonel davet kartını PNG olarak üretip paylaş.
      if (canShareFile && shareCardRef.current) {
        // Görünmeyen paylaşım kartının son render'ını tamamlamasına izin ver.
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
        const captured = await captureRef(shareCardRef, {
          format: "png",
          quality: 1,
          result: "tmpfile",
        });
        const fileUri = captured.startsWith("file://") ? captured : `file://${captured}`;

        await Sharing.shareAsync(fileUri, {
          mimeType: "image/png",
          UTI: "public.png",
          dialogTitle: "NSX Cihaz Daveti",
        });
        return;
      }

      // Web veya dosya paylaşımının bulunmadığı cihazlarda güvenli metin fallback'i.
      await Share.share({ message, title: "NSX Cihaz Daveti" });
    } catch {
      // Kullanıcı paylaşım sayfasını kapattıysa veya hedef uygulama reddettiyse
      // oturumu/daveti bozma.
    } finally {
      setShareBusy(false);
    }
  };

  const remainingLabel = useMemo(() => {
    if (!invite) return "";
    const s = Math.floor(remainingMs / 1000);
    const mm = Math.floor(s / 60)
      .toString()
      .padStart(2, "0");
    const ss = (s % 60).toString().padStart(2, "0");
    return `${mm}:${ss}`;
  }, [invite, remainingMs]);

  const expired = invite ? remainingMs <= 0 : false;
  const errorMessage = inviteMut.error ? nsxApiErrorMessage(inviteMut.error, "create") : null;

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="invite-device-screen">
      <ScreenHeader title="Cihaz Davet Et" />
      <ScrollView
        contentContainerStyle={{
          paddingHorizontal: spacing.lg,
          paddingBottom: insets.bottom + 32,
          paddingTop: spacing.md,
        }}
      >
        <View
          style={[
            styles.introCard,
            { backgroundColor: colors.brandTertiary, borderColor: colors.brandPrimary + "44" },
          ]}
        >
          <View style={[styles.introIcon, { backgroundColor: colors.brandPrimary }]}>
            <Feather name="share-2" size={18} color={colors.onBrandPrimary} />
          </View>
          <View style={{ flex: 1 }}>
            <Text style={[styles.introTitle, { color: colors.onBrandTertiary }]}>
              İkinci cihazı bağla
            </Text>
            <Text style={[styles.introDesc, { color: colors.onBrandTertiary }]}>
              Kısa süreli, tek kullanımlık bir eşleştirme QR/kodu paylaşarak yeni bir mobil cihaza {activeCompany} hesabına erişim ver.
            </Text>
          </View>
        </View>

        {inviteMut.isPending && !invite ? (
          <View style={[styles.qrCard, { backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}>
            <ActivityIndicator color={colors.brandPrimary} size="large" />
            <Text style={[styles.loadingText, { color: colors.muted }]}>
              Yeni davet oluşturuluyor…
            </Text>
          </View>
        ) : errorMessage && !invite ? (
          <ErrorState
            testID="invite-error"
            title="Davet oluşturulamadı"
            description={errorMessage}
            onRetry={generateInvite}
          />
        ) : invite ? (
          <View style={[styles.qrCard, { backgroundColor: colors.surfaceSecondary, borderColor: colors.border }]}>
            <View style={styles.qrBox}>
              <QRCode
                value={invite.qrPayload}
                size={200}
                backgroundColor="#FFFFFF"
                color="#0F172A"
              />
              {expired ? (
                <View style={styles.qrExpiredOverlay}>
                  <Feather name="x-circle" size={36} color="#FFFFFF" />
                  <Text style={styles.qrExpiredText}>Süresi Doldu</Text>
                </View>
              ) : null}
            </View>

            <View style={styles.codePillRow}>
              <Text style={[styles.codeLabel, { color: colors.muted }]}>Eşleştirme Kodu</Text>
              <Text style={[styles.codeValue, { color: colors.onSurface }]}>{invite.code}</Text>
            </View>

            <View
              style={[
                styles.timerPill,
                { backgroundColor: expired ? colors.errorSoft : colors.surfaceTertiary },
              ]}
            >
              <Feather
                name={expired ? "alert-circle" : "clock"}
                size={12}
                color={expired ? colors.error : colors.muted}
              />
              <Text
                style={[
                  styles.timerLabel,
                  { color: expired ? colors.error : colors.muted },
                ]}
              >
                {expired ? "Süresi doldu" : `Kalan süre ${remainingLabel}`}
              </Text>
            </View>

            <View style={styles.actionRow}>
              <Pressable
                testID="invite-refresh"
                onPress={generateInvite}
                disabled={inviteMut.isPending}
                style={({ pressed }) => [
                  styles.secondaryBtn,
                  {
                    backgroundColor: colors.surfaceTertiary,
                    opacity: inviteMut.isPending ? 0.5 : pressed ? 0.85 : 1,
                  },
                ]}
              >
                {inviteMut.isPending ? (
                  <ActivityIndicator size="small" color={colors.onSurface} />
                ) : (
                  <>
                    <Feather name="refresh-cw" size={16} color={colors.onSurface} />
                    <Text style={[styles.secondaryLabel, { color: colors.onSurface }]}>
                      Yeni Kod
                    </Text>
                  </>
                )}
              </Pressable>
              <Pressable
                testID="invite-share"
                onPress={shareInvite}
                disabled={expired || shareBusy}
                style={({ pressed }) => [
                  styles.primaryBtn,
                  {
                    backgroundColor: colors.brandPrimary,
                    opacity: expired || shareBusy ? 0.5 : pressed ? 0.9 : 1,
                  },
                ]}
              >
                {shareBusy ? (
                  <ActivityIndicator size="small" color={colors.onBrandPrimary} />
                ) : (
                  <>
                    <Feather name="share-2" size={16} color={colors.onBrandPrimary} />
                    <Text style={[styles.primaryLabel, { color: colors.onBrandPrimary }]}>
                      QR ile Paylaş
                    </Text>
                  </>
                )}
              </Pressable>
            </View>
          </View>
        ) : null}

        <View style={styles.security}>
          <Feather name="shield" size={14} color={colors.muted} />
          <Text style={[styles.securityText, { color: colors.muted }]}>
            Bu davet <Text style={{ fontFamily: fonts.bodyBold }}>tek kullanımlıktır</Text> ve süresi dolduğunda otomatik geçersiz olur. Mevcut oturumunuz bu paylaşımda asla iletilmez.
          </Text>
        </View>
      </ScrollView>

      {invite && !expired ? (
        <View style={styles.shareCaptureHost} pointerEvents="none">
          <View
            ref={shareCardRef}
            collapsable={false}
            style={styles.shareCard}
            testID="invite-share-card"
          >
            <Image
              source={require("../assets/images/nsx-icon-light.png")}
              style={styles.shareLogo}
              resizeMode="contain"
            />
            <Text style={styles.shareEyebrow}>MOBİL EŞLEŞTİRME DAVETİ</Text>
            <Text style={styles.shareCompany} numberOfLines={2}>
              {activeCompany}
            </Text>

            <View style={styles.shareQrBox}>
              <QRCode
                value={invite.qrPayload}
                size={208}
                backgroundColor="#FFFFFF"
                color="#0F172A"
              />
            </View>

            <Text style={styles.shareCodeLabel}>EŞLEŞTİRME KODU</Text>
            <Text style={styles.shareCodeValue}>{invite.code}</Text>

            <View style={styles.shareInfoPill}>
              <Text style={styles.shareInfoText}>
                Yaklaşık {Math.max(1, Math.ceil(remainingMs / 60_000))} dk geçerli · Tek kullanımlık
              </Text>
            </View>

            <Text style={styles.shareInstruction}>
              NSX Cari Takip Pro mobil uygulamasında “QR ile Bağla” ekranından bu QR kodu okutun.
              QR okutulamıyorsa yukarıdaki kodu manuel girin.
            </Text>
            <Text style={styles.shareSecurity}>
              Bu davet yalnızca eşleştirme içindir; mevcut oturum bilgileri paylaşılmaz.
            </Text>
          </View>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  introCard: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 12,
    padding: spacing.lg,
    borderRadius: radius.lg,
    borderWidth: 1,
    marginTop: spacing.md,
  },
  introIcon: {
    width: 36,
    height: 36,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  introTitle: {
    fontFamily: fonts.displayMedium,
    fontSize: 16,
    letterSpacing: -0.3,
    marginBottom: 4,
  },
  introDesc: { fontFamily: fonts.body, fontSize: 13, lineHeight: 19, opacity: 0.92 },
  qrCard: {
    marginTop: spacing.lg,
    padding: spacing.xl,
    borderRadius: 24,
    borderWidth: 1,
    alignItems: "center",
  },
  qrBox: {
    width: 224,
    height: 224,
    borderRadius: 20,
    backgroundColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
    padding: 12,
  },
  qrExpiredOverlay: {
    ...StyleSheet.absoluteFill,
    borderRadius: 20,
    backgroundColor: "rgba(15,23,42,0.7)",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
  },
  qrExpiredText: { color: "#FFFFFF", fontFamily: fonts.bodyBold, fontSize: 14 },
  loadingText: { marginTop: 12, fontFamily: fonts.body, fontSize: 13 },
  codePillRow: { alignItems: "center", marginTop: spacing.lg, gap: 4 },
  codeLabel: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 10,
    letterSpacing: 0.5,
    textTransform: "uppercase",
  },
  codeValue: {
    fontFamily: fonts.display,
    fontSize: 24,
    letterSpacing: 2,
    fontVariant: ["tabular-nums"],
  },
  timerPill: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 12,
    paddingVertical: 7,
    borderRadius: 999,
    marginTop: spacing.md,
  },
  timerLabel: { fontFamily: fonts.bodySemiBold, fontSize: 12 },
  actionRow: {
    flexDirection: "row",
    gap: spacing.md,
    marginTop: spacing.lg,
    width: "100%",
  },
  secondaryBtn: {
    flex: 1,
    height: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
    borderRadius: 999,
  },
  primaryBtn: {
    flex: 1,
    height: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
    borderRadius: 999,
  },
  secondaryLabel: { fontFamily: fonts.bodyBold, fontSize: 14 },
  primaryLabel: { fontFamily: fonts.bodyBold, fontSize: 14 },
  security: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 8,
    marginTop: spacing.lg,
    padding: 12,
  },
  securityText: { flex: 1, fontFamily: fonts.body, fontSize: 12, lineHeight: 18 },
  shareCaptureHost: {
    position: "absolute",
    left: -10000,
    top: 0,
    width: 360,
  },
  shareCard: {
    width: 360,
    paddingHorizontal: 24,
    paddingTop: 24,
    paddingBottom: 22,
    borderRadius: 28,
    backgroundColor: "#F8FAFC",
    alignItems: "center",
    borderWidth: 1,
    borderColor: "#E2E8F0",
  },
  shareLogo: {
    width: 104,
    height: 104,
    marginBottom: 10,
  },
  shareEyebrow: {
    color: "#2563EB",
    fontSize: 11,
    fontWeight: "800",
    letterSpacing: 1.2,
    textAlign: "center",
  },
  shareCompany: {
    marginTop: 6,
    color: "#0F172A",
    fontSize: 20,
    lineHeight: 25,
    fontWeight: "800",
    textAlign: "center",
  },
  shareQrBox: {
    marginTop: 18,
    width: 232,
    height: 232,
    padding: 12,
    borderRadius: 20,
    backgroundColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
    borderColor: "#E2E8F0",
  },
  shareCodeLabel: {
    marginTop: 18,
    color: "#64748B",
    fontSize: 10,
    fontWeight: "800",
    letterSpacing: 1.1,
  },
  shareCodeValue: {
    marginTop: 5,
    color: "#0F172A",
    fontSize: 21,
    fontWeight: "900",
    letterSpacing: 1.15,
    textAlign: "center",
    fontVariant: ["tabular-nums"],
  },
  shareInfoPill: {
    marginTop: 14,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 999,
    backgroundColor: "#DBEAFE",
  },
  shareInfoText: {
    color: "#1D4ED8",
    fontSize: 11,
    fontWeight: "700",
    textAlign: "center",
  },
  shareInstruction: {
    marginTop: 16,
    color: "#334155",
    fontSize: 12,
    lineHeight: 18,
    textAlign: "center",
  },
  shareSecurity: {
    marginTop: 12,
    color: "#64748B",
    fontSize: 10,
    lineHeight: 15,
    textAlign: "center",
  },
});
