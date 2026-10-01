// Yardım Merkezi — modern kart yapısında SSS + iletişim.
// Kullanıcı teknik / API mesajı görmez.
import React, { useState } from "react";
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  Pressable,
  Linking,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Feather from "@react-native-vector-icons/feather";
import Animated, { LinearTransition } from "react-native-reanimated";
import { ScreenHeader } from "@/src/components/screen-header";
import { useTheme, spacing, fonts } from "@/src/theme";
import { selectionHaptic } from "@/src/utils/haptics";

type IconName = React.ComponentProps<typeof Feather>["name"];

interface Faq {
  q: string;
  a: string;
  icon: IconName;
}

const FAQS: Faq[] = [
  {
    icon: "camera",
    q: "QR ile bağlantı nasıl yapılır?",
    a: "Masaüstü NSX Cari Takip programında “QR ile Bağla” menüsünden kısa süreli bir eşleştirme kodu oluşturun. Mobil uygulamada QR Kodu Tara butonuna dokunun ve kodu çerçeveye hizalayın. Kamera kullanamıyorsanız, kodu manuel olarak da girebilirsiniz.",
  },
  {
    icon: "user-plus",
    q: "Yeni müşteri nasıl eklenir?",
    a: "Ana Sayfa'daki Yeni Müşteri hızlı aksiyonuna veya Müşteriler ekranındaki + butonuna dokunun. Ad zorunlu, telefon/e-posta/not opsiyonel. Kaydettikten sonra müşteri hemen listede görünür.",
  },
  {
    icon: "arrow-down-left",
    q: "Borç veya Tahsilat nasıl eklenir?",
    a: "Ana Sayfa'daki Tahsilat Yap veya Borç Ekle hızlı aksiyonlarına dokunun. Ya da bir müşterinin cari kartını açıp Tahsilat Al / Borç Ekle butonlarını kullanın. Tutarı girin, tarihi seçin, isteğe bağlı açıklama ekleyin. Ana sayfa: Alacak − Tahsilat = Net Bakiye.",
  },
  {
    icon: "share-2",
    q: "İkinci cihaz nasıl davet edilir?",
    a: "Ayarlar → Mobil Erişim → Cihaz Davet Et adımlarını izleyin. Uygulama size özel, tek kullanımlık ve kısa süreli bir QR + kod üretir; bunu WhatsApp/Mesaj/E-posta ile paylaşabilirsiniz. Karşı taraf mobil uygulamada bu kodu okutarak aynı hesaba bağlanır.",
  },
  {
    icon: "refresh-cw",
    q: "Senkronizasyon sorunları",
    a: "Uygulama arka planda gerçek zamanlı senkronizasyon kullanır. Verilerin güncellenmediğini düşünüyorsanız listeleri aşağı çekerek manuel yenileyebilirsiniz. Sürekli sorun yaşıyorsanız Ayarlar → Yedekleme ekranından bağlantı durumunu kontrol edin.",
  },
  {
    icon: "log-out",
    q: "Nasıl çıkış yaparım?",
    a: "Ayarlar ekranının en altındaki Oturumu Kapat butonuna dokunun. Yeniden giriş için masaüstünden yeni bir QR kodu okutmanız gerekir.",
  },
];

