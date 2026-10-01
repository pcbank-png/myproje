import html
import os
import re
from html.parser import HTMLParser
from pathlib import Path

from argostranslate import translate


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "Services" / "LocalizationAdminSeedData.cs"
ADMIN_DIRS = tuple(ROOT / "Views" / name for name in (
    "Admin", "AdminCampaign", "AdminCampaigns", "AdminDealers", "AdminLanguages", "AdminVideos"
))
SHARED = (ROOT / "Views" / "Shared" / "_AdminLayout.cshtml", ROOT / "Views" / "Shared" / "_Alerts.cshtml")

TURKISH_HINT = re.compile(
    r"[çğıöşüÇĞİÖŞÜ]|\b(?:admin|api|client|critical|warning|info|live|online|offline|"
    r"ürün|lisans|sipariş|kayıt|cihaz|bayi|reklam|kampanya|video|dil|çeviri|müşteri|"
    r"kullanıcı|şifre|giriş|çıkış|dosya|görsel|başlık|açıklama|durum|tarih|aktif|pasif|"
    r"başarılı|başarısız|bekliyor|sil|düzenle|kaydet|ekle|iptal|geri|ara|filtrele|"
    r"tümü|yeni|henüz|anasayfa|yönetim|mesaj|ziyaretçi|istatistik|satış|havale)\b",
    re.IGNORECASE,
)
STRONG_LITERAL_HINT = re.compile(
    r"[çğıöşüÇĞİÖŞÜ]|(?i:\b(?:ürün|lisans|sipariş|kayıt|cihaz|bayi|reklam|kampanya|video|dil|çeviri|müşteri|"
    r"kullanıcı|şifre|giriş|çıkış|dosya|görsel|başlık|açıklama|durum|tarih|aktif|pasif|başarılı|başarısız|"
    r"bekliyor|sil|düzenle|kaydet|ekle|iptal|geri|ara|filtrele|tümü|yeni|henüz|anasayfa|yönetim|mesaj|"
    r"ziyaretçi|istatistik|satış|havale|yayın|uyarı|onay|fiyat|stok|kategori|indirim|üye|hesap)\b)",
)
UI_ALLOW = {
    "Admin Panel", "Admin menüsü", "Client", "Critical", "Warning", "Info", "Active", "Passive",
    "Pending", "Failed", "Offline", "Online", "Lifetime", "Endpoint", "API Test", "API Servisleri",
}
OVERRIDES = {
    "(Pasif)": ("(Inactive)", "(Inaktiv)", "(غير نشط)"),
    "API Anahtarı": ("API Key", "API-Schlüssel", "مفتاح API"),
    "API anahtarı ve offline lisans imza anahtarı durumu.": ("API key and offline license signing key status.", "Status des API-Schlüssels und des Offline-Lizenzsignaturschlüssels.", "حالة مفتاح API ومفتاح توقيع الترخيص دون اتصال."),
    "Active": ("Active", "Aktiv", "نشط"),
    "Admin Panel": ("Admin Panel", "Admin-Bereich", "لوحة الإدارة"),
    "Admin panel giriş ekranı": ("Admin panel login screen", "Anmeldeseite des Admin-Bereichs", "شاشة تسجيل الدخول إلى لوحة الإدارة"),
    "Aktif": ("Active", "Aktiv", "نشط"),
    "Aktif Kurulum İstatistikleri": ("Active Installation Statistics", "Statistik aktiver Installationen", "إحصاءات التثبيتات النشطة"),
    "Aktif müşteri": ("Active customer", "Aktiver Kunde", "عميل نشط"),
    "Aktif yayınla": ("Publish as active", "Aktiv veröffentlichen", "نشر كنشط"),
    "Aktif/Pasif": ("Active/Inactive", "Aktiv/Inaktiv", "نشط/غير نشط"),
    "Altı çizili": ("Underline", "Unterstrichen", "تسطير"),
    "Anasayfa": ("Dashboard", "Dashboard", "لوحة المعلومات"),
    "Anlık Canlı Destek": ("Real-Time Live Support", "Live-Support in Echtzeit", "دعم مباشر في الوقت الفعلي"),
    "Askıda": ("On Hold", "Angehalten", "معلّق"),
    "Bayi": ("Dealer", "Händler", "وكيل"),
    "Client": ("Website", "Website", "الموقع"),
    "Critical": ("Critical", "Kritisch", "حرج"),
    "Değişiklikleri Kaydet": ("Save Changes", "Änderungen speichern", "حفظ التغييرات"),
    "Düzenle": ("Edit", "Bearbeiten", "تعديل"),
    "Endpoint": ("Endpoint", "Endpunkt", "نقطة النهاية"),
    "Geri": ("Back", "Zurück", "رجوع"),
    "Güncelle": ("Update", "Aktualisieren", "تحديث"),
    "Güncelleme Paketini Kaydet": ("Save Update Package", "Update-Paket speichern", "حفظ حزمة التحديث"),
    "Havale": ("Bank Transfer", "Banküberweisung", "تحويل بنكي"),
    "Havale Bildirimleri": ("Bank Transfer Notifications", "Überweisungsmitteilungen", "إشعارات التحويل البنكي"),
    "Hayır": ("No", "Nein", "لا"),
    "Info": ("Info", "Info", "معلومات"),
    "Kategori": ("Category", "Kategorie", "الفئة"),
    "Kaydet": ("Save", "Speichern", "حفظ"),
    "Lifetime": ("Lifetime", "Lebenslang", "مدى الحياة"),
    "Manuel Satışı Kaydet": ("Save Manual Sale", "Manuellen Verkauf speichern", "حفظ البيع اليدوي"),
    "Mesaj": ("Message", "Nachricht", "رسالة"),
    "Metin biçimi": ("Text format", "Textformat", "تنسيق النص"),
    "Offline": ("Offline", "Offline", "دون اتصال"),
    "Offline İmza Anahtarı": ("Offline Signing Key", "Offline-Signaturschlüssel", "مفتاح التوقيع دون اتصال"),
    "Oluşturma": ("Created", "Erstellt", "تاريخ الإنشاء"),
    "Pasif": ("Inactive", "Inaktiv", "غير نشط"),
    "Pasif / Doğrulanmamış": ("Inactive / Unverified", "Inaktiv / Nicht verifiziert", "غير نشط / غير موثّق"),
    "Program Güncellemeleri": ("Program Updates", "Programm-Updates", "تحديثات البرنامج"),
    "Reklamı Kaydet": ("Save Advertisement", "Werbung speichern", "حفظ الإعلان"),
    "Sil": ("Delete", "Löschen", "حذف"),
    "Slider Kaydet": ("Save Slider", "Slider speichern", "حفظ الشريط"),
    "Sürüm": ("Version", "Version", "الإصدار"),
    "Sıra": ("Order", "Reihenfolge", "الترتيب"),
    "toplam kayıt": ("total records", "Datensätze insgesamt", "إجمالي السجلات"),
    "Videoyu Kaydet": ("Save Video", "Video speichern", "حفظ الفيديو"),
    "Warning": ("Warning", "Warnung", "تحذير"),
    "Yayınla": ("Publish", "Veröffentlichen", "نشر"),
    "Yönetim merkezi": ("Management center", "Verwaltungszentrale", "مركز الإدارة"),
    "Ziyaretçi İstatistikleri": ("Visitor Statistics", "Besucherstatistik", "إحصاءات الزوار"),
    "Ürün Kategorileri": ("Product Categories", "Produktkategorien", "فئات المنتجات"),
    "Ürün ve müşteri seçimi": ("Product and customer selection", "Produkt- und Kundenauswahl", "اختيار المنتج والعميل"),
    "Ürünü Kaydet": ("Save Product", "Produkt speichern", "حفظ المنتج"),
    "ödeme/sipariş": ("payment/order", "Zahlung/Bestellung", "الدفع/الطلب"),
    "İl": ("Province", "Provinz", "المحافظة"),
    "İtalik": ("Italic", "Kursiv", "مائل"),
    "İşlem": ("Action", "Aktion", "الإجراء"),
}
EXCLUDE = re.compile(
    r"^(?:https?://|/|#|\.|~/|[A-Za-z0-9_.-]+\.(?:cs|js|css|png|jpg|jpeg|webp|zip)|"
    r"[A-Za-z]+/[A-Za-z]|[A-Za-z0-9_-]{18,}|[a-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)$"
)


