import { useColorScheme, useTheme } from "@mui/material/styles";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { vinxTheme } from "./create-vinx-theme";
import {
  useVisualPreferences,
  VinxThemeProvider,
  VisualPreferencesControls,
} from "./vinx-theme-provider";

function createMatchMediaMock(initialMatches: boolean) {
  let matches = initialMatches;
  let listener: ((event: MediaQueryListEvent) => void) | undefined;

  const matchMedia = vi.fn(() => ({
    get matches() {
      return matches;
    },
    media: "(prefers-color-scheme: dark)",
    addListener: (nextListener: (event: MediaQueryListEvent) => void) => {
      listener = nextListener;
    },
    removeListener: (nextListener: (event: MediaQueryListEvent) => void) => {
      if (listener === nextListener) {
        listener = undefined;
      }
    },
    addEventListener: () => {},
    removeEventListener: () => {},
    onchange: null,
    dispatchEvent: () => true,
  }));

  return {
    matchMedia,
    emit(nextMatches: boolean) {
      matches = nextMatches;
      listener?.({
        matches,
        media: "(prefers-color-scheme: dark)",
      } as MediaQueryListEvent);
    },
  };
}

function PreferenceConsumer() {
  const { colorScheme, mode } = useColorScheme();
  const { density } = useVisualPreferences();
  const theme = useTheme();

  return (
    <>
      <VisualPreferencesControls />
      <output data-testid="selected-mode">{mode ?? "unresolved"}</output>
      <output data-testid="selected-density">{density}</output>
      <output data-testid="surface-scheme">
        {colorScheme ?? "unresolved"}
      </output>
      <output data-testid="surface-background">
        {theme.palette.background.default}
      </output>
      <output data-testid="content-gap">{theme.vinx.layout.contentGap}</output>
    </>
  );
}

function renderHarness() {
  return render(
    <VinxThemeProvider>
      <PreferenceConsumer />
    </VinxThemeProvider>,
  );
}

describe("visual preferences", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("keeps an explicit light selection when the system scheme changes later", async () => {
    const user = userEvent.setup();
    const system = createMatchMediaMock(true);
    vi.stubGlobal("matchMedia", system.matchMedia);
    renderHarness();

    const modeControl = await screen.findByRole("combobox", {
      name: "Modo de cor",
    });
    expect(modeControl).not.toBeDisabled();
    await user.selectOptions(modeControl, "light");
    expect(screen.getByTestId("selected-mode")).toHaveTextContent("light");
    expect(screen.getByTestId("surface-scheme")).toHaveTextContent("light");

    system.emit(false);

    expect(screen.getByTestId("selected-mode")).toHaveTextContent("light");
    expect(screen.getByTestId("surface-scheme")).toHaveTextContent("light");
    expect(screen.getByTestId("surface-background")).toHaveTextContent(
      vinxTheme.palette.background.default,
    );
  });

  it("follows a later system scheme change while mode remains system", () => {
    const system = createMatchMediaMock(false);
    vi.stubGlobal("matchMedia", system.matchMedia);
    renderHarness();

    expect(screen.getByTestId("selected-mode")).toHaveTextContent("system");
    expect(screen.getByTestId("surface-scheme")).toHaveTextContent("light");

    act(() => {
      system.emit(true);
    });

    expect(screen.getByTestId("selected-mode")).toHaveTextContent("system");
    expect(screen.getByTestId("surface-scheme")).toHaveTextContent("dark");
  });

  it("restores compact density and an explicit mode in a fresh provider", async () => {
    const user = userEvent.setup();
    const system = createMatchMediaMock(false);
    vi.stubGlobal("matchMedia", system.matchMedia);
    const firstRender = renderHarness();

    await user.selectOptions(
      await screen.findByRole("combobox", { name: "Modo de cor" }),
      "dark",
    );
    await user.selectOptions(
      screen.getByRole("combobox", { name: "Densidade" }),
      "compact",
    );
    firstRender.unmount();

    renderHarness();

    expect(
      await screen.findByRole("combobox", { name: "Modo de cor" }),
    ).toHaveValue("dark");
    expect(screen.getByRole("combobox", { name: "Densidade" })).toHaveValue(
      "compact",
    );
    expect(screen.getByTestId("selected-density")).toHaveTextContent("compact");
    expect(screen.getByTestId("content-gap")).toHaveTextContent("16");
  });
});
