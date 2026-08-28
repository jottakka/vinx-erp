import {
  createTheme,
  type StorageManager,
  type Theme,
} from "@mui/material/styles";
import "./theme-augmentation";
import { darkPalette, lightPalette } from "./palette-primitives";
import type { Density } from "./visual-preferences";

export type VinxLayoutTokens = {
  collapsedNavigationWidth: number;
  contentGap: number;
  contentPadding: number;
  controlMinHeight: number;
  navigationWidth: number;
};

const supportedModes = new Set(["light", "dark"]);
const supportedSchemes = new Set(["light", "dark"]);
const modeKeys = new Set(["mui-mode", "mode"]);
const schemeKeys = new Set(["mui-color-scheme", "scheme-light", "scheme-dark"]);

function noop() {}

function isModeKey(key: string) {
  return modeKeys.has(key);
}

function isSchemeKey(key: string) {
  return schemeKeys.has(key);
}

export function createVinxStorageManager({
  key,
}: {
  key: string;
}): StorageManager {
  return ({ key: storageKey, storageWindow }) => {
    const prefixedKey = `${key}.${storageKey}`;
    const modeKey = isModeKey(storageKey);
    const schemeKey = isSchemeKey(storageKey);

    return {
      get(defaultValue: unknown) {
        try {
          const value = storageWindow?.localStorage.getItem(prefixedKey);

          if (value === null || value === undefined) {
            return modeKey ? "light" : defaultValue;
          }

          if (modeKey && !supportedModes.has(value)) {
            return "light";
          }

          if (schemeKey && !supportedSchemes.has(value)) {
            return defaultValue;
          }

          return value;
        } catch {
          return modeKey ? "light" : defaultValue;
        }
      },
      set(value: unknown) {
        try {
          storageWindow?.localStorage.setItem(prefixedKey, String(value));
        } catch {
          noop();
        }
      },
      subscribe(handler: (value: unknown) => void) {
        if (!storageWindow) {
          return noop;
        }

        const listener = (event: StorageEvent) => {
          if (event.key !== prefixedKey) {
            return;
          }

          if (
            modeKey &&
            (event.newValue === null || !supportedModes.has(event.newValue))
          ) {
            handler("light");
            return;
          }

          if (
            schemeKey &&
            event.newValue !== null &&
            !supportedSchemes.has(event.newValue)
          ) {
            handler(undefined);
            return;
          }

          handler(event.newValue);
        };

        try {
          storageWindow.addEventListener("storage", listener);
          return () => {
            try {
              storageWindow.removeEventListener("storage", listener);
            } catch {
              noop();
            }
          };
        } catch {
          return noop;
        }
      },
    };
  };
}

const layoutTokens: Record<Density, VinxLayoutTokens> = {
  comfortable: {
    collapsedNavigationWidth: 72,
    contentGap: 24,
    contentPadding: 32,
    controlMinHeight: 40,
    navigationWidth: 280,
  },
  compact: {
    collapsedNavigationWidth: 64,
    contentGap: 16,
    contentPadding: 24,
    controlMinHeight: 32,
    navigationWidth: 240,
  },
};

type VinxColorSchemes = {
  light: { palette: typeof lightPalette };
  dark: { palette: typeof darkPalette };
};

export function createVinxTheme(density: Density): Theme & {
  colorSchemes: VinxColorSchemes;
} {
  return createTheme({
    components: {
      MuiButton: {
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: { textTransform: "none" },
        },
      },
      MuiCard: {
        defaultProps: { elevation: 0 },
      },
      MuiCssBaseline: {
        styleOverrides: {
          "@media (forced-colors: active)": {
            body: {
              backgroundColor: "Canvas !important",
              color: "CanvasText !important",
            },
            "body *": {
              borderColor: "CanvasText !important",
              color: "CanvasText !important",
            },
            a: {
              color: "LinkText !important",
            },
            "button, [role='button']": {
              backgroundColor: "ButtonFace !important",
              color: "ButtonText !important",
            },
            "input, select, textarea": {
              backgroundColor: "Field !important",
              color: "FieldText !important",
            },
          },
        },
      },
    },
    cssVariables: {
      colorSchemeSelector: "data",
      cssVarPrefix: "vinx",
    },
    colorSchemes: {
      light: { palette: lightPalette },
      dark: { palette: darkPalette },
    },
    shape: { borderRadius: 10 },
    typography: {
      fontFamily:
        'system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif',
      h1: { fontWeight: 600, letterSpacing: "-0.02em" },
      h2: { fontWeight: 600, letterSpacing: "-0.015em" },
      h3: { fontWeight: 600 },
      h4: { fontWeight: 600 },
      h5: { fontWeight: 600 },
      h6: { fontWeight: 600 },
      button: { fontWeight: 600 },
    },
    vinx: { layout: layoutTokens[density] },
  }) as Theme & { colorSchemes: VinxColorSchemes };
}

export const vinxTheme = createVinxTheme("comfortable");