def strip_razor_blocks(value: str) -> str:
    result = []
    i = 0
    while i < len(value):
        if value.startswith("@{", i):
            depth, quote, escaped = 1, None, False
            i += 2
            while i < len(value) and depth:
                ch = value[i]
                if quote:
                    if escaped:
                        escaped = False
                    elif ch == "\\":
                        escaped = True
                    elif ch == quote:
                        quote = None
                elif ch in "\"'":
                    quote = ch
                elif ch == "{":
                    depth += 1
                elif ch == "}":
                    depth -= 1
                i += 1
            result.append(" ")
            continue
        result.append(value[i])
        i += 1
    return "".join(result)


class VisibleParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.values = []
        self.hidden_depth = 0

    def handle_starttag(self, tag, attrs):
        if tag.lower() in {"script", "style", "svg", "pre", "code", "textarea", "noscript"}:
            self.hidden_depth += 1
        if not self.hidden_depth:
            for name, value in attrs:
                if name.lower() in {"title", "aria-label", "placeholder", "alt"} and value:
                    self.values.append(value)

    def handle_endtag(self, tag):
        if tag.lower() in {"script", "style", "svg", "pre", "code", "textarea", "noscript"} and self.hidden_depth:
            self.hidden_depth -= 1

    def handle_data(self, data):
        if not self.hidden_depth:
            self.values.append(data)


