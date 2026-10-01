// Turkish locale formatters. Kept dependency-free.

/**
 * Uygulamadaki tum parasal gosterimlerin tek kaynagi.
 * Her zaman tam tutar + Turkce gruplama + 2 ondalik + TL soneki kullanir.
 * Ornek: 95370 -> "95.370,00 TL"
 */
export function formatTRY(value: number): string {
  const safeValue = Number.isFinite(value) ? value : 0;
  const normalizedValue = Object.is(safeValue, -0) ? 0 : safeValue;

  try {
    const number = new Intl.NumberFormat("tr-TR", {
      useGrouping: true,
      maximumFractionDigits: 2,
      minimumFractionDigits: 2,
    }).format(normalizedValue);
    return `${number} TL`;
  } catch {
    const sign = normalizedValue < 0 ? "-" : "";
    const absolute = Math.abs(normalizedValue);
    const fixed = absolute.toFixed(2);
    const [integer, decimals] = fixed.split(".");
    const grouped = integer.replace(/\B(?=(\d{3})+(?!\d))/g, ".");
    return `${sign}${grouped},${decimals} TL`;
  }
}

export function formatAmount(value: number): string {
  try {
    return new Intl.NumberFormat("tr-TR", {
      maximumFractionDigits: 2,
      minimumFractionDigits: 2,
    }).format(value);
  } catch {
    return value.toFixed(2);
  }
}

const MONTHS = ["Oca", "Şub", "Mar", "Nis", "May", "Haz", "Tem", "Ağu", "Eyl", "Eki", "Kas", "Ara"];

export function formatDate(iso: string): string {
  const d = new Date(iso);
  return `${d.getDate().toString().padStart(2, "0")} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
}

export function formatRelativeDate(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  const startOfDay = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diffDays = Math.round((startOfDay(now) - startOfDay(d)) / 86_400_000);
  if (diffDays === 0) return "Bugün";
  if (diffDays === 1) return "Dün";
  if (diffDays > 1 && diffDays < 7) return `${diffDays} gün önce`;
  return formatDate(iso);
}

export function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((p) => p[0]?.toLocaleUpperCase("tr") ?? "")
    .join("");
}
