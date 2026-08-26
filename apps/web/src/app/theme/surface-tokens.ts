import type { CompanyAppearanceSlot } from "./company-appearance-slot";

export const displayFontFamily =
  '"Sora", system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';

export const vinxSurface = {
  // Lavagem ambiente azul para o fundo do shell; segue o esquema ativo
  // porque mistura variáveis semânticas em vez de valores fixos.
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
  rest: "color-mix(in srgb, var(--vinx-palette-text-primary) 52%, transparent)",
  hover:
    "color-mix(in srgb, var(--vinx-palette-text-primary) 64%, transparent)",
} as const;

export const vinxFieldFill = {
  rest: "color-mix(in srgb, var(--vinx-palette-text-primary) 3%, transparent)",
  hover: "color-mix(in srgb, var(--vinx-palette-text-primary) 5%, transparent)",
  focus: "color-mix(in srgb, var(--vinx-palette-text-primary) 2%, transparent)",
  darkRest:
    "color-mix(in srgb, var(--vinx-palette-text-primary) 4%, transparent)",
  darkHover:
    "color-mix(in srgb, var(--vinx-palette-text-primary) 7%, transparent)",
  darkFocus:
    "color-mix(in srgb, var(--vinx-palette-text-primary) 3%, transparent)",
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
  card: "0 1px 2px color-mix(in srgb, var(--vinx-palette-navigation-dark) 5%, transparent), 0 4px 14px color-mix(in srgb, var(--vinx-palette-navigation-dark) 5%, transparent)",
  lift: "0 2px 4px color-mix(in srgb, var(--vinx-palette-navigation-dark) 7%, transparent), 0 14px 34px color-mix(in srgb, var(--vinx-palette-navigation-dark) 13%, transparent)",
  overlay:
    "0 10px 24px color-mix(in srgb, var(--vinx-palette-navigation-dark) 16%, transparent), 0 24px 64px color-mix(in srgb, var(--vinx-palette-navigation-dark) 22%, transparent)",
  markFilter:
    "drop-shadow(0 2px 6px color-mix(in srgb, var(--vinx-palette-navigation-dark) 18%, transparent))",
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
