import type { CompanyAppearanceSlot } from "./company-appearance-slot";

export const displayFontFamily =
  '"Sora", system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';

export const vinxSurface = {
  // Lavagem ambiente quente para o fundo do shell; segue o esquema ativo
  // porque mistura variáveis semanticas em vez de valores fixos.
  ambientWash:
    "radial-gradient(1100px 520px at 88% -12%, color-mix(in srgb, var(--vinx-palette-primary-main) 9%, transparent), transparent 62%), radial-gradient(920px 460px at -8% 112%, color-mix(in srgb, var(--vinx-palette-secondary-main) 8%, transparent), transparent 58%)",
} as const;

// Tints e bordas derivadas de tokens semânticos; todas reagem à troca de
// esquema porque color-mix é avaliado pelo navegador sobre CSS vars.
export const vinxTint = {
  primaryVeil:
    "color-mix(in srgb, var(--vinx-palette-primary-main) 3%, transparent)",
  primaryWash:
    "color-mix(in srgb, var(--vinx-palette-primary-main) 6%, transparent)",
  primarySoft:
    "color-mix(in srgb, var(--vinx-palette-primary-main) 11%, transparent)",
  primaryMuted:
    "color-mix(in srgb, var(--vinx-palette-primary-main) 14%, transparent)",
  primaryEdge:
    "color-mix(in srgb, var(--vinx-palette-primary-main) 30%, transparent)",
  primaryLine:
    "color-mix(in srgb, var(--vinx-palette-primary-main) 48%, transparent)",
  contrastRing:
    "color-mix(in srgb, var(--vinx-palette-primary-contrastText) 32%, transparent)",
} as const;

// Borda de campo com contraste não textual >= 3:1 nos dois esquemas.
export const vinxFieldBorder = {
  rest: "color-mix(in srgb, var(--vinx-palette-text-primary) 48%, transparent)",
  hover:
    "color-mix(in srgb, var(--vinx-palette-text-primary) 64%, transparent)",
} as const;

type AccentSurface = {
  main: string;
  tint: string;
  border: string;
};

export function companySurface(slot: CompanyAppearanceSlot): AccentSurface {
  const main = `var(--vinx-palette-companyAccent-${slot}-main)`;
  return {
    border: `color-mix(in srgb, ${main} 45%, transparent)`,
    main,
    tint: `color-mix(in srgb, ${main} 16%, transparent)`,
  };
}

export const vinxShadow = {
  card: "0 1px 2px rgba(30, 21, 14, 0.05), 0 4px 14px rgba(30, 21, 14, 0.05)",
  lift: "0 2px 4px rgba(30, 21, 14, 0.07), 0 14px 34px rgba(30, 21, 14, 0.13)",
  overlay:
    "0 10px 24px rgba(22, 15, 10, 0.16), 0 24px 64px rgba(22, 15, 10, 0.22)",
  markFilter: "drop-shadow(0 2px 6px rgba(30, 21, 14, 0.18))",
} as const;

// Vidro fosco reservado a superfícies paradas (AppBar); nunca sobre o
// canvas animado do login, que pagaria recomposição por quadro.
export function glassSurface() {
  return {
    backgroundColor:
      "color-mix(in srgb, var(--vinx-palette-background-paper) 78%, transparent)",
    backdropFilter: "blur(16px) saturate(1.25)",
    WebkitBackdropFilter: "blur(16px) saturate(1.25)",
  } as const;
}

// Superfície quase opaca sem blur: usada no cartão de login sobre o campo
// de tinta animado.
export function veilSurface() {
  return {
    backgroundColor:
      "color-mix(in srgb, var(--vinx-palette-background-paper) 92%, transparent)",
  } as const;
}
