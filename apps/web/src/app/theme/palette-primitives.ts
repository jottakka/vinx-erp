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

export const lightPalette: PalettePrimitives = {
  primary: {
    light: "#3c8eaa",
    main: "#0f5b78",
    dark: "#0a3d52",
    contrastText: "#ffffff",
  },
  secondary: {
    light: "#39a4a2",
    main: "#007c83",
    dark: "#005357",
    contrastText: "#ffffff",
  },
  navigation: {
    light: "#244a69",
    main: "#102a43",
    dark: "#071a2b",
    contrastText: "#ffffff",
  },
  background: {
    default: "#f7fafc",
    paper: "#ffffff",
  },
  text: {
    primary: "#102a43",
    secondary: "#486581",
    disabled: "#829ab1",
  },
  divider: "#d9e2ec",
  companyAccent: {
    alpha: {
      light: "#3399a0",
      main: "#007c83",
      dark: "#005357",
      contrastText: "#ffffff",
    },
    beta: {
      light: "#5279a8",
      main: "#2c5282",
      dark: "#1f3b5f",
      contrastText: "#ffffff",
    },
    gamma: {
      light: "#b77c16",
      main: "#8a5a00",
      dark: "#5c3c00",
      contrastText: "#ffffff",
    },
    neutral: {
      light: "#829ab1",
      main: "#5f7891",
      dark: "#486581",
      contrastText: "#ffffff",
    },
  },
};

export const darkPalette: PalettePrimitives = {
  primary: {
    light: "#a1e4ec",
    main: "#66c7d2",
    dark: "#3a929d",
    contrastText: "#06121e",
  },
  secondary: {
    light: "#9ce3e0",
    main: "#55b9b7",
    dark: "#2f8583",
    contrastText: "#06121e",
  },
  navigation: {
    light: "#b7cfe4",
    main: "#8fb1ce",
    dark: "#6188a9",
    contrastText: "#06121e",
  },
  background: {
    default: "#0b1726",
    paper: "#12263a",
  },
  text: {
    primary: "#f5f7fa",
    secondary: "#c5d2df",
    disabled: "#8aa0b5",
  },
  divider: "#29445d",
  companyAccent: {
    alpha: {
      light: "#9ce3e0",
      main: "#55b9b7",
      dark: "#2f8583",
      contrastText: "#06121e",
    },
    beta: {
      light: "#a5c4e8",
      main: "#78a6d6",
      dark: "#4e78a5",
      contrastText: "#06121e",
    },
    gamma: {
      light: "#f1c36c",
      main: "#d99a2b",
      dark: "#a96f0d",
      contrastText: "#06121e",
    },
    neutral: {
      light: "#b4c6d6",
      main: "#8aa0b5",
      dark: "#60788f",
      contrastText: "#06121e",
    },
  },
};
