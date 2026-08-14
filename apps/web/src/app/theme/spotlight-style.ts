import { alpha, type Theme } from "@mui/material/styles";

/**
 * Camadas radiais decorativas do fundo que acompanha o ponteiro. Construídas
 * aqui para manter literais de cor confinadas às primitivas do tema.
 */
export function createSpotlightLayers(theme: Theme): string {
  const { mode } = theme.palette;
  const strength = mode === "dark" ? 0.16 : 0.12;

  return [
    `radial-gradient(34rem circle at var(--vinx-spot-x) var(--vinx-spot-y), ${alpha(
      theme.palette.primary.main,
      strength,
    )}, transparent 68%)`,
    `radial-gradient(56rem circle at 88% 8%, ${alpha(
      theme.palette.primary.light,
      0.07,
    )}, transparent 62%)`,
    `radial-gradient(44rem circle at 6% 92%, ${alpha(
      theme.palette.primary.dark,
      0.06,
    )}, transparent 64%)`,
  ].join(", ");
}
