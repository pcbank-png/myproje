import React from "react";
import {
  Image,
  StyleSheet,
  View,
  type ImageStyle,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { useTheme } from "@/src/theme";

const MAIN = require("../../assets/images/nsx-icon-main.png");
const LIGHT = require("../../assets/images/nsx-icon-light.png");
const DARK = require("../../assets/images/nsx-icon-dark.png");

export type BrandAppIconVariant = "main" | "light" | "dark";

interface Props {
  size?: number;
  variant?: BrandAppIconVariant;
  /** Arkada görünen zemin — logo köşeleri şeffaf; bu renk akar. */
  surface?: string;
  style?: StyleProp<ViewStyle>;
  imageStyle?: StyleProp<ImageStyle>;
  testID?: string;
}

export function BrandAppIcon({
  size = 120,
  variant = "main",
  surface,
  style,
  imageStyle,
  testID,
}: Props) {
  const { colors } = useTheme();
  const source = variant === "light" ? LIGHT : variant === "dark" ? DARK : MAIN;
  const bg = surface ?? colors.surface;

  return (
    <View
      style={[styles.wrap, { width: size, height: size, backgroundColor: bg }, style]}
      testID={testID}
    >
      <Image
        source={source}
        style={[styles.image, { width: size, height: size }, imageStyle]}
        resizeMode="contain"
        accessibilityLabel="NSX Cari Takip Pro"
      />
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    alignSelf: "center",
    overflow: "hidden",
  },
  image: {
    alignSelf: "center",
  },
});
