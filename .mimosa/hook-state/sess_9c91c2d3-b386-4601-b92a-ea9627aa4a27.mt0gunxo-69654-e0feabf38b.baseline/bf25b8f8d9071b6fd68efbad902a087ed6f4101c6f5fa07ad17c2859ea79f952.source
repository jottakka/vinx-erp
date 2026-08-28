import { render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { LoginPage } from "../features/auth/login-page";
import { createMockSessionGateway } from "../features/auth/mock-session-gateway";
import type { MockSessionGateway } from "../features/auth/session-contract";
import { SessionProvider } from "../features/auth/session-provider";
import { ProtectedRoute } from "./protected-route";
import { VinxThemeProvider } from "./theme/vinx-theme-provider";

function ProtectedContent() {
  return <h1>Conteúdo protegido</h1>;
}

function renderProtectedRoute(initialPath: string) {
  const router = createMemoryRouter(
    [
      { element: <LoginPage />, path: "/login" },
      {
        element: (
          <ProtectedRoute>
            <ProtectedContent />
          </ProtectedRoute>
        ),
        path: "/protected",
      },
    ],
    { initialEntries: [initialPath] },
  );

  return render(
    <VinxThemeProvider>
      <SessionProvider gateway={createMockSessionGateway()}>
        <RouterProvider router={router} />
      </SessionProvider>
    </VinxThemeProvider>,
  );
}

describe("ProtectedRoute", () => {
  it("redirects an anonymous deep link to the login route", async () => {
    renderProtectedRoute("/protected");

    expect(
      await screen.findByRole("heading", {
        name: "Entrar",
      }),
    ).toBeVisible();
    expect(screen.queryByText("Conteúdo protegido")).not.toBeInTheDocument();
  });

  it("shows an accessible loading state while protected bootstrap is pending", () => {
    const gateway: MockSessionGateway = {
      getBootstrap: () => new Promise(() => {}),
      login: async () => {
        throw new Error("not used");
      },
      logout: async () => {},
    };
    const router = createMemoryRouter(
      [
        {
          element: (
            <ProtectedRoute>
              <ProtectedContent />
            </ProtectedRoute>
          ),
          path: "/protected",
        },
      ],
      { initialEntries: ["/protected"] },
    );

    render(
      <VinxThemeProvider>
        <SessionProvider gateway={gateway}>
          <RouterProvider router={router} />
        </SessionProvider>
      </VinxThemeProvider>,
    );

    expect(screen.getByRole("status")).toHaveTextContent(
      "Carregando aplicativo",
    );
    expect(screen.queryByText("Conteúdo protegido")).not.toBeInTheDocument();
  });

  it("keeps protected content blocked while bootstrap revalidation is unavailable", async () => {
    const gateway: MockSessionGateway = {
      getBootstrap: async () => {
        throw new Error("internal diagnostic must not be rendered");
      },
      login: async () => {
        throw new Error("not used");
      },
      logout: async () => {},
    };
    const router = createMemoryRouter(
      [
        { element: <LoginPage />, path: "/login" },
        {
          element: (
            <ProtectedRoute>
              <ProtectedContent />
            </ProtectedRoute>
          ),
          path: "/protected",
        },
      ],
      { initialEntries: ["/protected"] },
    );

    render(
      <VinxThemeProvider>
        <SessionProvider gateway={gateway}>
          <RouterProvider router={router} />
        </SessionProvider>
      </VinxThemeProvider>,
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Não foi possível validar sua sessão. Tente novamente.",
    );
    expect(screen.queryByText("Conteúdo protegido")).not.toBeInTheDocument();
    expect(screen.queryByText("internal diagnostic")).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/protected");
  });
});
