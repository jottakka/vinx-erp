import { CssBaseline } from "@mui/material";
import { ThemeProvider } from "@mui/material/styles";
import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useMemo,
  useState,
} from "react";
import { createVinxStorageManager, createVinxTheme } from "./create-vinx-theme";
import { type Density, readDensity, writeDensity } from "./visual-preferences";

const vinxStorageManager = createVinxStorageManager({ key: "vinx.visual.v1" });

type VisualPreferencesContextValue = {
  density: Density;
  setDensity: (density: Density) => void;
};

const VisualPreferencesContext = createContext<VisualPreferencesContextValue>({
  density: "comfortable",
  setDensity: writeDensity,
});

type VinxThemeProviderProps = {
  children: ReactNode;
};

export function VinxThemeProvider({ children }: VinxThemeProviderProps) {
  const [density, setDensityState] = useState<Density>(readDensity);
  const setDensity = useCallback((nextDensity: Density) => {
    setDensityState(nextDensity);
    writeDensity(nextDensity);
  }, []);
  const theme = useMemo(() => createVinxTheme(density), [density]);
  const preferences = useMemo(
    () => ({ density, setDensity }),
    [density, setDensity],
  );

  return (
    <ThemeProvider
      colorSchemeStorageKey="scheme"
      defaultMode="light"
      disableTransitionOnChange
      modeStorageKey="mode"
      noSsr
      storageManager={vinxStorageManager}
      theme={theme}
    >
      <CssBaseline enableColorScheme />
      <VisualPreferencesContext.Provider value={preferences}>
        {children}
      </VisualPreferencesContext.Provider>
    </ThemeProvider>
  );
}

export function useVisualPreferences(): VisualPreferencesContextValue {
  return useContext(VisualPreferencesContext);
}
