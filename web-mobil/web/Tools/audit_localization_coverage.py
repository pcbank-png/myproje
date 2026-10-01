import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICES = ROOT / "Services"

FULL_RE = re.compile(
    r'\bnew\(\s*"(?P<source>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<en>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<de>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<ar>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<area>(?:\\.|[^"\\])*)"\s*\)',
    re.DOTALL,
)
ENGLISH_RE = re.compile(
    r'\bE\(\s*"(?P<source>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<en>(?:\\.|[^"\\])*)"\s*,\s*'
    r'"(?P<area>(?:\\.|[^"\\])*)"\s*\)',
    re.DOTALL,
)


def decode(value: str) -> str:
    return (
        value.replace(r"\n", "\n")
        .replace(r"\r", "\r")
        .replace(r"\t", "\t")
        .replace(r'\"', '"')
        .replace(r"\\", "\\")
        .strip()
    )


def main() -> int:
    coverage: dict[str, set[str]] = {}
    suspicious: list[tuple[str, str, str]] = []
    arabic_latin: list[tuple[str, str]] = []
    for path in SERVICES.glob("*SeedData.cs"):
        content = path.read_text(encoding="utf-8-sig")
        for match in FULL_RE.finditer(content):
            source = decode(match.group("source"))
            present = coverage.setdefault(source, {"tr-TR"})
            for group, code in (("en", "en-US"), ("de", "de-DE"), ("ar", "ar-SA")):
                value = decode(match.group(group))
                if value:
                    present.add(code)
                pattern = r"[ğüşöçıİĞÜŞÖÇ]" if code != "de-DE" else r"[ğşıİĞŞçÇ]"
                proper_name_only = source.startswith("Copyright ©") and "Nevzat SÜRÜCÜ" in value
                if value and re.search(pattern, value) and not proper_name_only:
                    suspicious.append((source, code, value))
                if code == "ar-SA" and value and re.search(r"[A-Za-z]", value):
                    arabic_latin.append((source, value))
        for match in ENGLISH_RE.finditer(content):
            source = decode(match.group("source"))
            if decode(match.group("en")):
                coverage.setdefault(source, {"tr-TR"}).add("en-US")

    required = {"tr-TR", "en-US", "de-DE", "ar-SA"}
    incomplete = [(source, sorted(required - codes)) for source, codes in coverage.items() if codes != required]
    print(f"Unique localized sources: {len(coverage)}")
    print(f"Complete in all four languages: {len(coverage) - len(incomplete)}")
    print(f"Incomplete: {len(incomplete)}")
    for source, missing in incomplete[:30]:
        print(f"- {source!r}: {', '.join(missing)}")
    print(f"Targets still containing Turkish characters: {len(suspicious)}")
    for source, code, value in suspicious[:60]:
        print(f"- {code} {source!r} => {value!r}")
    print(f"Arabic targets containing Latin letters: {len(arabic_latin)}")
    for source, value in arabic_latin[:80]:
        print(f"- ar-SA {source!r} => {value!r}")
    return 1 if incomplete or suspicious or arabic_latin else 0


if __name__ == "__main__":
    sys.exit(main())
