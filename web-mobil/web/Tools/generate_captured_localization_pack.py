import json
import re
import sys
from pathlib import Path

import pymysql
from argostranslate import translate


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "Services" / "LocalizationCapturedContentSeedData.cs"
TURKISH_EXCLUSIVE = re.compile(r"[ğşıİĞŞ]")
TURKISH_WORDS = re.compile(
    r"\b(?:ve|için|ile|yazılım|ürün|ücretsiz|müşteri|güvenli|görseli|büyüt|"
    r"çerez|tümü|programı|hakkımızda|iletişim|iade|işletme|canlı|nöbet|okul)\b",
    re.IGNORECASE,
)
DO_NOT_TRANSLATE = {
    "Türkçe",
    "Aynur SÜRÜCÜ",
    "Nevzat SÜRÜCÜ",
}

OVERRIDES = {
    "Şifre ve Güvenlik | NSX Yazılım": ("Password and Security | NSX Software", "Passwort und Sicherheit | NSX Software", "كلمة المرور والأمان | NSX Software"),
    "İndirmelerim | NSX Yazılım": ("My Downloads | NSX Software", "Meine Downloads | NSX Software", "تنزيلاتي | NSX Software"),
    "E-postanızı Kontrol Edin | NSX Yazılım": ("Check Your Email | NSX Software", "Prüfen Sie Ihre E-Mail | NSX Software", "تحقق من بريدك الإلكتروني | NSX Software"),
    "Lisanslarım | NSX Yazılım": ("My Licenses | NSX Software", "Meine Lizenzen | NSX Software", "تراخيصي | NSX Software"),
    "Üye Girişi | NSX Yazılım": ("Member Login | NSX Software", "Mitglieder-Login | NSX Software", "تسجيل دخول الأعضاء | NSX Software"),
    "Hesabım | NSX Yazılım": ("My Account | NSX Software", "Mein Konto | NSX Software", "حسابي | NSX Software"),
    "Siparişlerim | NSX Yazılım": ("My Orders | NSX Software", "Meine Bestellungen | NSX Software", "طلباتي | NSX Software"),
    "Ücretsiz Üyelik | NSX Yazılım": ("Free Membership | NSX Software", "Kostenlose Mitgliedschaft | NSX Software", "عضوية مجانية | NSX Software"),
    "NSX Yazılım Blog | İşletme ve Yazılım Rehberleri": ("NSX Software Blog | Business and Software Guides", "NSX Software Blog | Ratgeber für Unternehmen und Software", "مدونة NSX Software | أدلة الأعمال والبرمجيات"),
    "Hakkımızda | NSX Yazılım": ("About Us | NSX Software", "Über uns | NSX Software", "من نحن | NSX Software"),
    "İade Politikası | NSX Yazılım": ("Refund Policy | NSX Software", "Rückerstattungsrichtlinie | NSX Software", "سياسة الاسترداد | NSX Software"),
    "İletişim ve Destek | NSX Yazılım": ("Contact and Support | NSX Software", "Kontakt und Support | NSX Software", "التواصل والدعم | NSX Software"),
    "KVKK Aydınlatma Metni | NSX Yazılım": ("KVKK Privacy Notice | NSX Software", "KVKK-Datenschutzhinweis | NSX Software", "إشعار الخصوصية وفق KVKK | NSX Software"),
    "Randevu Programı | Klinik ve İşletme Randevu Takibi": ("Appointment Software | Clinic and Business Appointment Tracking", "Terminsoftware | Terminverwaltung für Kliniken und Unternehmen", "برنامج المواعيد | متابعة مواعيد العيادات والأعمال"),
    "Servis Live Programı | Canlı Servis ve İş Takibi | NSX Yazılım": ("Service Live Software | Live Service and Job Tracking | NSX Software", "Service-Live-Software | Live-Service- und Auftragsverfolgung | NSX Software", "برنامج Service Live | متابعة الخدمة والعمل مباشرة | NSX Software"),
    "Ücretsiz Bilgisayar Hızlandırma Programı | PC Performans": ("Free PC Speed-Up Software | PC Performance", "Kostenlose PC-Beschleunigungssoftware | PC-Leistung", "برنامج مجاني لتسريع الكمبيوتر | أداء الكمبيوتر"),
    "Nöbet Planlama Merkezi:": ("Duty Scheduling Center:", "Zentrum für Aufsichtsplanung:", "مركز تخطيط المناوبات:"),
    "NSX Okul Plan Pro | Ders Programı ve Nöbet Planlama": ("NSX School Plan Pro | Timetable and Duty Scheduling", "NSX Schulplan Pro | Stundenplan- und Aufsichtsplanung", "NSX School Plan Pro | تخطيط الجداول والمناوبات"),
    "NSX Okul Plan Pro ile Ders ve Nöbet Planlamasını Tek Merkezde Yönetin": ("Manage Timetables and Duty Scheduling Centrally with NSX School Plan Pro", "Stundenpläne und Aufsichten zentral mit NSX Schulplan Pro verwalten", "أدر الجداول والمناوبات من مركز واحد باستخدام NSX School Plan Pro"),
    "NSX Okul Plan Pro ile Ders ve Nöbet Planlamasını Tek Merkezde Yönetin için NSX Yazılım ürün görseli": ("NSX Software product image for centrally managing timetables and duty scheduling with NSX School Plan Pro", "NSX-Software-Produktbild zur zentralen Verwaltung von Stundenplänen und Aufsichten mit NSX Schulplan Pro", "صورة منتج NSX Software لإدارة الجداول والمناوبات مركزيًا باستخدام NSX School Plan Pro"),
    "NSX Okul Plan Pro ile Ders ve Nöbet Planlamasını Tek Merkezde Yönetin rehberini oku": ("Read the guide to managing timetables and duty scheduling centrally with NSX School Plan Pro", "Ratgeber zur zentralen Verwaltung von Stundenplänen und Aufsichten mit NSX Schulplan Pro lesen", "اقرأ دليل إدارة الجداول والمناوبات مركزيًا باستخدام NSX School Plan Pro"),
    "Klinik Programı | Hasta, Doktor ve Klinik Takip Yazılımı": ("Clinic Software | Patient, Doctor and Clinic Management", "Kliniksoftware | Patienten-, Arzt- und Klinikverwaltung", "برنامج العيادات | إدارة المرضى والأطباء والعيادة"),
    "Klinik Programı için NSX Yazılım ürün görseli": ("NSX Software product image for Clinic Software", "NSX-Software-Produktbild für die Kliniksoftware", "صورة منتج NSX Software لبرنامج العيادات"),
    "Canlı senkronizasyon": ("Live synchronization", "Live-Synchronisierung", "مزامنة مباشرة"),
}


