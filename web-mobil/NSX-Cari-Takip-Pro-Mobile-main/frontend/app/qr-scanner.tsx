// Native QR scanner — full-screen modal over the login screen.
import React, { useCallback, useEffect, useRef, useState } from "react";
import {
  View,
  Text,
  Pressable,
  StyleSheet,
  ActivityIndicator,
  Platform,
  Linking,
} from "react-native";
import { StatusBar } from "expo-status-bar";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import { Camera, CameraView, useCameraPermissions } from "expo-camera";
import * as ImagePicker from "expo-image-picker";
import { useRouter } from "expo-router";
import { useAuth } from "@/src/auth/auth-context";
import { useTheme, fonts, spacing, radius } from "@/src/theme";
import { extractPairingCode } from "@/src/auth/qr-payload";
import {
  selectionHaptic,
  successHaptic,
  errorHaptic,
} from "@/src/utils/haptics";

export default function QRScannerModal() {
  const insets = useSafeAreaInsets();
  const { colors, scheme } = useTheme();
  const router = useRouter();
  const { pair } = useAuth();

  const [permission, requestPermission] = useCameraPermissions();
  const [torch, setTorch] = useState(false);
  const [pairing, setPairing] = useState(false);
  const [pickingImage, setPickingImage] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const locked = useRef(false);

  useEffect(() => {
    locked.current = false;
  }, []);

  const handleScan = useCallback(
    async ({ data }: { data: string }) => {
      if (locked.current || pairing) return;
      locked.current = true;
      setError(null);
      const code = extractPairingCode(data);
      if (!code) {
        errorHaptic();
        setError("QR kod okunamadı. Tekrar deneyin.");
        locked.current = false;
        return;
      }
      setPairing(true);
      try {
        await pair(code);
        successHaptic();
        router.dismissAll();
        router.replace("/(tabs)");
      } catch (e) {
        errorHaptic();
        setError(e instanceof Error ? e.message : "Eşleştirme başarısız. Tekrar deneyin.");
        setPairing(false);
        locked.current = false;
      }
    },
    [pair, pairing, router],
  );

  const pickQrFromLibrary = useCallback(async () => {
    if (pairing || pickingImage || locked.current) return;
    selectionHaptic();
    setError(null);
    setPickingImage(true);
    try {
      const result = await ImagePicker.launchImageLibraryAsync({
        mediaTypes: ["images"],
        allowsEditing: false,
        quality: 1,
      });

      if (result.canceled || !result.assets?.length) return;

      const asset = result.assets[0];
      const scans = await Camera.scanFromURLAsync(asset.uri, ["qr"]);
      const qr = scans.find((item) => item.type === "qr") ?? scans[0];

      if (!qr?.data) {
        errorHaptic();
        setError("Seçilen fotoğrafta QR kodu bulunamadı.");
        return;
      }

      await handleScan({ data: qr.data });
    } catch (e) {
      errorHaptic();
      setError(
        e instanceof Error
          ? e.message
          : "Fotoğraftaki QR kodu okunamadı. Başka bir görsel deneyin.",
      );
    } finally {
      setPickingImage(false);
    }
  }, [handleScan, pairing, pickingImage]);

  const close = () => router.back();

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="qr-scanner-modal">
      <StatusBar style={scheme === "dark" ? "light" : "dark"} />

      <View
        style={[
          styles.header,
          {
            paddingTop: insets.top + 8,
            backgroundColor: colors.surface,
            borderBottomColor: colors.divider,
          },
        ]}
      >
        <Pressable
          onPress={close}
          hitSlop={12}
          style={({ pressed }) => [styles.iconBtn, { opacity: pressed ? 0.7 : 1 }]}
          testID="qr-scanner-close"
        >
          <View style={[styles.iconChip, { backgroundColor: colors.surfaceTertiary }]}>
            <Feather name="x" size={20} color={colors.onSurface} />
          </View>
        </Pressable>
        <Text style={[styles.headerTitle, { color: colors.onSurface }]}>QR ile Bağla</Text>
        <Pressable
          onPress={() => {
            selectionHaptic();
            setTorch((t) => !t);
          }}
          hitSlop={12}
          style={({ pressed }) => [styles.iconBtn, { opacity: pressed ? 0.7 : 1 }]}
          testID="qr-scanner-torch"
        >
          <View
            style={[
              styles.iconChip,
              {
                backgroundColor: torch ? colors.warningSoft : colors.surfaceTertiary,
              },
            ]}
          >
            <Feather
              name={torch ? "zap" : "zap-off"}
              size={18}
              color={torch ? colors.warning : colors.muted}
            />
          </View>
        </Pressable>
      </View>

      {Platform.OS === "web" ? (
        <StatePane
          icon="smartphone"
          title="Kamera önizlemede kullanılamıyor"
          description="QR taramayı denemek için uygulamayı Expo Go ile telefonunuzda açın. Tarayıcıda eşleştirme kodu ile giriş yapabilirsiniz."
          actionLabel="Kod ile Girişe Dön"
          onAction={close}
          testID="qr-web-fallback"
        />
      ) : !permission ? (
        <View style={styles.center}>
          <ActivityIndicator color={colors.brandPrimary} />
        </View>
      ) : !permission.granted ? (
        permission.canAskAgain ? (
          <StatePane
            icon="camera"
            title="Kamera izni gerekli"
            description="Masaüstündeki QR kodu okutarak saniyeler içinde güvenli giriş yapın. Kamera yalnızca tarama sırasında kullanılır."
            actionLabel="Kamerayı Etkinleştir"
            onAction={() => {
              selectionHaptic();
              void requestPermission();
            }}
            secondaryLabel="Kod ile Girişe Dön"
            onSecondary={close}
            testID="qr-permission-pane"
          />
        ) : (
          <StatePane
            icon="camera-off"
            title="Kamera erişimi kapalı"
            description="QR tarama için kamera izni ayarlardan açılmalı."
            actionLabel="Ayarları Aç"
            onAction={() => Linking.openSettings()}
            secondaryLabel="Kod ile Girişe Dön"
            onSecondary={close}
            testID="qr-permission-blocked"
          />
        )
      ) : (
        <View style={styles.cameraWrap}>
          <CameraView
            style={StyleSheet.absoluteFill}
            facing="back"
            enableTorch={torch}
            barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
            onBarcodeScanned={pairing ? undefined : handleScan}
          />
          <View style={styles.overlay} pointerEvents="none">
            <View style={styles.maskCol}>
              <View style={styles.maskBar} />
              <View style={styles.maskMid}>
                <View style={styles.maskSide} />
                <View style={styles.frame} testID="qr-scan-frame">
                  <Corner
                    color={colors.brandPrimary}
                    style={{ top: -2, left: -2, borderTopWidth: 4, borderLeftWidth: 4 }}
                  />
                  <Corner
                    color={colors.brandPrimary}
                    style={{ top: -2, right: -2, borderTopWidth: 4, borderRightWidth: 4 }}
                  />
                  <Corner
                    color={colors.brandPrimary}
                    style={{ bottom: -2, left: -2, borderBottomWidth: 4, borderLeftWidth: 4 }}
                  />
                  <Corner
                    color={colors.brandPrimary}
                    style={{ bottom: -2, right: -2, borderBottomWidth: 4, borderRightWidth: 4 }}
                  />
                </View>
                <View style={styles.maskSide} />
              </View>
              <View style={[styles.maskBar, styles.maskBottom]}>
                <Text style={styles.hint}>Masaüstündeki QR kodu çerçevenin içine hizalayın</Text>
              </View>
            </View>
          </View>

          {pairing ? (
            <View style={styles.pairingOverlay} testID="qr-pairing-spinner">
              <View style={styles.pairingCard}>
                <ActivityIndicator size="large" color="#FFFFFF" />
                <Text style={styles.pairingLabel}>Bağlanıyor…</Text>
              </View>
            </View>
          ) : null}
        </View>
      )}

      <View
        style={[
          styles.footer,
          {
            paddingBottom: insets.bottom + 16,
            backgroundColor: colors.surface,
            borderTopColor: colors.divider,
          },
        ]}
      >
        {error ? (
          <View
            style={[
              styles.errorChip,
              { backgroundColor: colors.errorSoft, borderColor: colors.error + "33" },
            ]}
            testID="qr-scan-error"
          >
            <Feather name="alert-circle" size={14} color={colors.error} />
            <Text style={[styles.errorText, { color: colors.error }]}>{error}</Text>
          </View>
        ) : null}
        {Platform.OS !== "web" ? (
          <Pressable
            onPress={pickQrFromLibrary}
            disabled={pairing || pickingImage}
            style={({ pressed }) => [
              styles.galleryBtn,
              {
                borderColor: colors.border,
                backgroundColor: colors.surfaceSecondary,
                opacity: pairing || pickingImage ? 0.55 : pressed ? 0.9 : 1,
                transform: [{ scale: pressed && !pairing && !pickingImage ? 0.98 : 1 }],
              },
            ]}
            testID="qr-pick-from-library"
          >
            {pickingImage ? (
              <ActivityIndicator size="small" color={colors.brandPrimary} />
            ) : (
              <View style={[styles.galleryIcon, { backgroundColor: colors.brandTertiary }]}>
                <Feather name="image" size={15} color={colors.onBrandTertiary} />
              </View>
            )}
            <Text style={[styles.galleryLabel, { color: colors.onSurface }]}>
              {pickingImage ? "Fotoğraftaki QR okunuyor…" : "Fotoğraftan QR Seç"}
            </Text>
          </Pressable>
        ) : null}
        <Pressable
          onPress={close}
          hitSlop={8}
          style={({ pressed }) => [styles.fallbackBtn, { opacity: pressed ? 0.7 : 1 }]}
          testID="qr-fallback-manual"
        >
          <Feather name="key" size={14} color={colors.muted} />
          <Text style={[styles.fallbackLabel, { color: colors.muted }]}>
            Eşleştirme kodu ile devam et
          </Text>
        </Pressable>
      </View>
    </View>
  );
}

