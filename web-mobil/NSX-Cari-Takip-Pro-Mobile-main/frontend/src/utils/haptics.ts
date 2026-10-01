// Native haptic feedback wrapper.
// Sistem sessiz moduna saygı: iOS'ta Haptics zaten kullanıcının
// haptic tercihine uyar, Android'de vibration izinli değilse no-op.
// Web ortamında sessizce yok sayılır.
import * as Haptics from "expo-haptics";

/** Pull-to-refresh onRefresh anında kısa native "tik". */
export function refreshHaptic(): void {
  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light).catch(() => {});
}

/** Tehlikeli aksiyon (sil butonuna basıldığında onay öncesi). */
export function warnHaptic(): void {
  Haptics.notificationAsync(Haptics.NotificationFeedbackType.Warning).catch(() => {});
}

/** Başarı geri bildirimi (kaydet/kaydedildi anında). */
export function successHaptic(): void {
  Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success).catch(() => {});
}

/** Hata geri bildirimi (kaydedilemedi, hata modalları). */
export function errorHaptic(): void {
  Haptics.notificationAsync(Haptics.NotificationFeedbackType.Error).catch(() => {});
}

/** Seçim değişimi (segment/filtre değişince). */
export function selectionHaptic(): void {
  Haptics.selectionAsync().catch(() => {});
}
