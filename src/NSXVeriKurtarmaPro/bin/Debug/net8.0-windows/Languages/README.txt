NSX Veri Kurtarma Pro - Dinamik Dil Paketi

Klasör: Uygulama ana dizini\Languages

Yeni dil eklemek için:
1) HER ZAMAN en-US.json dosyasını kanonik şablon olarak kopyalayın.
2) Yeni bir JSON oluşturun. code, name, nativeName ve flag alanlarını doldurun.
3) SVG bayrağı Languages\flags klasörüne kopyalayın.
4) translations bölümünde Türkçe kaynak metni anahtar, hedef dil metnini değer olarak kullanın.
5) JSON dosyasını Languages klasörüne kopyalayın.

Program Dil Seçiniz menüsü her açıldığında klasörü yeniden tarar. Uygulama çalışırken eklenen geçerli bir dil paketi yeniden başlatma gerektirmeden menüye gelir.
Eksik çeviri varsa Türkçe sızmaması için uygulama en-US.json içindeki İngilizce karşılığa düşer. Tam hedef dil için en-US.json içindeki tüm translation anahtarlarını yeni dil dosyanızda koruyup yalnız değerleri çevirin.

Örnek:
{
  "code": "de-DE",
  "name": "German",
  "nativeName": "Deutsch",
  "flag": "flags/de.svg",
  "translations": {
    "Ana Sayfa": "Startseite",
    "Hızlı Tarama": "Schnellscan"
  }
}

DINAMIK METIN / PLACEHOLDER
-------------------------
Dinamik sayi, cihaz adi ve kapasite metinlerinde {0}, {1}, {2} kullanin.
Ornek: "{0} bos / {1} toplam" -> "{0} free / {1} total".
Motor placeholder kaliplarini otomatik algilar ve dil degisince ekrani aninda yeniler.


KANONİK DİL STANDARDI
----------------------
- en-US.json ana anahtar listesidir. Yeni diller bu dosya kopyalanarak hazırlanır.
- translations anahtarları değiştirilmez; yalnız değerler hedef dile çevrilir.
- Dinamik {0}, {1}, {2} placeholder'ları aynen korunur.
- Dil seçildiğinde statik XAML, disk kartları, sol-alt durum alanı, tarama/progress ve sonuç metinleri canlı yenilenir.
- Bir hedef dil anahtarı eksikse Türkçe yerine en-US karşılığı gösterilir.
- Logo kuralı: yalnız tr-* dillerinde Assets/logo_ui.png; diğer tüm dillerde Assets/logo_ui_en.png kullanılır.
