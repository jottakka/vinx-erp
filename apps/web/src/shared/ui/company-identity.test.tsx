import { render, screen } from "@testing-library/react";
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

    expect(
      screen.getByRole("group", { name: "Empresa Nova Empresa" }),
    ).toBeVisible();
    expect(screen.getByText("Nova Empresa")).toBeVisible();
    expect(screen.getByText("NE")).toBeVisible();
    expect(screen.queryByText("company-secret-id")).not.toBeInTheDocument();
  });
});
