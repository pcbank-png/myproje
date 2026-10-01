import concurrent.futures
import html
import re
import sys
import threading
import time
from pathlib import Path

import requests


ROOT = Path(__file__).resolve().parents[1]
SERVICES = ROOT / "Services"
OUTPUT = SERVICES / "LocalizationArabicStrictSeedData.cs"
ENTRY_RE = re.compile(
    r'\bnew\(\s*"(?P<source>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<en>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<de>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<ar>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<area>(?:\\.|[^"\\])*)"\s*\)',
    re.DOTALL,
)
LATIN_RE = re.compile(r"[A-Za-z]")

PHRASES = {
    "NSX Security Auditor Pro": "مدقق الأمان الاحترافي من إن إس إكس",
    "NSX Security Auditors Pro": "مدقق الأمان الاحترافي من إن إس إكس",
    "Security Auditor Pro": "مدقق الأمان الاحترافي",
    "NSX Kasa Defteri Pro": "دفتر الصندوق الاحترافي من إن إس إكس",
    "NSX Case Book Pro": "دفتر الصندوق الاحترافي من إن إس إكس",
    "NSX Okul Plan Pro": "مخطط المدارس الاحترافي من إن إس إكس",
    "NSX School Plan Pro": "مخطط المدارس الاحترافي من إن إس إكس",
    "NSX Sigorta Acente Pro": "وكالة التأمين الاحترافية من إن إس إكس",
    "NSX Cari Takip Pro": "تتبع الحسابات الاحترافي من إن إس إكس",
    "NSX Teknik Servis Pro": "إدارة الصيانة الاحترافية من إن إس إكس",
    "NSX Technical Service Pro": "إدارة الصيانة الاحترافية من إن إس إكس",
    "NSX Veresiye Takip Pro": "تتبع الديون الاحترافي من إن إس إكس",
    "NSX Komisyonlu Cari Takip Pro": "تتبع الحسابات بالعمولة الاحترافي من إن إس إكس",
    "NSX ServisPro Live": "خدمة الصيانة المباشرة من إن إس إكس",
    "NSX Service Pro Live": "خدمة الصيانة المباشرة من إن إس إكس",
    "NSX Software": "برمجيات إن إس إكس",
    "NSX Yazılım": "برمجيات إن إس إكس",
    "Microsoft Defender SmartScreen": "مايكروسوفت ديفندر سمارت سكرين",
    "Windows SmartScreen": "سمارت سكرين في ويندوز",
    "Google Cloud": "سحابة جوجل",
    "DeepL": "ديب إل",
    "WhatsApp": "واتساب",
    "Instagram": "إنستغرام",
    "Facebook": "فيسبوك",
    "YouTube": "يوتيوب",
    "TikTok": "تيك توك",
    "Windows": "ويندوز",
    "Microsoft": "مايكروسوفت",
    "SmartScreen": "سمارت سكرين",
    "Defender": "ديفندر",
    "Excel": "إكسل",
    "Server": "الخادم",
    "Client": "العميل",
    "Cloud": "السحابة",
    "Software": "البرمجيات",
    "Security": "الأمان",
    "Auditor": "المدقق",
    "Pro": "الاحترافي",
    "Live": "مباشر",
    "PDF": "بي دي إف",
    "JSON": "جيسون",
    "HTML": "إتش تي إم إل",
    "SEO": "تحسين محركات البحث",
    "API": "واجهة برمجة التطبيقات",
    "SMS": "رسائل نصية",
    "QR": "كيو آر",
    "NSX": "إن إس إكس",
}

CHAR_MAP = {
    "a": "ا", "b": "ب", "c": "ك", "d": "د", "e": "ي", "f": "ف", "g": "ج",
    "h": "ه", "i": "ي", "j": "ج", "k": "ك", "l": "ل", "m": "م", "n": "ن",
    "o": "و", "p": "ب", "q": "ك", "r": "ر", "s": "س", "t": "ت", "u": "و",
    "v": "ف", "w": "و", "x": "كس", "y": "ي", "z": "ز",
}

SOURCE_OVERRIDES = {
    "NSX Barkodlu Satış Pro": "المبيعات بالباركود الاحترافية من إن إس إكس",
    "NSX Cari Takip Pro": "تتبع الحسابات الاحترافي من إن إس إكس",
    "NSX Düğün Salonu Pro": "إدارة قاعات الأفراح الاحترافية من إن إس إكس",
    "NSX Kasa Defteri Pro": "دفتر الصندوق الاحترافي من إن إس إكس",
    "NSX Komisyonlu Cari Takip Pro": "تتبع الحسابات بالعمولة الاحترافي من إن إس إكس",
    "NSX Okul Plan Pro": "مخطط المدارس الاحترافي من إن إس إكس",
    "NSX Oto Galeri Pro": "إدارة معارض السيارات الاحترافية من إن إس إكس",
    "NSX Oto Tamir Servis Pro": "إدارة صيانة السيارات الاحترافية من إن إس إكس",
    "NSX Performance Pro": "تحسين الأداء الاحترافي من إن إس إكس",
    "NSX Security Auditor Pro": "مدقق الأمان الاحترافي من إن إس إكس",
    "NSX Servis Pro": "إدارة الصيانة الاحترافية من إن إس إكس",
    "NSX Sigorta Acenta Pro": "وكالة التأمين الاحترافية من إن إس إكس",
    "NSX Sigorta Acente Pro": "وكالة التأمين الاحترافية من إن إس إكس",
    "NSX Teknik Servis Pro": "إدارة الصيانة التقنية الاحترافية من إن إس إكس",
    "NSX Turbo Performans Pro": "تسريع الأداء الاحترافي من إن إس إكس",
    "NSX Ücretsiz Veresiye Pro": "تتبع الديون المجاني من إن إس إكس",
    "NSX Veresiye Pro": "تتبع الديون الاحترافي من إن إس إكس",
    "NSX Veresiye Takip Pro": "تتبع الديون الاحترافي من إن إس إكس",
}


