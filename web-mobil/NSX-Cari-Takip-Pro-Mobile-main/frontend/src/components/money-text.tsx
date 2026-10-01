import React from "react";
import { Text, type StyleProp, type TextStyle } from "react-native";
import { fonts, type } from "@/src/theme";
import { formatTRY } from "@/src/utils/format";

interface Props {
  value: number;
  color: string;
  size?: "hero" | "lg" | "md" | "sm";
  signed?: "debt" | "collection" | "none";
  style?: StyleProp<TextStyle>;
  testID?: string;
  numberOfLines?: number;
  adjustsFontSizeToFit?: boolean;
}

const SIZES = {
  hero: 36,
  lg: 22,
  md: 15,
  sm: 13,
} as const;

/**
 * Finansal tutar — tabular rakam + marka font. Tutar hiyerarşisinin
 * tek kaynağı; dashboard / KPI / satır aynı ritmi paylaşır.
 */
export function MoneyText({
  value,
  color,
  size = "md",
  signed = "none",
  style,
  testID,
  numberOfLines = 1,
  adjustsFontSizeToFit = true,
}: Props) {
  const prefix =
    signed === "debt" ? "+" : signed === "collection" ? "−" : "";
  const base = size === "hero" ? type.heroMoney : type.money;
  return (
    <Text
      testID={testID}
      numberOfLines={numberOfLines}
      adjustsFontSizeToFit={adjustsFontSizeToFit}
      minimumFontScale={0.68}
      style={[
        base,
        {
          color,
          fontSize: SIZES[size],
          fontFamily: size === "hero" ? fonts.display : fonts.bodyBold,
        },
        style,
      ]}
    >
      {prefix}
      {formatTRY(value)}
    </Text>
  );
}
