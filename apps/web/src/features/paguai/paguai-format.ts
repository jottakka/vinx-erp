import {
  amountToCents,
  canonicalAmount,
  maximumAmountCents,
} from "./paguai-amount";

/**
 * Interpreta a entrada em reais do formulário (vírgula ou ponto, com ou sem
 * separador de milhar) e devolve a forma canônica "1234.56"; null se inválido.
 */
export function amountFromReais(value: string): string | null {
  const trimmed = value.trim();
  let normalized: string;
  const withCommaDecimal = /^[0-9]+,[0-9]{1,2}$/.test(trimmed);
  const groupedComma = /^[0-9]{1,3}(?:[.][0-9]{3})+,[0-9]{1,2}$/.test(trimmed);
  const withDotDecimal = /^[0-9]+[.][0-9]{1,2}$/.test(trimmed);
  const groupedDot = /^[0-9]{1,3}(?:[.][0-9]{3})+$/.test(trimmed);
  const plain = /^[0-9]+$/.test(trimmed);

  if (groupedComma) {
    normalized = trimmed.replace(/[.]/g, "").replace(",", ".");
  } else if (withCommaDecimal) {
    normalized = trimmed.replace(",", ".");
  } else if (groupedDot) {
    normalized = trimmed.replace(/[.]/g, "");
  } else if (withDotDecimal) {
    normalized = trimmed;
  } else if (plain) {
    normalized = `${trimmed}.00`;
  } else {
    return null;
  }

  const cents = amountToCents(normalized);
  if (cents === null || cents <= 0n || cents > maximumAmountCents) return null;
  return canonicalAmount(normalized);
}

/** Formata um valor canônico em reais para exibição. */
export function formatAmount(amount: string): string {
  const [whole, fraction = ""] = amount.split(".");
  let grouped = "";
  let count = 0;
  for (let i = whole.length - 1; i >= 0; i -= 1) {
    grouped = whole[i] + grouped;
    count += 1;
    if (count % 3 === 0 && i > 0) grouped = "." + grouped;
  }
  return `R$ ${grouped},${fraction.padEnd(2, "0")}`;
}

/** Converte uma data ISO (yyyy-mm-dd) para exibição dd/mm/yyyy. */
export function formatDueDate(dueDate: string): string {
  const [year, month, day] = dueDate.split("-");
  return `${day}/${month}/${year}`;
}
