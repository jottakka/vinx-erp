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
    expect(screen.getByRole("menuitem", { name: "Tema: claro" })).toBeVisible();
    expect(
      screen.getByRole("menuitem", { name: "Tema: escuro" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("menuitem", { name: "Tema: sistema" }),
    ).not.toBeInTheDocument();
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

  it("keeps one menu and the final selection after repeated preference changes", async () => {
    const user = userEvent.setup();
    render(
      <VinxThemeProvider>
        <AppearanceMenu />
        <PreferenceProbe />
      </VinxThemeProvider>,
    );

    const trigger = screen.getByRole("button", {
      name: "Abrir preferências visuais",
    });
    await user.click(trigger);
    await user.click(
      screen.getByRole("menuitem", { name: "Densidade: compacta" }),
    );
    await user.click(trigger);
    await user.click(
      screen.getByRole("menuitem", { name: "Densidade: confortável" }),
    );
    await user.click(trigger);

    expect(
      screen.getAllByRole("menu", { name: "Abrir preferências visuais" }),
    ).toHaveLength(1);
    expect(
      screen.getByRole("menuitem", { name: "Densidade: confortável" }),
    ).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("density-gap")).toHaveTextContent(
      "comfortable:24",
    );
  });
});
