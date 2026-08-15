import { alpha } from "@mui/material/styles";
import { darkPalette, lightPalette } from "./palette-primitives";

/**
 * Primitivas do campo de tinta cobre do login. As cores chegam ao shader como
 * uniforms derivados das primitivas do tema — nenhum literal fora daqui — e o
 * fallback CSS permanece caso o WebGL2 não esteja disponível. O modo vem de
 * useColorScheme: com variáveis CSS, theme.palette.mode não acompanha a
 * troca ao vivo.
 */

export type InkFieldMode = "dark" | "light";

export type InkFieldRgb = readonly [number, number, number];

export type InkFieldColors = {
  inkLight: InkFieldRgb;
  inkMain: InkFieldRgb;
  inkDark: InkFieldRgb;
  paper: InkFieldRgb;
  glint: InkFieldRgb;
  dark: boolean;
};

function hexToRgb(hex: string): InkFieldRgb {
  const channels = hex
    .match(/^#([\da-f]{6})$/i)?.[1]
    ?.match(/.{2}/g)
    ?.map((channel) => Number.parseInt(channel, 16) / 255);

  if (channels?.length !== 3) {
    throw new Error(`Ink color must be a six-digit hex value: ${hex}`);
  }

  return [channels[0], channels[1], channels[2]];
}

export function createInkFieldColors(mode: InkFieldMode): InkFieldColors {
  const palette = mode === "dark" ? darkPalette : lightPalette;

  return {
    inkLight: hexToRgb(palette.primary.light),
    inkMain: hexToRgb(palette.primary.main),
    inkDark: hexToRgb(palette.primary.dark),
    paper: hexToRgb(palette.background.default),
    glint: hexToRgb(
      mode === "dark" ? palette.text.primary : palette.primary.light,
    ),
    dark: mode === "dark",
  };
}

/**
 * Camadas estáticas exibidas quando o campo animado não pode ser criado
 * (WebGL2 indisponível, movimento reduzido ou ausência de ponteiro fino).
 */
export function createInkFallbackLayers(mode: InkFieldMode): string {
  const palette = mode === "dark" ? darkPalette : lightPalette;
  const strength = mode === "dark" ? 0.14 : 0.1;

  return [
    `radial-gradient(38rem circle at 22% 68%, ${alpha(
      palette.primary.main,
      strength,
    )}, transparent 66%)`,
    `radial-gradient(52rem circle at 86% 10%, ${alpha(
      palette.primary.dark,
      0.07,
    )}, transparent 62%)`,
    `radial-gradient(46rem circle at 8% 88%, ${alpha(
      palette.primary.light,
      0.06,
    )}, transparent 64%)`,
  ].join(", ");
}
