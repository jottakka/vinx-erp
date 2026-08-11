import {
  CssBaseline,
  FormControl,
  InputLabel,
  NativeSelect,
  Stack,
} from "@mui/material";
import { ThemeProvider, useColorScheme } from "@mui/material/styles";
import {
  type ChangeEvent,
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useMemo,
  useState,
} from "react";
import { createVinxStorageManager, createVinxTheme } from "./create-vinx-theme";
import { type Density, readDensity, writeDensity } from "./visual-preferences";

type ColorMode = "light" | "dark" | "system";

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
      defaultMode="system"
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

export function VisualPreferencesControls({
  compact = false,
}: {
  compact?: boolean;
}) {
  const { mode, setMode } = useColorScheme();
  const { density, setDensity } = useVisualPreferences();

  function handleModeChange(event: ChangeEvent<HTMLSelectElement>) {
    setMode(event.target.value as ColorMode);
  }

  function handleDensityChange(event: ChangeEvent<HTMLSelectElement>) {
    const nextDensity = event.target.value as Density;
    setDensity(nextDensity);
  }

  return (
    <Stack direction={compact ? "row" : "column"} spacing={1}>
      <FormControl size="small">
        <InputLabel htmlFor="vinx-color-mode">Modo de cor</InputLabel>
        <NativeSelect
          aria-label="Modo de cor"
          disabled={mode === undefined}
          id="vinx-color-mode"
          onChange={handleModeChange}
          value={mode ?? "system"}
        >
          <option value="system">Sistema</option>
          <option value="light">Claro</option>
          <option value="dark">Escuro</option>
        </NativeSelect>
      </FormControl>
      <FormControl size="small">
        <InputLabel htmlFor="vinx-density">Densidade</InputLabel>
        <NativeSelect
          aria-label="Densidade"
          id="vinx-density"
          onChange={handleDensityChange}
          value={density}
        >
          <option value="comfortable">Confortável</option>
          <option value="compact">Compacta</option>
        </NativeSelect>
      </FormControl>
    </Stack>
  );
}