export default function HelpScreen() {
  const insets = useSafeAreaInsets();
  const { colors } = useTheme();
  const [openIdx, setOpenIdx] = useState<number | null>(0);

  return (
    <View style={[styles.root, { backgroundColor: colors.surface }]} testID="help-screen">
      <ScreenHeader title="Yardım Merkezi" />
      <ScrollView
        contentContainerStyle={{
          paddingHorizontal: spacing.lg,
          paddingBottom: insets.bottom + 32,
          paddingTop: spacing.md,
        }}
      >
        <Text style={[styles.sectionTitle, { color: colors.muted }]}>SIK SORULAN SORULAR</Text>
        <View style={{ gap: 10 }}>
          {FAQS.map((f, idx) => {
            const open = openIdx === idx;
            return (
              <Animated.View
                key={f.q}
                layout={LinearTransition.duration(180)}
                style={[
                  styles.card,
                  { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
                ]}
              >
                <Pressable
                  testID={`faq-toggle-${idx}`}
                  onPress={() => {
                    selectionHaptic();
                    setOpenIdx(open ? null : idx);
                  }}
                  style={({ pressed }) => [
                    styles.qRow,
                    { opacity: pressed ? 0.85 : 1 },
                  ]}
                >
                  <View
                    style={[
                      styles.iconWrap,
                      {
                        backgroundColor: open ? colors.brandPrimary : colors.brandTertiary,
                      },
                    ]}
                  >
                    <Feather
                      name={f.icon}
                      size={16}
                      color={open ? colors.onBrandPrimary : colors.onBrandTertiary}
                    />
                  </View>
                  <Text style={[styles.qText, { color: colors.onSurface }]}>{f.q}</Text>
                  <Feather
                    name={open ? "chevron-up" : "chevron-down"}
                    size={18}
                    color={colors.muted}
                  />
                </Pressable>
                {open ? (
                  <Text style={[styles.aText, { color: colors.muted }]}>{f.a}</Text>
                ) : null}
              </Animated.View>
            );
          })}
        </View>

        <Text style={[styles.sectionTitle, { color: colors.muted, marginTop: spacing.xl }]}>
          İLETİŞİM
        </Text>
        <View
          style={[
            styles.card,
            { backgroundColor: colors.surfaceSecondary, borderColor: colors.border },
          ]}
        >
          <ContactRow
            icon="globe"
            label="NSX Yazılım"
            hint="nsxyazilim.com"
            onPress={() => Linking.openURL("https://www.nsxyazilim.com").catch(() => {})}
            testID="contact-web"
          />
          <ContactRow
            icon="mail"
            label="Destek E-postası"
            hint="destek@nsxyazilim.com"
            onPress={() =>
              Linking.openURL("mailto:destek@nsxyazilim.com").catch(() => {})
            }
            testID="contact-mail"
            last
          />
        </View>
      </ScrollView>
    </View>
  );
}

interface ContactProps {
  icon: IconName;
  label: string;
  hint?: string;
  onPress: () => void;
  testID?: string;
  last?: boolean;
}

function ContactRow({ icon, label, hint, onPress, testID, last }: ContactProps) {
  const { colors } = useTheme();
  return (
    <Pressable
      testID={testID}
      onPress={onPress}
      style={({ pressed }) => [
        styles.contactRow,
        {
          borderBottomColor: colors.divider,
          borderBottomWidth: last ? 0 : StyleSheet.hairlineWidth,
          opacity: pressed ? 0.85 : 1,
        },
      ]}
    >
      <View style={[styles.iconWrap, { backgroundColor: colors.brandTertiary }]}>
        <Feather name={icon} size={16} color={colors.onBrandTertiary} />
      </View>
      <View style={{ flex: 1 }}>
        <Text style={[styles.contactLabel, { color: colors.onSurface }]}>{label}</Text>
        {hint ? <Text style={[styles.contactHint, { color: colors.muted }]}>{hint}</Text> : null}
      </View>
      <Feather name="external-link" size={16} color={colors.borderStrong} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  sectionTitle: {
    fontFamily: fonts.bodySemiBold,
    fontSize: 11,
    letterSpacing: 0.5,
    marginTop: spacing.md,
    marginBottom: spacing.sm,
    marginLeft: 4,
  },
  card: { borderRadius: 16, borderWidth: 1, overflow: "hidden" },
  qRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 14,
  },
  iconWrap: {
    width: 32,
    height: 32,
    borderRadius: 10,
    alignItems: "center",
    justifyContent: "center",
  },
  qText: { flex: 1, fontFamily: fonts.bodySemiBold, fontSize: 14 },
  aText: {
    fontFamily: fonts.body,
    fontSize: 13,
    lineHeight: 20,
    paddingHorizontal: 14,
    paddingBottom: 14,
    paddingTop: 2,
  },
  contactRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 14,
  },
  contactLabel: { fontFamily: fonts.bodySemiBold, fontSize: 15 },
  contactHint: { fontFamily: fonts.body, fontSize: 12, marginTop: 2 },
});