def connection_options() -> dict:
    settings = json.loads((ROOT / "appsettings.json").read_text(encoding="utf-8-sig"))
    raw = settings["ConnectionStrings"]["DefaultConnection"]
    values = {}
    for part in raw.split(";"):
        if "=" not in part:
            continue
        key, value = part.split("=", 1)
        values[key.strip().lower()] = value.strip()
    return {
        "host": values.get("server", values.get("host", "localhost")),
        "port": int(values.get("port", "3306")),
        "user": values.get("user", values.get("user id", "")),
        "password": values.get("password", ""),
        "database": values.get("database", ""),
        "charset": "utf8mb4",
    }


def existing_static_sources() -> set[str]:
    entry = re.compile(r'\b(?:new|E)\(\s*"((?:\\.|[^"\\])*)"')
    sources = set()
    for path in (ROOT / "Services").glob("*SeedData.cs"):
        if path == OUTPUT:
            continue
        for value in entry.findall(path.read_text(encoding="utf-8-sig")):
            sources.add(value.replace(r'\"', '"').replace(r"\\", "\\"))
    return sources


def load_missing_sources() -> list[tuple[str, str]]:
    sql = """
        SELECT r.SourceText, COALESCE(r.FirstSeenPath, '/')
        FROM LocalizationResources r
        WHERE r.IsIgnored = 0
        ORDER BY r.FirstSeenPath, r.SourceText
    """
    with pymysql.connect(**connection_options()) as connection:
        with connection.cursor() as cursor:
            cursor.execute(sql)
            rows = cursor.fetchall()
    static_sources = existing_static_sources()
    result = []
    for source, area in rows:
        source = re.sub(r"\s+", " ", source).strip()
        if source in DO_NOT_TRANSLATE or source in static_sources:
            continue
        if not TURKISH_EXCLUSIVE.search(source) and not TURKISH_WORDS.search(source):
            continue
        result.append((source, area or "/"))
    return result


def translate_in_batches(values: list[str], source: str, target: str, batch_size: int = 8) -> list[str]:
    results: list[str] = []
    for start in range(0, len(values), batch_size):
        batch = values[start : start + batch_size]
        translated = translate.translate("\n".join(batch), source, target).splitlines()
        if len(translated) != len(batch):
            translated = [translate.translate(text, source, target) for text in batch]
        results.extend(text.strip() for text in translated)
        print(f"{source}->{target}: {min(start + batch_size, len(values))}/{len(values)}", flush=True)
    return results


def encode_csharp(value: str) -> str:
    return (
        value.replace("\\", r"\\")
        .replace('"', r'\"')
        .replace("\r", r"\r")
        .replace("\n", r"\n")
        .replace("\t", r"\t")
    )


def main() -> int:
    entries = load_missing_sources()
    sources = [source for source, _ in entries]
    english = translate_in_batches(sources, "tr", "en")
    german = translate_in_batches(english, "en", "de")
    arabic = translate_in_batches(english, "en", "ar")

    for index, source in enumerate(sources):
        if source in OVERRIDES:
            english[index], german[index], arabic[index] = OVERRIDES[source]

    lines = [
        "namespace NSYazilim.Web.Services",
        "{",
        "    /// <summary>",
        "    /// Four-language pack for rendered page titles, SEO text, article content and",
        "    /// other customer-facing strings discovered by the localization capture engine.",
        "    /// </summary>",
        "    public static class LocalizationCapturedContentSeedData",
        "    {",
        "        public static readonly IReadOnlyList<LocalizationSeedItem> Items = new LocalizationSeedItem[]",
        "        {",
    ]
    for ((source, area), en, de, ar) in zip(entries, english, german, arabic):
        lines.append(
            '            new("{}", "{}", "{}", "{}", "{}"),'.format(
                encode_csharp(source),
                encode_csharp(en),
                encode_csharp(de),
                encode_csharp(ar),
                encode_csharp(area),
            )
        )
    lines.extend(["        };", "    }", "}", ""])
    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {len(entries)} entries to {OUTPUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
