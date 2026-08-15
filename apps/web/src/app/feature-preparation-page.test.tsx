import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { FeaturePreparationPage } from "./feature-preparation-page";
import { VinxThemeProvider } from "./theme/vinx-theme-provider";

function renderPreparationPage(feature: string) {
  const router = createMemoryRouter(
    [
      {
        element: <FeaturePreparationPage feature={feature} scope="Escopo." />,
        path: "/paguai",
      },
      { element: <h1>Início</h1>, path: "/" },
    ],
    { initialEntries: ["/paguai"] },
  );

  render(
    <VinxThemeProvider>
      <RouterProvider router={router} />
    </VinxThemeProvider>,
  );

  return router;
}

describe("FeaturePreparationPage", () => {
  it("labels an unfinished module honestly and returns to Home", async () => {
    const user = userEvent.setup();
    const router = renderPreparationPage("Paguai");

    expect(screen.getByRole("heading", { name: "Paguai" })).toBeVisible();
    expect(screen.getByText("Módulo em preparação")).toBeVisible();
    expect(
      screen.queryByRole("button", { name: /criar|aprovar|pagar/i }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole("link", { name: "Voltar ao Início" }));
    expect(router.state.location.pathname).toBe("/");
  });
});
