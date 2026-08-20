export const maximumAmountCents = 999_999_999_999_999_999n;

/**
 * Normaliza um decimal de até duas casas na forma canônica "1234.56"
 * (sem zeros à esquerda, sempre duas casas); `null` quando malformado.
 */
export function canonicalAmount(amount: string): string | null {
  const match = amount.match(/^(\d+)(?:\.(\d{1,2}))?$/);
  if (!match) return null;
  const [, whole, fraction = ""] = match;
  return `${BigInt(whole)}.${fraction.padEnd(2, "0")}`;
}

/** Centavos exatos de um decimal canônico, sem aritmética de ponto flutuante. */
export function amountToCents(amount: string): bigint | null {
  const canonical = canonicalAmount(amount);
  return canonical === null ? null : BigInt(canonical.replace(".", ""));
}
