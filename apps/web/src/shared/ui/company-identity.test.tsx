import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { CompanyIdentity } from "./company-identity";

describe("CompanyIdentity", () => {
  it("renders a stable readable identity and monogram without exposing the ID", () => {
    render(
      <VinxThemeProvider>
        <CompanyIdentity
          companyId="company-secret-id"
          companyName="Nova Empresa"
        />
      </VinxThemeProvider>,
    );

    expect(screen.getByText("Nova Empresa")).toBeVisible();
    expect(screen.getByText("NE")).toBeVisible();
    expect(screen.queryByText("company-secret-id")).not.toBeInTheDocument();
  });

  it("renders a neutral wrapper without group semantics for button nesting", () => {
    render(
      <VinxThemeProvider>
        <button type="button">
          <CompanyIdentity
            companyId="company-secret-id"
            companyName="Nova Empresa"
          />
        </button>
      </VinxThemeProvider>,
    );

    // Dentro de Button/MenuItem o wrapper não pode expor role/aria-label
    // próprios: HTML inválido e nome narrado duas vezes.
    expect(screen.queryByRole("group")).not.toBeInTheDocument();
    expect(
      within(screen.getByRole("button", { name: "Nova Empresa" })).getByText(
        "Nova Empresa",
      ),
    ).toBeVisible();
  });
});