function Corner({ style, color }: { style: object; color: string }) {
  return (
    <View
      style={[
        {
          position: "absolute",
          width: 30,
          height: 30,
          borderColor: color,
          borderRadius: 6,
        },
        style,
      ]}
    />
  );
}

interface StatePaneProps {
  icon: React.ComponentProps<typeof Feather>["name"];
  title: string;
  description: string;
  actionLabel: string;
  onAction: () => void;
  secondaryLabel?: string;
  onSecondary?: () => void;
  testID?: string;
}

function StatePane({
  icon,
  title,
  description,
  actionLabel,
  onAction,
  secondaryLabel,
  onSecondary,
  testID,
}: StatePaneProps) {
  const { colors } = useTheme();
  return (
    <View style={styles.statePane} testID={testID}>
      <View style={[styles.stateIcon, { backgroundColor: colors.brandTertiary }]}>
        <Feather name={icon} size={30} color={colors.onBrandTertiary} />
      </View>
      <Text style={[styles.stateTitle, { color: colors.onSurface }]}>{title}</Text>
      <Text style={[styles.stateDesc, { color: colors.muted }]}>{description}</Text>
      <Pressable
        onPress={onAction}
        style={({ pressed }) => [
          styles.stateCta,
          {
            backgroundColor: colors.brandPrimary,
            opacity: pressed ? 0.92 : 1,
            transform: [{ scale: pressed ? 0.98 : 1 }],
          },
        ]}
        testID={`${testID}-action`}
      >
        <Text style={[styles.stateCtaLabel, { color: colors.onBrandPrimary }]}>{actionLabel}</Text>
      </Pressable>
      {secondaryLabel && onSecondary ? (
        <Pressable
          onPress={onSecondary}
          hitSlop={8}
          style={{ marginTop: 16 }}
          testID={`${testID}-secondary`}
        >
          <Text style={[styles.stateSecondary, { color: colors.muted }]}>{secondaryLabel}</Text>
        </Pressable>
      ) : null}
    </View>
  );
}

