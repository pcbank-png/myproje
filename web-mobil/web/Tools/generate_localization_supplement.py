import html
import re
import sys
from pathlib import Path

from argostranslate import translate


ROOT = Path(__file__).resolve().parents[1]
SERVICES = ROOT / "Services"
OUTPUT = SERVICES / "LocalizationGermanArabicExtendedSeedData.cs"

ENGLISH_ONLY_FILES = (
    "LocalizationEnglishAccountSeedData.cs",
    "LocalizationEnglishBankTransferSeedData.cs",
    "LocalizationEnglishContentSeedData.cs",
    "LocalizationEnglishDealerSeedData.cs",
    "LocalizationEnglishExtendedSeedData.cs",
    "LocalizationEnglishSharedSeedData.cs",
)

ENTRY_RE = re.compile(
    r'\bE\(\s*"(?P<source>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<english>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<area>(?:\\.|[^"\\])*)"\s*\)',
    re.DOTALL,
)


def decode_csharp(value: str) -> str:
    replacements = {
        r"\\": "\\",
        r'\"': '"',
        r"\r": "\r",
        r"\n": "\n",
        r"\t": "\t",
    }
    for old, new in replacements.items():
        value = value.replace(old, new)
    return value


def encode_csharp(value: str) -> str:
    return (
        value.replace("\\", r"\\")
        .replace('"', r'\"')
        .replace("\r", r"\r")
        .replace("\n", r"\n")
        .replace("\t", r"\t")
    )


def translate_in_batches(values: list[str], target: str, batch_size: int = 24) -> list[str]:
    results: list[str] = []
    for start in range(0, len(values), batch_size):
        batch = values[start : start + batch_size]
        joined = "\n".join(text.replace("\n", " ") for text in batch)
        translated = translate.translate(joined, "en", target).splitlines()
        if len(translated) != len(batch):
            translated = [translate.translate(text, "en", target) for text in batch]
        results.extend(item.strip() for item in translated)
        print(f"{target}: {min(start + batch_size, len(values))}/{len(values)}", flush=True)
    return results


def main() -> int:
    entries: dict[str, tuple[str, str]] = {}
    for filename in ENGLISH_ONLY_FILES:
        content = (SERVICES / filename).read_text(encoding="utf-8-sig")
        for match in ENTRY_RE.finditer(content):
            source = html.unescape(decode_csharp(match.group("source"))).strip()
            english = html.unescape(decode_csharp(match.group("english"))).strip()
            area = decode_csharp(match.group("area")).strip()
            if source and english:
                entries.setdefault(source, (english, area))

    ordered = sorted(entries.items(), key=lambda item: (item[1][1], item[0]))
    english_values = [data[0] for _, data in ordered]
    german_values = translate_in_batches(english_values, "de")
    arabic_values = translate_in_batches(english_values, "ar")

    lines = [
        "namespace NSYazilim.Web.Services",
        "{",
        "    /// <summary>",
        "    /// Native German and Arabic completion pack for customer-facing entries whose",
        "    /// original reviewed package only contained English. Generated once and shipped",
        "    /// as deterministic seed data; no translation service is needed at runtime.",
        "    /// </summary>",
        "    public static class LocalizationGermanArabicExtendedSeedData",
        "    {",
        "        public static readonly IReadOnlyList<LocalizationSeedItem> Items = new LocalizationSeedItem[]",
        "        {",
    ]
    for ((source, (english, area)), german, arabic) in zip(ordered, german_values, arabic_values):
        lines.append(
            '            new("{}", "{}", "{}", "{}", "{}"),'.format(
                encode_csharp(source),
                encode_csharp(english),
                encode_csharp(german),
                encode_csharp(arabic),
                encode_csharp(area),
            )
        )
    lines.extend(["        };", "    }", "}", ""])
    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {len(ordered)} entries to {OUTPUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
