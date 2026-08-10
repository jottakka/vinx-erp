import type { PaletteColor } from "@mui/material/styles";
import type { CompanyAppearanceSlot } from "./company-appearance-slot";

declare module "@mui/material/styles" {
  interface Palette {
    navigation: PaletteColor;
    companyAccent: Record<CompanyAppearanceSlot, PaletteColor>;
  }

  interface PaletteOptions {
    navigation?: PaletteColor;
    companyAccent?: Record<CompanyAppearanceSlot, PaletteColor>;
  }
}
