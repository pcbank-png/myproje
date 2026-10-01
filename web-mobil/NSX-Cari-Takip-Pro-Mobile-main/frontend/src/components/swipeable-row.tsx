// Kurumsal swipe-aksiyon sarmalayıcı. react-native-gesture-handler'ın
// Swipeable'ı üzerinde ince bir katman: iri, okunaklı butonlar; tutarlı
// yükseklik/border; tehlikeli aksiyon kırmızı. Otomatik veri
// yenilemesi (SignalR/foreground refetch/polling) sırasında kullanıcı
// swipe'ı asla etkilenmez çünkü bu component durum tutmaz.

import React, { useRef } from "react";
import { StyleSheet, Text, View } from "react-native";
import { RectButton, Swipeable } from "react-native-gesture-handler";
import Feather from "@react-native-vector-icons/feather";
import { useTheme, fonts } from "@/src/theme";

type IconName = React.ComponentProps<typeof Feather>["name"];

export interface SwipeAction {
  key: string;
  label: string;
  icon: IconName;
  tone: "danger" | "primary";
  onPress: () => void;
  testID?: string;
}

interface Props {
  actions: SwipeAction[];
  children: React.ReactNode;
}

const ACTION_WIDTH = 88;

export function SwipeableRow({ actions, children }: Props) {
  const { colors } = useTheme();
  const ref = useRef<Swipeable>(null);

  const renderRight = () => (
    <View style={styles.actionsWrap}>
      {actions.map((a) => {
        const bg = a.tone === "danger" ? colors.error : colors.brandPrimary;
        const fg = a.tone === "danger" ? colors.onError : colors.onBrandPrimary;
        return (
          <RectButton
            key={a.key}
            testID={a.testID}
            style={[styles.action, { backgroundColor: bg }]}
            onPress={() => {
              ref.current?.close();
              a.onPress();
            }}
          >
            <Feather name={a.icon} size={20} color={fg} />
            <Text style={[styles.actionLabel, { color: fg }]}>{a.label}</Text>
          </RectButton>
        );
      })}
    </View>
  );

  return (
    <Swipeable
      ref={ref}
      renderRightActions={renderRight}
      overshootRight={false}
      friction={2}
      rightThreshold={40}
      containerStyle={{ backgroundColor: colors.surfaceSecondary }}
    >
      {children}
    </Swipeable>
  );
}

const styles = StyleSheet.create({
  actionsWrap: { flexDirection: "row" },
  action: {
    width: ACTION_WIDTH,
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
  },
  actionLabel: { fontFamily: fonts.bodyBold, fontSize: 12 },
});
