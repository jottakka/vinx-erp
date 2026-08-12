import { useTheme } from "@mui/material/styles";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import {
  useVisualPreferences,
  VinxThemeProvider,
} from "../../app/theme/vinx-theme-provider";
import { AppearanceMenu } from "./appearance-menu";

function PreferenceProbe() {
  const { density } = useVisualPreferences();
  const theme = useTheme();

  return (
    <output data-testid="density-gap">
      {density}:{theme.vinx.layout.contentGap}
    </output>
  );
}

describe("AppearanceMenu", () => {
  it("updates the selected theme and density and closes after each selection", async () => {
    const user = userEvent.setup();
    render(
      <VinxThemeProvider>
        <AppearanceMenu />
        <PreferenceProbe />
      </VinxThemeProvider>,
    );

    await user.click(
      screen.getByRole("button", { name: "Abrir preferências visuais" }),
    );
    expect(
      screen.getByRole("menu", { name: "Abrir preferências visuais" }),
    ).toBeVisible();
    await user.click(screen.getByRole("menuitem", { name: "Tema: escuro" }));

    expect(screen.queryByRole("menu")).not.toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Abrir preferências visuais" }),
    );
    expect(
      screen.getByRole("menuitem", { name: "Tema: escuro" }),
    ).toHaveAttribute("aria-selected", "true");

    await user.click(
      screen.getByRole("menuitem", { name: "Densidade: compacta" }),
    );

    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
    expect(screen.getByTestId("density-gap")).toHaveTextContent("compact:16");
  });

  it("names its menu from the trigger and returns focus after Escape", async () => {
    const user = userEvent.setup();
    render(
      <VinxThemeProvider>
        <AppearanceMenu />
      </VinxThemeProvider>,
    );

    const trigger = screen.getByRole("button", {
      name: "Abrir preferências visuais",
    });
    trigger.focus();
    await user.keyboard("{Enter}");

    expect(
      screen.getByRole("menu", { name: "Abrir preferências visuais" }),
    ).toBeVisible();
    await user.keyboard("{Escape}");

    await waitFor(() => expect(trigger).toHaveFocus());
  });
});
