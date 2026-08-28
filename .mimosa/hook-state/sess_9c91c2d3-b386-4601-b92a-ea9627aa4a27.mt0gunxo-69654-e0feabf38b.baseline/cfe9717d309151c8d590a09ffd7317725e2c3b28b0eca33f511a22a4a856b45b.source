export type CompanyAppearanceSlot = "alpha" | "beta" | "gamma" | "neutral";

const auxiliarySlots = ["alpha", "beta", "gamma"] as const;
const companyIdentifier = /^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/;

export function resolveCompanyAppearanceSlot(
  companyId: string,
): CompanyAppearanceSlot {
  const normalizedId = companyId.trim().toLowerCase();

  if (!companyIdentifier.test(normalizedId)) {
    return "neutral";
  }

  const hash = Array.from(normalizedId).reduce(
    (value, character) => (value * 31 + (character.codePointAt(0) ?? 0)) >>> 0,
    0,
  );

  return auxiliarySlots[hash % auxiliarySlots.length];
}
