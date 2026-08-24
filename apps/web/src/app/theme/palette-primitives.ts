import type { PaletteColor } from "@mui/material/styles";
import type { CompanyAppearanceSlot } from "./company-appearance-slot";

type PalettePrimitives = {
  primary: PaletteColor;
  secondary: PaletteColor;
  navigation: PaletteColor;
  success: PaletteColor;
  warning: PaletteColor;
  info: PaletteColor;
  error: PaletteColor;
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
    light: "#c88b66",
    main: "#92502f",
    dark: "#6c3824",
    contrastText: "#fff9f2",
  },
  secondary: {
    light: "#a89a9d",
    main: "#746267",
    dark: "#504348",
    contrastText: "#fff9f2",
  },
  navigation: {
    light: "#3e3a36",
    main: "#292623",
    dark: "#181715",
    contrastText: "#f8f3ea",
  },
  success: {
    light: "#7ba585",
    main: "#4a7856",
    dark: "#35593f",
    contrastText: "#fff9f2",
  },
  warning: {
    light: "#d3a04c",
    // 4.764:1 sobre contrastText — AA com folga; #9a6b1e media 4.467:1.
    main: "#94671c",
    dark: "#6f4b14",
    contrastText: "#fff9f2",
  },
  info: {
    light: "#738696",
    main: "#506474",
    dark: "#384754",
    contrastText: "#fff9f2",
  },
  error: {
    light: "#d4715f",
    main: "#b3402f",
    dark: "#8a3021",
    contrastText: "#fff9f2",
  },
  background: {
    default: "#f6f1e8",
    paper: "#fdfbf7",
  },
  text: {
    primary: "#292521",
    secondary: "#665f58",
    disabled: "#918a82",
  },
  divider: "#ddd4c9",
  companyAccent: {
    alpha: {
      light: "#a46f5b",
      main: "#7a4d3b",
      dark: "#553428",
      contrastText: "#fff9f2",
    },
    beta: {
      light: "#738696",
      main: "#506474",
      dark: "#384754",
      contrastText: "#fff9f2",
    },
    gamma: {
      light: "#96805a",
      main: "#6f5d3c",
      dark: "#4d402a",
      contrastText: "#fff9f2",
    },
    neutral: {
      light: "#827c73",
      main: "#5e5a54",
      dark: "#423f3a",
      contrastText: "#fff9f2",
    },
  },
} as const satisfies PalettePrimitives;

export const darkPalette = {
  primary: {
    light: "#e7ac87",
    main: "#cd8258",
    dark: "#a65e3d",
    contrastText: "#191411",
  },
  secondary: {
    light: "#c7b5b8",
    main: "#aa969b",
    dark: "#7c6a6f",
    contrastText: "#191411",
  },
  navigation: {
    light: "#32302d",
    main: "#22211f",
    dark: "#151412",
    contrastText: "#f3ede4",
  },
  success: {
    light: "#a8c9b1",
    main: "#8fb99b",
    dark: "#6f9a7d",
    contrastText: "#16211a",
  },
  warning: {
    light: "#e5bd7c",
    main: "#d9a45b",
    dark: "#b98944",
    contrastText: "#241a0d",
  },
  info: {
    light: "#b4c3cd",
    main: "#9db1c0",
    dark: "#8095a5",
    contrastText: "#131c23",
  },
  error: {
    light: "#eba795",
    main: "#e08a77",
    dark: "#c46f5c",
    contrastText: "#2a120c",
  },
  background: {
    default: "#141311",
    paper: "#201d19",
  },
  text: {
    primary: "#f2ece3",
    secondary: "#b7afa5",
    disabled: "#7e7871",
  },
  divider: "#3a352f",
  companyAccent: {
    alpha: {
      light: "#e1a68e",
      main: "#c08169",
      dark: "#965d48",
      contrastText: "#191411",
    },
    beta: {
      light: "#b4c3cd",
      main: "#8fa4b1",
      dark: "#687d8b",
      contrastText: "#191411",
    },
    gamma: {
      light: "#d3bf8a",
      main: "#b39b65",
      dark: "#887344",
      contrastText: "#191411",
    },
    neutral: {
      light: "#c1bcb3",
      main: "#9f9a91",
      dark: "#746f68",
      contrastText: "#191411",
    },
  },
} as const satisfies PalettePrimitives;
