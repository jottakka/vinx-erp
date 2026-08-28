import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { resolveCompanyAppearance } from "./company-appearance";
import { CompanyBadge } from "./company-badge";

function renderBadge(id: string, name: string) {
  return render(
    <VinxThemeProvider>
      <CompanyBadge id={id} name={name} />
    </VinxThemeProvider>,
  );
}

describe("CompanyBadge", () => {
  it("renders Alfa with readable text, a symbol and the local alpha appearance", () => {
    renderBadge("alfa", "Alfa");

    expect(screen.getByText("Alfa")).toBeVisible();
    expect(screen.getByRole("group", { name: "Empresa Alfa" })).toBeVisible();
    expect(
      screen.getByRole("group", { name: "Empresa Alfa" }).querySelector("svg"),
    ).toBeTruthy();
    expect(resolveCompanyAppearance("alfa", "Alfa")).toMatchObject({
      monogram: "A",
      slot: "alpha",
    });
  });

  it("assigns a valid future company a deterministic non-neutral appearance", () => {
    renderBadge("nova-empresa", "Nova Empresa");

    expect(screen.getByText("Nova Empresa")).toBeVisible();
    expect(
      screen.getByRole("group", { name: "Empresa Nova Empresa" }),
    ).toBeVisible();
    expect(resolveCompanyAppearance("nova-empresa", "Nova Empresa")).toEqual(
      resolveCompanyAppearance("nova-empresa", "Nova Empresa"),
    );
    expect(
      resolveCompanyAppearance("nova-empresa", "Nova Empresa"),
    ).toMatchObject({
      monogram: "NE",
    });
    expect(
      resolveCompanyAppearance("nova-empresa", "Nova Empresa").slot,
    ).not.toBe("neutral");
  });

  it("reserves neutral appearance for an absent or invalid company identifier", () => {
    expect(resolveCompanyAppearance("", "Sem identificador").slot).toBe(
      "neutral",
    );
    expect(resolveCompanyAppearance("   ", "Sem identificador").slot).toBe(
      "neutral",
    );
    expect(
      resolveCompanyAppearance("!invalido", "Sem identificador").slot,
    ).toBe("neutral");
  });
});
