import React, { useMemo } from "react";
import { View, StyleSheet } from "react-native";
import Svg, { Defs, LinearGradient, Path, Stop } from "react-native-svg";
import { useTheme } from "@/src/theme";

interface Props {
  values: number[];
  width?: number;
  height?: number;
  testID?: string;
}

/**
 * Mini balance sparkline — son N günün net bakiye eğrisi.
 * Pozitif trend brand/success, negatif trend error tonu.
 */
export function Sparkline({ values, width = 140, height = 36, testID }: Props) {
  const { colors } = useTheme();

  const { line, area, stroke } = useMemo(() => {
    if (!values.length) {
      return { line: "", area: "", stroke: colors.muted };
    }
    const min = Math.min(...values);
    const max = Math.max(...values);
    const span = max - min || 1;
    const padY = 3;
    const usableH = height - padY * 2;
    const step = values.length > 1 ? width / (values.length - 1) : width;

    const pts = values.map((v, i) => {
      const x = i * step;
      const y = padY + usableH - ((v - min) / span) * usableH;
      return { x, y };
    });

    const linePath = pts
      .map((p, i) => `${i === 0 ? "M" : "L"} ${p.x.toFixed(1)} ${p.y.toFixed(1)}`)
      .join(" ");

    const last = pts[pts.length - 1];
    const first = pts[0];
    const areaPath = `${linePath} L ${last.x.toFixed(1)} ${height} L ${first.x.toFixed(1)} ${height} Z`;

    const delta = values[values.length - 1] - values[0];
    const strokeColor =
      delta > 0 ? colors.success : delta < 0 ? colors.error : colors.brandPrimary;

    return { line: linePath, area: areaPath, stroke: strokeColor };
  }, [values, width, height, colors]);

  if (values.length < 2) {
    return <View style={{ width, height }} testID={testID} />;
  }

  return (
    <View style={styles.wrap} testID={testID}>
      <Svg width={width} height={height}>
        <Defs>
          <LinearGradient id="sparkFill" x1="0" y1="0" x2="0" y2="1">
            <Stop offset="0" stopColor={stroke} stopOpacity={0.28} />
            <Stop offset="1" stopColor={stroke} stopOpacity={0.02} />
          </LinearGradient>
        </Defs>
        <Path d={area} fill="url(#sparkFill)" />
        <Path
          d={line}
          stroke={stroke}
          strokeWidth={2}
          fill="none"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </Svg>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { alignSelf: "flex-start" },
});
