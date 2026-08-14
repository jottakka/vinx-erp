import {
  createTheme,
  type StorageManager,
  type Theme,
} from "@mui/material/styles";
import "./theme-augmentation";
import { darkPalette, lightPalette } from "./palette-primitives";
import type { Density } from "./visual-preferences";

export type VinxLayoutTokens = {
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
    contentGap: 24,
    contentPadding: 32,
    controlMinHeight: 40,
    navigationWidth: 280,
  },
  compact: {
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
    vinx: { layout: layoutTokens[density] },
  }) as Theme & { colorSchemes: VinxColorSchemes };
}

export const vinxTheme = createVinxTheme("comfortable");
