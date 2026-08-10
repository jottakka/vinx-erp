import {
  CssBaseline,
  FormControl,
  InputLabel,
  NativeSelect,
  Stack,
} from "@mui/material";
import { ThemeProvider, useColorScheme } from "@mui/material/styles";
import { type ChangeEvent, type ReactNode, useState } from "react";
import { createVinxStorageManager, vinxTheme } from "./create-vinx-theme";
import { type Density, readDensity, writeDensity } from "./visual-preferences";

type ColorMode = "light" | "dark" | "system";

const vinxStorageManager = createVinxStorageManager({ key: "vinx.visual.v1" });

type VinxThemeProviderProps = {
  children: ReactNode;
};

export function VinxThemeProvider({ children }: VinxThemeProviderProps) {
  return (
    <ThemeProvider
      colorSchemeStorageKey="scheme"
      defaultMode="system"
      disableTransitionOnChange
      modeStorageKey="mode"
      noSsr
      storageManager={vinxStorageManager}
      theme={vinxTheme}
    >
      <CssBaseline enableColorScheme />
      {children}
    </ThemeProvider>
  );
}

export function VisualPreferencesControls({
  compact = false,
}: {
  compact?: boolean;
}) {
  const { mode, setMode } = useColorScheme();
  const [density, setDensity] = useState<Density>(readDensity);

  function handleModeChange(event: ChangeEvent<HTMLSelectElement>) {
    setMode(event.target.value as ColorMode);
  }

  function handleDensityChange(event: ChangeEvent<HTMLSelectElement>) {
    const nextDensity = event.target.value as Density;
    setDensity(nextDensity);
    writeDensity(nextDensity);
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
