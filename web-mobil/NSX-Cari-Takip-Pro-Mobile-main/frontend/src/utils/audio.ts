// Uygulama ses tasarımı — iki amaçlı, local asset tabanlı (offline çalışır,
// ilk tetiklemede bekleme yok):
//
// 1) `startSplashIntro()` — "Short Logo" (LiteSaturation). Yalnızca GERÇEK
//    cold launch'ta, splash animasyonuyla senkron BİR KEZ çalar.
//    Splash kapanışı SABİT süreye değil, ses dosyasının gerçek playback
//    durumuna bağlıdır (`playbackStatusUpdate` event'leri):
//      - onStart: ses gerçekten çalmaya başladığında (animasyon başlar)
//      - onNearEnd: bitime ~500 ms kala (kapanış küçülme/fade başlar)
//      - onEnd: ses tamamen bittiğinde (splash kapanır)
//    Ses sona kadar kesintisiz çalar; splash unmount/navigation sesi erken
//    durdurmaz. Background→foreground, SignalR reconnect, tab geçişi ve QR
//    login sonrası tekrar çalmaz (modül guard'ı).
//
// 2) `playRefreshSound()` — "UI Soft Glass Ping". Yalnızca kullanıcının
//    MANUEL pull-to-refresh işlemi başarıyla tamamlanınca bir kez çalar.
//
// Seviyeler: splash ~%45, refresh ~%60. Sistem sessiz moduna saygı: iOS'ta
// `playsInSilentMode: false`; diğer uygulamaların sesi kesilmez
// (`interruptionMode: "mixWithOthers"`).

import { createAudioPlayer, setAudioModeAsync, type AudioPlayer } from "expo-audio";

const SPLASH_ASSET = require("../../assets/sounds/splash-intro.mp3");
const REFRESH_ASSET = require("../../assets/sounds/refresh-ping.mp3");

const SPLASH_VOLUME = 0.45;
const REFRESH_VOLUME = 0.6;

/**
 * Fallback süre — ses dosyasının (splash-intro.mp3) bilinen süresi (ms).
 * Normal akışta kullanılmaz; yalnızca playback hiç başlayamazsa (örn. web
 * autoplay politikası) splash'in kilitlenmemesi için güvenlik zamanlayıcı.
 */
export const SPLASH_DURATION_MS = 3000;

let splashPlayer: AudioPlayer | null = null;
let refreshPlayer: AudioPlayer | null = null;
let modeConfigured = false;
let initFailed = false;
/** Cold-launch guard — splash sesi uygulama yaşam döngüsünde 1 kez çalar. */
let splashPlayed = false;
let preparePromise: Promise<void> | null = null;

async function configureAudioMode(): Promise<void> {
  if (modeConfigured) return;
  modeConfigured = true;
  try {
    await setAudioModeAsync({
      playsInSilentMode: false,
      interruptionMode: "mixWithOthers",
      shouldPlayInBackground: false,
    });
  } catch {
    // best-effort
  }
}

function makePlayer(asset: number, volume: number): AudioPlayer | null {
  if (initFailed) return null;
  try {
    const p = createAudioPlayer(asset);
    p.volume = volume;
    return p;
  } catch {
    initFailed = true;
    return null;
  }
}

/**
 * Splash ses player'ını önceden oluşturur ve başa sarar — splash mount
 * olmadan hemen önce (bundle yüklenirken) çağrılırsa `play()` anında
 * başlar; ses/animasyon senkronu için.
 */
export function prepareSplashIntro(): void {
  if (splashPlayed || initFailed) return;
  if (!preparePromise) {
    preparePromise = (async () => {
      await configureAudioMode();
      if (!splashPlayer) splashPlayer = makePlayer(SPLASH_ASSET, SPLASH_VOLUME);
      try {
        await splashPlayer?.seekTo(0);
      } catch {
        // yok say
      }
    })();
  }
}

export interface SplashPlaybackCallbacks {
  /** Ses gerçekten çalmaya başladığı anda (animasyon bununla başlar). */
  onStart?: (durationMs: number) => void;
  /** Bitime ~500 ms kala (kapanış shrink/fade bununla başlar). */
  onNearEnd?: () => void;
  /** Ses tamamen bittiğinde (splash tamamen kapanır). */
  onEnd?: () => void;
}

/**
 * Splash intro sesini playback event'leriyle çalar. Uygulama yaşamında
 * yalnızca ilk çağrı ses üretir; sonraki çağrılar callback'leri anında
 * sırayla tetikler (splash'in takılmaması için).
 */
export function startSplashIntro(cb: SplashPlaybackCallbacks): void {
  if (splashPlayed) {
    cb.onStart?.(SPLASH_DURATION_MS);
    cb.onNearEnd?.();
    cb.onEnd?.();
    return;
  }
  splashPlayed = true;
  void (async () => {
    try {
      await preparePromise; // preload tamamlanana kadar bekle (varsa)
      await configureAudioMode();
      if (!splashPlayer) splashPlayer = makePlayer(SPLASH_ASSET, SPLASH_VOLUME);
      const p = splashPlayer;
      if (!p) {
        cb.onStart?.(SPLASH_DURATION_MS);
        return;
      }
      let started = false;
      let nearEndFired = false;
      let endFired = false;
      const sub = p.addListener("playbackStatusUpdate", (s) => {
        const durMs = s.duration > 0 ? s.duration * 1000 : SPLASH_DURATION_MS;
        if (!started && s.playing) {
          started = true;
          cb.onStart?.(durMs);
        }
        if (started && !nearEndFired && durMs > 0 && s.currentTime * 1000 >= durMs - 500) {
          nearEndFired = true;
          cb.onNearEnd?.();
        }
        if (
          started &&
          !endFired &&
          (s.didJustFinish || (durMs > 0 && s.currentTime * 1000 >= durMs - 40))
        ) {
          endFired = true;
          sub.remove();
          cb.onEnd?.();
        }
      });
      await p.seekTo(0);
      p.play();
      // Playback hiç başlayamazsa (web autoplay engeli vb.): animasyonu
      // sessiz timeline ile başlat; onEnd splash bileşeninin güvenlik
      // zamanlayıcısı tarafından tetiklenir.
      setTimeout(() => {
        if (!started) {
          started = true;
          cb.onStart?.(SPLASH_DURATION_MS);
        }
      }, 1200);
    } catch {
      cb.onStart?.(SPLASH_DURATION_MS);
    }
  })();
}

/**
 * Manuel pull-to-refresh başarı sesi. Aynı refresh içinde birden çok çağrı
 * gelirse ses üst üste binmez; player başa sarılıp tek sefer çalar.
 */
export function playRefreshSound(): void {
  void (async () => {
    await configureAudioMode();
    if (!refreshPlayer) refreshPlayer = makePlayer(REFRESH_ASSET, REFRESH_VOLUME);
    const p = refreshPlayer;
    if (!p) return;
    try {
      await p.seekTo(0);
      p.play();
    } catch {
      // yok say
    }
  })();
}
