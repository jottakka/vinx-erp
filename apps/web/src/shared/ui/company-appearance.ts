import type { CompanyAppearanceSlot } from "../../app/theme/company-appearance-slot";

const localCompanySlots: Readonly<Record<string, CompanyAppearanceSlot>> = {
  alfa: "alpha",
  beta: "beta",
  gama: "gamma",
};

function createMonogram(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);

  if (words.length > 1) {
    return words
      .slice(0, 2)
      .map((word) => Array.from(word)[0])
      .join("")
      .toUpperCase();
  }

  return Array.from(words[0] ?? "?")
    .slice(0, 1)
    .join("")
    .toUpperCase();
}

export function resolveCompanyAppearance(
  id: string,
  name: string,
): { monogram: string; slot: CompanyAppearanceSlot } {
  return {
    monogram: createMonogram(name),
    slot: localCompanySlots[id.trim().toLowerCase()] ?? "neutral",
  };
}
