# NSX Cari Takip Pro Mobile - Para Formati Standardi

Tarih: 2026-10-01

Tum mobil uygulamada parasal gosterim tek kurala baglandi:

- Binlik ayiraci: nokta
- Ondalik ayiraci: virgul
- Ondalik basamak: her zaman 2
- Para birimi: tutarin sonunda `TL`
- Kisaltma yok: `B`, `M` veya yuvarlatilmis KPI gosterimi kullanilmaz

Ornekler:

- `95.370,00 TL`
- `1.234.567,80 TL`
- `0,00 TL`
- `-95.370,00 TL`

Kapsam:

- Ana sayfa buyuk Net Bakiye
- Ana sayfa Alacak / Tahsilat / Net Bakiye uclusu
- Musteri listesi bakiye tutarlari
- Musteri detay Net Bakiye / Toplam Borc / Toplam Tahsilat
- Hareket listesi borc ve tahsilat tutarlari
- MoneyText kullanan diger KPI ve finansal alanlar
- Tutar giris etiketi `Tutar (TL)` olarak standardize edildi

Teknik olarak tum finansal gosterim `src/utils/format.ts -> formatTRY()` ve `MoneyText` uzerinden tek kaynaktan yonetilir.
