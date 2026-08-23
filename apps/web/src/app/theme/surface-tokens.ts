export const vinxSurface = {
  // Gradiente de marca reservado a logo, detalhes de destaque e heros.
  // Consumido via tokens; nenhum literal de gradiente fora deste módulo.
  brandGradient:
    "linear-gradient(135deg, var(--vinx-palette-primary-light) 0%, var(--vinx-palette-primary-main) 58%, var(--vinx-palette-primary-dark) 100%)",
  // Lavagem ambiente quente para o fundo do shell; sutil nos dois esquemas.
  ambientWash:
    "radial-gradient(1100px 520px at 88% -12%, rgba(146, 80, 47, 0.08), transparent 62%), radial-gradient(920px 460px at -8% 112%, rgba(116, 98, 103, 0.07), transparent 58%)",
} as const;

export const vinxShadow = {
  card: "0 1px 2px rgba(30, 21, 14, 0.05), 0 4px 14px rgba(30, 21, 14, 0.05)",
  lift: "0 2px 4px rgba(30, 21, 14, 0.07), 0 14px 34px rgba(30, 21, 14, 0.13)",
  overlay:
    "0 10px 24px rgba(22, 15, 10, 0.16), 0 24px 64px rgba(22, 15, 10, 0.22)",
  markFilter: "drop-shadow(0 2px 6px rgba(30, 21, 14, 0.18))",
} as const;

// Superfície fosca que segue o esquema ativo via CSS vars.
// color-mix é avaliado pelo navegador, sem valores fixos por esquema.
export function glassSurface() {
  return {
    backgroundColor:
      "color-mix(in srgb, var(--vinx-palette-background-paper) 78%, transparent)",
    backdropFilter: "blur(16px) saturate(1.25)",
    WebkitBackdropFilter: "blur(16px) saturate(1.25)",
  } as const;
}
