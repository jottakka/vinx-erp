import { createTheme, type StorageManager } from "@mui/material/styles";
import "./theme-augmentation";
import { darkPalette, lightPalette } from "./palette-primitives";

const supportedModes = new Set(["light", "dark", "system"]);

function noop() {}

function isModeKey(key: string) {
  return key === "mui-mode" || key === "mode" || key.endsWith("-mode");
}

export function createVinxStorageManager({
  key,
}: {
  key: string;
}): StorageManager {
  return ({ key: storageKey, storageWindow }) => {
    const prefixedKey = `${key}.${storageKey}`;
    const modeKey = isModeKey(storageKey);

    return {
      get(defaultValue: unknown) {
        try {
          const value = storageWindow?.localStorage.getItem(prefixedKey);

          if (value === null || value === undefined) {
            return defaultValue;
          }

          if (modeKey && !supportedModes.has(value)) {
            return defaultValue;
          }

          return value;
        } catch {
          return defaultValue;
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
            event.newValue !== null &&
            !supportedModes.has(event.newValue)
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

const baseVinxTheme = createTheme({
  cssVariables: {
    colorSchemeSelector: "data",
    cssVarPrefix: "vinx",
  },
  colorSchemes: {
    light: { palette: lightPalette },
    dark: { palette: darkPalette },
  },
});

type VinxColorSchemes = {
  light: { palette: typeof lightPalette };
  dark: { palette: typeof darkPalette };
};

export const vinxTheme = baseVinxTheme as typeof baseVinxTheme & {
  colorSchemes: VinxColorSchemes;
};