const FRAME = 248;

const styles = StyleSheet.create({
  root: { flex: 1 },
  header: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: 10,
    paddingBottom: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  iconBtn: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  iconChip: {
    width: 36,
    height: 36,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  headerTitle: { fontFamily: fonts.displayMedium, fontSize: 17, letterSpacing: -0.3 },
  center: { flex: 1, alignItems: "center", justifyContent: "center" },
  cameraWrap: { flex: 1, overflow: "hidden", backgroundColor: "#000" },
  overlay: { ...StyleSheet.absoluteFill },
  maskCol: { flex: 1 },
  maskBar: { flex: 1, backgroundColor: "rgba(11,18,32,0.58)" },
  maskBottom: { alignItems: "center", paddingTop: 28 },
  maskMid: { flexDirection: "row", height: FRAME },
  maskSide: { flex: 1, backgroundColor: "rgba(11,18,32,0.58)" },
  frame: {
    width: FRAME,
    height: FRAME,
    borderRadius: 20,
    backgroundColor: "transparent",
  },
  hint: {
    color: "rgba(255,255,255,0.92)",
    fontFamily: fonts.bodySemiBold,
    fontSize: 14,
    textAlign: "center",
    paddingHorizontal: 32,
    textShadowColor: "rgba(0,0,0,0.45)",
    textShadowOffset: { width: 0, height: 1 },
    textShadowRadius: 4,
  },
  pairingOverlay: {
    ...StyleSheet.absoluteFill,
    backgroundColor: "rgba(11,18,32,0.72)",
    alignItems: "center",
    justifyContent: "center",
  },
  pairingCard: {
    alignItems: "center",
    gap: 14,
    paddingHorizontal: 28,
    paddingVertical: 24,
    borderRadius: 20,
    backgroundColor: "rgba(255,255,255,0.08)",
  },
  pairingLabel: { color: "#FFFFFF", fontFamily: fonts.bodySemiBold, fontSize: 15 },
  footer: {
    paddingHorizontal: spacing.xl,
    paddingTop: 12,
    gap: 10,
    borderTopWidth: StyleSheet.hairlineWidth,
  },
  galleryBtn: {
    minHeight: 50,
    borderRadius: radius.pill,
    borderWidth: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 10,
    paddingHorizontal: 18,
  },
  galleryIcon: {
    width: 28,
    height: 28,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
  },
  galleryLabel: { fontFamily: fonts.bodyBold, fontSize: 14 },
  errorChip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 12,
    borderWidth: 1,
  },
  errorText: { flex: 1, fontFamily: fonts.bodyMedium, fontSize: 13, lineHeight: 18 },
  fallbackBtn: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    paddingVertical: 10,
  },
  fallbackLabel: { fontFamily: fonts.bodySemiBold, fontSize: 14 },
  statePane: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 32,
    gap: 10,
  },
  stateIcon: {
    width: 72,
    height: 72,
    borderRadius: 24,
    alignItems: "center",
    justifyContent: "center",
    marginBottom: 8,
  },
  stateTitle: {
    fontFamily: fonts.displayMedium,
    fontSize: 18,
    letterSpacing: -0.3,
    textAlign: "center",
  },
  stateDesc: {
    fontFamily: fonts.body,
    fontSize: 14,
    lineHeight: 21,
    textAlign: "center",
  },
  stateCta: {
    marginTop: 12,
    height: 50,
    paddingHorizontal: 28,
    borderRadius: radius.pill,
    alignItems: "center",
    justifyContent: "center",
  },
  stateCtaLabel: { fontFamily: fonts.bodyBold, fontSize: 15 },
  stateSecondary: { fontFamily: fonts.bodySemiBold, fontSize: 14 },
});
