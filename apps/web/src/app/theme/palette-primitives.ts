import type { PaletteColor } from "@mui/material/styles";
import type { CompanyAppearanceSlot } from "./company-appearance-slot";

type PalettePrimitives = {
  primary: PaletteColor;
  secondary: PaletteColor;
  navigation: PaletteColor;
  background: {
    default: string;
    paper: string;
  };
  text: {
    primary: string;
    secondary: string;
    disabled: string;
  };
  divider: string;
  companyAccent: Record<CompanyAppearanceSlot, PaletteColor>;
};

export const lightPalette = {
  primary: {
    light: "#c98761",
    main: "#a85f39",
    dark: "#7f4326",
    contrastText: "#fffaf2",
  },
  secondary: {
    light: "#b59a8b",
    main: "#8c7060",
    dark: "#624c40",
    contrastText: "#fffaf2",
  },
  navigation: {
    light: "#3f3c37",
    main: "#24221f",
    dark: "#161513",
    contrastText: "#f8f5ee",
  },
  background: {
    default: "#f4f2ed",
    paper: "#fbfaf7",
  },
  text: {
    primary: "#24221f",
    secondary: "#6f6a62",
    disabled: "#99938a",
  },
  divider: "#dedbd4",
  companyAccent: {
    alpha: {
      light: "#9a6f59",
      main: "#77513e",
      dark: "#54372a",
      contrastText: "#fffaf2",
    },
    beta: {
      light: "#6f7f91",
      main: "#506071",
      dark: "#37434e",
      contrastText: "#fffaf2",
    },
    gamma: {
      light: "#9a7b52",
      main: "#755a38",
      dark: "#503d27",
      contrastText: "#fffaf2",
    },
    neutral: {
      light: "#857f75",
      main: "#625e57",
      dark: "#45423d",
      contrastText: "#fffaf2",
    },
  },
} as const satisfies PalettePrimitives;

export const darkPalette = {
  primary: {
    light: "#e0a47d",
    main: "#c47a4f",
    dark: "#965735",
    contrastText: "#17120f",
  },
  secondary: {
    light: "#d2b7a5",
    main: "#b18f7b",
    dark: "#886b5b",
    contrastText: "#17120f",
  },
  navigation: {
    light: "#3a3935",
    main: "#242421",
    dark: "#151514",
    contrastText: "#f3f0e8",
  },
  background: {
    default: "#111110",
    paper: "#191918",
  },
  text: {
    primary: "#f3f0e8",
    secondary: "#aaa69d",
    disabled: "#77736c",
  },
  divider: "#32312e",
  companyAccent: {
    alpha: {
      light: "#f0b79a",
      main: "#d18c69",
      dark: "#a96545",
      contrastText: "#17120f",
    },
    beta: {
      light: "#b6c4d3",
      main: "#8fa1b3",
      dark: "#687b8e",
      contrastText: "#17120f",
    },
    gamma: {
      light: "#d7b27d",
      main: "#b99155",
      dark: "#896b3f",
      contrastText: "#17120f",
    },
    neutral: {
      light: "#c1bdb4",
      main: "#9a968d",
      dark: "#716e67",
      contrastText: "#17120f",
    },
  },
} as const satisfies PalettePrimitives;
