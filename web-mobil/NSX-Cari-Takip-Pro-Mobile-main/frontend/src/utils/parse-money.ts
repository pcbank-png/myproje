/** Turkish grouping and comma/dot decimal input; never accept a partial number. */
export const MAX_TRANSACTION_AMOUNT = 999_999_999_999;

export function parseMoney(input: string): { amount: number; error?: string } {
  const text = input.trim();
  if (!text) return { amount: 0 };
  let normalized: string;
  if (/^\d+(?:[.,]\d{1,2})?$/.test(text)) {
    normalized = text.replace(",", ".");
  } else if (/^\d{1,3}(?:\.\d{3})+,\d{1,2}$/.test(text)) {
    normalized = text.replace(/\./g, "").replace(",", ".");
  } else {
    return { amount: 0, error: "Geçerli bir tutar girin. Örnek: 100,50 veya 100.50. En fazla iki ondalık kullanın." };
  }
  const amount = Number(normalized);
  if (!Number.isFinite(amount) || amount <= 0 || amount > MAX_TRANSACTION_AMOUNT) {
    return { amount: 0, error: "Tutar sıfırdan büyük ve 999.999.999.999,00 TL veya daha küçük olmalı." };
  }
  return { amount: Math.round((amount + Number.EPSILON) * 100) / 100 };
}