def decode(value: str) -> str:
    return html.unescape(
        value.replace(r"\n", "\n").replace(r"\r", "\r").replace(r"\t", "\t")
        .replace(r'\"', '"').replace(r"\\", "\\")
    ).strip()


def encode(value: str) -> str:
    return (
        value.replace("\\", r"\\").replace('"', r'\"')
        .replace("\r", r"\r").replace("\n", r"\n").replace("\t", r"\t")
    )


def load_entries() -> list[tuple[str, str]]:
    entries = {}
    for path in SERVICES.glob("*SeedData.cs"):
        if path == OUTPUT:
            continue
        content = path.read_text(encoding="utf-8-sig")
        for match in ENTRY_RE.finditer(content):
            source = decode(match.group("source"))
            arabic = decode(match.group("ar"))
            if source and LATIN_RE.search(arabic):
                entries.setdefault(source, decode(match.group("area")) or "/")
    return sorted(entries.items(), key=lambda item: (item[1], item[0]))


class BingTranslator:
    def __init__(self):
        self.session = requests.Session()
        self.refresh()

    def refresh(self):
        page = self.session.get("https://www.bing.com/translator", timeout=30).text
        self.ig = re.search(r'IG:"([^"]+)"', page).group(1)
        match = re.search(r'params_AbusePreventionHelper\s*=\s*\[(\d+),"([^"]+)"', page)
        self.key, self.token = match.group(1), match.group(2)

    def translate(self, source: str) -> str:
        url = f"https://www.bing.com/ttranslatev3?isVertical=1&IG={self.ig}&IID=translator.5028.1"
        response = self.session.post(
            url,
            data={"fromLang": "tr", "text": source, "to": "ar", "token": self.token, "key": self.key},
            timeout=30,
        )
        response.raise_for_status()
        return response.json()[0]["translations"][0]["text"].strip()


THREAD_STATE = threading.local()


def get_translator() -> BingTranslator:
    translator = getattr(THREAD_STATE, "translator", None)
    if translator is None:
        translator = BingTranslator()
        THREAD_STATE.translator = translator
    return translator


def arabicize_latin(value: str) -> str:
    value = html.unescape(value)
    for latin, arabic in sorted(PHRASES.items(), key=lambda item: len(item[0]), reverse=True):
        value = re.sub(re.escape(latin), arabic, value, flags=re.IGNORECASE)
    value = re.sub(r"\bv(?=\d)", "الإصدار ", value, flags=re.IGNORECASE)

    def transliterate(match: re.Match) -> str:
        return "".join(CHAR_MAP.get(char.lower(), "") for char in match.group(0))

    value = re.sub(r"[A-Za-z]+", transliterate, value)
    value = re.sub(r"\bبرو\b", "الاحترافي", value)
    return re.sub(r"\s+", " ", value).strip()


def load_existing_output() -> dict[str, str]:
    if not OUTPUT.exists():
        return {}
    result = {}
    for match in ENTRY_RE.finditer(OUTPUT.read_text(encoding="utf-8-sig")):
        result[decode(match.group("source"))] = decode(match.group("ar"))
    return result


def translate_one(source: str) -> str:
    translator = get_translator()
    for attempt in range(3):
        try:
            return arabicize_latin(translator.translate(source))
        except Exception:
            if attempt == 2:
                raise
            time.sleep(1 + attempt)
            translator.refresh()
    raise RuntimeError("unreachable")


def main() -> int:
    entries = load_entries()
    translated = {source: arabicize_latin(value) for source, value in load_existing_output().items()}
    pending = [(source, area) for source, area in entries if source not in translated]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = {pool.submit(translate_one, source): source for source, _ in pending}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            source = futures[future]
            translated[source] = future.result()
            print(f"ar: {index}/{len(pending)}", flush=True)

    translated.update(SOURCE_OVERRIDES)

    lines = [
        "namespace NSYazilim.Web.Services",
        "{",
        "    /// <summary>",
        "    /// Strict Arabic overrides. Every visible target is Arabic-script only; product",
        "    /// names, NSX and Pro labels are localized instead of leaking Latin text.",
        "    /// </summary>",
        "    public static class LocalizationArabicStrictSeedData",
        "    {",
        "        public static readonly IReadOnlyList<LocalizationSeedItem> Items = new LocalizationSeedItem[]",
        "        {",
    ]
    for source, area in entries:
        arabic = translated[source]
        if LATIN_RE.search(arabic):
            raise RuntimeError(f"Latin text remained for {source!r}: {arabic!r}")
        lines.append(f'            new("{encode(source)}", "", "", "{encode(arabic)}", "{encode(area)}"),')
    lines.extend(["        };", "    }", "}", ""])
    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {len(entries)} strict Arabic entries to {OUTPUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
