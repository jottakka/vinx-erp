import type { PaletteColor } from "@mui/material/styles";
import type { CompanyAppearanceSlot } from "./company-appearance-slot";
import type { VinxLayoutTokens } from "./create-vinx-theme";

declare module "@mui/material/styles" {
  interface Palette {
    navigation: PaletteColor;
    companyAccent: Record<CompanyAppearanceSlot, PaletteColor>;
  }

  interface PaletteOptions {
    navigation?: PaletteColor;
    companyAccent?: Record<CompanyAppearanceSlot, PaletteColor>;
  }

  interface Theme {
    vinx: {
      layout: VinxLayoutTokens;
    };
  }

  interface ThemeOptions {
    vinx?: {
      layout: VinxLayoutTokens;
    };
  }
}
