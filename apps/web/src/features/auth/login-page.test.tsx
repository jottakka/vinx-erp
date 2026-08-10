import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { LoginPage } from "./login-page";
import { createMockSessionGateway } from "./mock-session-gateway";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";
import { SessionProvider } from "./session-provider";

function PrototypePage({ onMount }: { onMount?: () => void }) {
  onMount?.();
  return <h1>Protótipo autenticado</h1>;
}

function renderLogin(
  gateway: MockSessionGateway,
  onProtectedMount?: () => void,
) {
  const router = createMemoryRouter(
    [
      { element: <LoginPage />, path: "/login" },
      {
        element: <PrototypePage onMount={onProtectedMount} />,
        path: "/protected",
      },
    ],
    { initialEntries: ["/login"] },
  );

  return render(
    <SessionProvider gateway={gateway}>
      <RouterProvider router={router} />
    </SessionProvider>,
  );
}

describe("LoginPage", () => {
  it("shows an actionable development-profile error and keeps the route public", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await user.clear(await screen.findByLabelText("Perfil de desenvolvimento"));
    await user.type(
      screen.getByLabelText("Perfil de desenvolvimento"),
      "missing-profile",
    );
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByText("Perfil de desenvolvimento indisponível"),
    ).toBeVisible();
    expect(screen.queryByText("Protótipo autenticado")).not.toBeInTheDocument();
  });

  it("opens the protected placeholder after a successful prototype sign-in", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await user.click(await screen.findByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Protótipo autenticado" }),
    ).toBeVisible();
  });

  it("prevents repeated sign-in from creating duplicate protected navigation", async () => {
    const user = userEvent.setup();
    const protectedMount = vi.fn();
    let resolveLogin!: (snapshot: BootstrapSnapshot) => void;
    const login = vi.fn(
      () =>
        new Promise<BootstrapSnapshot>((resolve) => {
          resolveLogin = resolve;
        }),
    );
    const gateway: MockSessionGateway = {
      getBootstrap: async () => null,
      login,
      logout: async () => {},
    };

    renderLogin(gateway, protectedMount);

    const enterButton = await screen.findByRole("button", { name: "Entrar" });
    await user.click(enterButton);
    await user.click(enterButton);

    expect(login).toHaveBeenCalledTimes(1);
    expect(protectedMount).not.toHaveBeenCalled();

    resolveLogin({
      companies: [],
      flags: { prototypeSession: true },
      user: { id: "maria", name: "Maria" },
    });

    await screen.findByRole("heading", { name: "Protótipo autenticado" });
    expect(protectedMount).toHaveBeenCalledTimes(1);
  });
});