def normalize(value: str) -> str:
    value = html.unescape(value)
    value = re.sub(r"\\u([0-9a-fA-F]{4})", lambda match: chr(int(match.group(1), 16)), value)
    value = re.sub(r"@(?:\([^)]*\)|[A-Za-z_][A-Za-z0-9_.?()]*)", " ", value)
    return re.sub(r"\s+", " ", value).strip(" \t\r\n-–|·")


def is_human_text(value: str) -> bool:
    if len(value) < 2 or len(value) > 800 or not re.search(r"[A-Za-zçğıöşüÇĞİÖŞÜ]", value):
        return False
    if EXCLUDE.match(value) or any(char in value for char in "<>{}=@\""):
        return False
    if value.startswith(("nsx-", "btn ")) or re.match(r"^nsx[A-Z]", value):
        return False
    if value.startswith(("/", ".", "~", "?", "!")) or re.search(r"(?:=>|&&|!=|==|\?\?|;|"
        r"\bvar\b|\bnew\b|\bstring\b|\bModel\b|\bViewBag\b|\bContext\b|\bHtml\b|"
        r"\bitem\.|\bform\.|\blicense\.|\bslider\.|\bproduct\.|\buser\.|\border\.|"
        r"document\.|querySelector|\.ToString|\.Is[A-Z]|\.HasValue|StringComparison|System\.)", value):
        return False
    if value.count("(") != value.count(")") or value.count("[") != value.count("]"):
        return False
    return bool(STRONG_LITERAL_HINT.search(value) or value in UI_ALLOW)


def extract_sources(path: Path) -> set[str]:
    raw = path.read_text(encoding="utf-8-sig")
    parser = VisibleParser()
    parser.feed(strip_razor_blocks(raw))
    candidates = {(value, False) for value in parser.values}

    # Titles, conditional labels and JavaScript notifications are not always HTML nodes.
    for match in re.finditer(r'(?<![A-Za-z0-9_])"((?:\\.|[^"\\])*)"', raw):
        value = match.group(1).replace(r'\"', '"').replace(r"\n", " ")
        if STRONG_LITERAL_HINT.search(value) or normalize(value) in UI_ALLOW:
            candidates.add((value, True))
    for match in re.finditer(r"(?<![A-Za-z0-9_])'((?:\\.|[^'\\])*)'", raw):
        value = match.group(1).replace(r"\'", "'").replace(r"\n", " ")
        if STRONG_LITERAL_HINT.search(value) or normalize(value) in UI_ALLOW:
            candidates.add((value, True))

    return {text for value, _ in candidates if (text := normalize(value)) and is_human_text(text)}


def existing_sources() -> set[str]:
    pattern = re.compile(r'\b(?:new|E)\(\s*"((?:\\.|[^"\\])*)"')
    values = set()
    for path in (ROOT / "Services").glob("*SeedData.cs"):
        if path == OUTPUT:
            continue
        for value in pattern.findall(path.read_text(encoding="utf-8-sig")):
            values.add(value.replace(r'\"', '"').replace(r"\\", "\\"))
    return values


def translate_values(values: list[str], source: str, target: str, batch_size: int = 8) -> list[str]:
    output = []
    for start in range(0, len(values), batch_size):
        batch = values[start:start + batch_size]
        translated = translate.translate("\n".join(batch), source, target).splitlines()
        if len(translated) != len(batch):
            translated = [translate.translate(value, source, target) for value in batch]
        output.extend(value.strip() for value in translated)
        if start % 96 == 0:
            print(f"{source}->{target}: {min(start + batch_size, len(values))}/{len(values)}", flush=True)
    return output


def encode(value: str) -> str:
    return value.replace("\\", r"\\").replace('"', r'\"').replace("\r", r"\r").replace("\n", r"\n")


def main() -> int:
    files = [path for directory in ADMIN_DIRS if directory.exists() for path in directory.rglob("*.cshtml")]
    files.extend(path for path in SHARED if path.exists())
    known = existing_sources()
    sources = sorted(set().union(*(extract_sources(path) for path in files)) - known)
    print(f"Admin sources to translate: {len(sources)}")

    english = translate_values(sources, "tr", "en")
    german = translate_values(english, "en", "de")
    arabic = translate_values(english, "en", "ar")

    for index, source in enumerate(sources):
        if source in OVERRIDES:
            english[index], german[index], arabic[index] = OVERRIDES[source]

    lines = [
        "namespace NSYazilim.Web.Services", "{",
        "    /// <summary>",
        "    /// Reviewed-at-build admin UI pack. Only source-code UI literals are included;",
        "    /// customer, order and payment data is never sent to a translation provider.",
        "    /// </summary>",
        "    public static class LocalizationAdminSeedData", "    {",
        "        public static readonly IReadOnlyList<LocalizationSeedItem> Items = new LocalizationSeedItem[]", "        {",
    ]
    for source, en, de, ar in zip(sources, english, german, arabic):
        lines.append(f'            new("{encode(source)}", "{encode(en)}", "{encode(de)}", "{encode(ar)}", "/Admin"),')
    lines.extend(["        };", "    }", "}", ""])
    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {len(sources)} entries to {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
