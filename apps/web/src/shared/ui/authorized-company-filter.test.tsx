import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { AuthorizedCompanyFilter } from "./authorized-company-filter";

const companies = [
  { companyId: "alfa-id", companyName: "Alfa" },
  { companyId: "beta-id", companyName: "Beta" },
];

function renderFilter(
  selectedCompanyIds: readonly string[] = [],
  onChange = vi.fn(),
) {
  return {
    onChange,
    ...render(
      <VinxThemeProvider>
        <AuthorizedCompanyFilter
          companies={companies}
          label="Empresas autorizadas"
          onChange={onChange}
          selectedCompanyIds={selectedCompanyIds}
        />
      </VinxThemeProvider>,
    ),
  };
}

describe("AuthorizedCompanyFilter", () => {
  it("communicates that an empty selection means all authorized companies", () => {
    renderFilter();

    expect(
      screen.getByText("Empresas autorizadas", { selector: "label" }),
    ).toHaveAttribute("data-shrink", "true");
    expect(
      screen.getByRole("combobox", { name: "Empresas autorizadas" }),
    ).toHaveTextContent("Todas as empresas autorizadas");
  });

  it("returns selected authorized company IDs and can restore all", async () => {
    const user = userEvent.setup();
    const { onChange } = renderFilter();
    await user.click(
      screen.getByRole("combobox", { name: "Empresas autorizadas" }),
    );
    await user.click(screen.getByRole("option", { name: /Alfa/ }));

    expect(onChange).toHaveBeenLastCalledWith(["alfa-id"]);

    await user.keyboard("{Escape}");
    await user.click(
      screen.getByRole("combobox", { name: "Empresas autorizadas" }),
    );
    await user.click(
      screen.getByRole("option", { name: "Todas as empresas autorizadas" }),
    );

    expect(onChange).toHaveBeenLastCalledWith([]);
  });

  it("exposes a disabled accessible combobox", () => {
    render(
      <VinxThemeProvider>
        <AuthorizedCompanyFilter
          companies={companies}
          disabled
          label="Filtro de empresas"
          onChange={vi.fn()}
          selectedCompanyIds={[]}
        />
      </VinxThemeProvider>,
    );

    expect(
      screen.getByRole("combobox", { name: "Filtro de empresas" }),
    ).toHaveAttribute("aria-disabled", "true");
  });
});
