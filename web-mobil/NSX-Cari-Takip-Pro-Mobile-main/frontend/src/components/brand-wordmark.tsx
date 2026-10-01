// NSX marka wordmark bileşeni — üretilmiş brand asset'lerini zemine
// göre seçer. `tone` kullanıldığı zemini ifade eder:
// - "onDark": koyu zemin üstünde (beyaz NS + gradient X) → dark asset
// - "onLight": açık zemin üstünde (lacivert NS + gradient X) → light asset
// Varsayılan: mevcut temanın yüzeyine göre otomatik seçim.

import React from "react";
import { Image, StyleSheet, type StyleProp, type ImageStyle } from "react-native";
import { useTheme } from "../theme";

const DARK = require("../../assets/images/nsx-wordmark-dark.png");
const LIGHT = require("../../assets/images/nsx-wordmark-light.png");

// Sıkı kesilmiş asset oranları. Koyu: 748×354, açık: 1443×429.
const DARK_RATIO = 354 / 748;
const LIGHT_RATIO = 429 / 1443;

interface Props {
  width?: number;
  tone?: "onDark" | "onLight";
  style?: StyleProp<ImageStyle>;
  testID?: string;
}

export function BrandWordmark({ width = 240, tone, style, testID }: Props) {
  const { scheme } = useTheme();
  const effective = tone ?? (scheme === "dark" ? "onDark" : "onLight");
  const source = effective === "onDark" ? DARK : LIGHT;
  const ratio = effective === "onDark" ? DARK_RATIO : LIGHT_RATIO;
  return (
    <Image
      source={source}
      testID={testID}
      style={[styles.img, { width, height: width * ratio }, style]}
      resizeMode="contain"
      accessibilityLabel="NSX Cari Takip Pro"
    />
  );
}

const styles = StyleSheet.create({
  img: { alignSelf: "center" },
});
