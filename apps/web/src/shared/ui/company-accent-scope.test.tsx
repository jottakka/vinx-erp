import { useColorScheme } from "@mui/material/styles";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { CompanyAccentScope } from "./company-accent-scope";

function ModeFlipper() {
  const { setMode } = useColorScheme();

  return <button onClick={() => setMode("dark")}>trocar tema</button>;
}

describe("CompanyAccentScope", () => {
  it("keeps the same input node, its typed value and focus capability when the color mode changes", async () => {
    const user = userEvent.setup();
    render(
      <VinxThemeProvider>
        <CompanyAccentScope slot="neutral">
          <input aria-label="titulo" />
          <ModeFlipper />
        </CompanyAccentScope>
      </VinxThemeProvider>,
    );
    const input = screen.getByLabelText("titulo");
    await user.type(input, "rascunho");

    await user.click(screen.getByRole("button", { name: "trocar tema" }));

    // Sem key={mode}: a troca claro/escuro não remonta a árvore — o mesmo nó
    // continua no DOM com o valor digitado.
    expect(screen.getByLabelText("titulo")).toBe(input);
    expect(input).toHaveValue("rascunho");
  });
});
