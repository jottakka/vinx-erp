import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { LoginPage } from "../features/auth/login-page";
import { createMockSessionGateway } from "../features/auth/mock-session-gateway";
import { SessionProvider } from "../features/auth/session-provider";
import { ProtectedRoute } from "./protected-route";
import { PrototypeEntryPage } from "./prototype-entry-page";

function renderPrototypeRoute(initialPath: string) {
  const router = createMemoryRouter(
    [
      { element: <LoginPage />, path: "/login" },
      {
        element: (
          <ProtectedRoute>
            <PrototypeEntryPage />
          </ProtectedRoute>
        ),
        path: "/protected",
      },
    ],
    { initialEntries: [initialPath] },
  );

  return render(
    <SessionProvider gateway={createMockSessionGateway()}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );
}

describe("ProtectedRoute", () => {
  it("redirects an anonymous deep link to the login route", async () => {
    renderPrototypeRoute("/protected");

    expect(
      await screen.findByRole("heading", { name: "Entrar" }),
    ).toBeVisible();
    expect(screen.queryByText("Protótipo autenticado")).not.toBeInTheDocument();
  });

  it("redirects to login after logout", async () => {
    const user = userEvent.setup();
    renderPrototypeRoute("/login");

    await user.click(await screen.findByRole("button", { name: "Entrar" }));
    await user.click(
      await screen.findByRole("button", { name: "Sair do protótipo" }),
    );

    expect(
      await screen.findByRole("heading", { name: "Entrar" }),
    ).toBeVisible();
    expect(screen.queryByText("Protótipo autenticado")).not.toBeInTheDocument();
  });
});
