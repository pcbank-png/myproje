// NSX Mobile design tokens. Filled from /app/design_guidelines.json.
// Premium B2B fintech palette: navy brand, modern blue action, status
// green/red for financial state. Full light + dark theme.
// Dark theme prefers kurumsal koyu lacivert/gri yerine saf siyah AMOLED;
// NSX mavisi vurgu rengi korunur.

import { useMemo } from "react";
import { StyleSheet } from "react-native";
import { useThemePreference } from "./theme-preference";

export type ColorScheme = "light" | "dark";

const light = {
  // Surfaces
  surface: "#F8FAFC",
  onSurface: "#0F172A",
  surfaceSecondary: "#FFFFFF",
  onSurfaceSecondary: "#0F172A",
  surfaceTertiary: "#F1F5F9",
  onSurfaceTertiary: "#334155",
  surfaceInverse: "#0F172A",
  onSurfaceInverse: "#FFFFFF",
  muted: "#64748B",

  // Brand
  brand: "#0F172A",
  onBrand: "#FFFFFF",
  brandPrimary: "#2563EB",
  onBrandPrimary: "#FFFFFF",
  brandSecondary: "#0F172A",
  onBrandSecondary: "#FFFFFF",
  brandTertiary: "#DBEAFE",
  onBrandTertiary: "#1D4ED8",

  // Status (financial)
  success: "#047857",
  onSuccess: "#FFFFFF",
  successSoft: "#ECFDF5",
  warning: "#92400E",
  onWarning: "#FFFFFF",
  warningSoft: "#FFFBEB",
  error: "#B91C1C",
  onError: "#FFFFFF",
  errorSoft: "#FEF2F2",
  info: "#3B82F6",
  onInfo: "#FFFFFF",

  // Lines
  border: "#E2E8F0",
  borderStrong: "#CBD5E1",
  divider: "#F1F5F9",

  // Overlay / scrim
  overlay: "rgba(15, 23, 42, 0.55)",
};

const dark = {
  // Kurumsal koyu — saf siyah değil, slate/navy tabanlı
  surface: "#0B1220", // arka plan
  onSurface: "#F8FAFC",
  surfaceSecondary: "#111C33", // kartlar
  onSurfaceSecondary: "#F1F5F9",
  surfaceTertiary: "#1B2A47", // ikinci seviye yüzey
  onSurfaceTertiary: "#CBD5E1",
  surfaceInverse: "#F8FAFC",
  onSurfaceInverse: "#0F172A",
  muted: "#94A3B8",

  // Brand — NSX mavisi biraz daha parlak
  brand: "#0B1220",
  onBrand: "#F8FAFC",
  brandPrimary: "#3B82F6",
  onBrandPrimary: "#FFFFFF",
  brandSecondary: "#F8FAFC",
  onBrandSecondary: "#0B1220",
  brandTertiary: "#1E3A8A", // koyu tonu tersine çevrilmiş
  onBrandTertiary: "#BFDBFE",

  // Status
  success: "#22C55E",
  onSuccess: "#052E16",
  successSoft: "rgba(34,197,94,0.14)",
  warning: "#FBBF24",
  onWarning: "#1F1300",
  warningSoft: "rgba(251,191,36,0.14)",
  error: "#F87171",
  onError: "#3B0808",
  errorSoft: "rgba(248,113,113,0.14)",
  info: "#60A5FA",
  onInfo: "#0B1220",

  border: "rgba(148,163,184,0.18)",
  borderStrong: "rgba(148,163,184,0.32)",
  divider: "rgba(148,163,184,0.12)",

  overlay: "rgba(0, 0, 0, 0.7)",
};

export type ThemeColors = typeof light;

export const themes: { light: ThemeColors; dark: ThemeColors } = { light, dark };

export const spacing = {
  xs: 4,
  sm: 8,
  md: 12,
  lg: 16,
  xl: 24,
  xxl: 32,
  xxxl: 48,
};

export const radius = {
  sm: 6,
  md: 12,
  lg: 20,
  pill: 999,
};

export const fontSizes = {
  sm: 12,
  base: 14,
  lg: 16,
  xl: 20,
  "2xl": 24,
  "3xl": 32,
  "4xl": 40,
};

/** Marka tipografi — Space Grotesk (başlık) + DM Sans (gövde/para). */
export const fonts = {
  display: "SpaceGrotesk_700Bold",
  displayMedium: "SpaceGrotesk_600SemiBold",
  displayRegular: "SpaceGrotesk_500Medium",
  body: "DMSans_400Regular",
  bodyMedium: "DMSans_500Medium",
  bodySemiBold: "DMSans_600SemiBold",
  bodyBold: "DMSans_700Bold",
} as const;

export const type = {
  heroMoney: {
    fontFamily: fonts.display,
    fontSize: 36,
    letterSpacing: -1.2,
    fontVariant: ["tabular-nums"] as ("tabular-nums")[],
  },
  money: {
    fontFamily: fonts.bodyBold,
    letterSpacing: -0.3,
    fontVariant: ["tabular-nums"] as ("tabular-nums")[],
  },
  title: {
    fontFamily: fonts.display,
    letterSpacing: -0.5,
  },
  section: {
    fontFamily: fonts.displayMedium,
    letterSpacing: -0.3,
  },
  label: {
    fontFamily: fonts.bodySemiBold,
    letterSpacing: 0.4,
    textTransform: "uppercase" as const,
  },
  body: {
    fontFamily: fonts.body,
  },
  bodyStrong: {
    fontFamily: fonts.bodySemiBold,
  },
};

export function useTheme(): { scheme: ColorScheme; colors: ThemeColors } {
  const scheme = useThemePreference().resolvedScheme;
  return { scheme, colors: themes[scheme] };
}

export function makeStyles<T extends StyleSheet.NamedStyles<T> | StyleSheet.NamedStyles<unknown>>(
  factory: (colors: ThemeColors) => T & StyleSheet.NamedStyles<unknown>,
): () => T {
  return function useStyles(): T {
    const { colors } = useTheme();
    return useMemo(() => StyleSheet.create(factory(colors)), [colors]);
  };
}
