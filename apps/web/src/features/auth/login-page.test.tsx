import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { LoginPage } from "./login-page";
import { createMockSessionGateway } from "./mock-session-gateway";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";
import { SessionProvider } from "./session-provider";

function PrototypePage() {
  return <h1>Protótipo autenticado</h1>;
}

function createLoginRouter() {
  return createMemoryRouter(
    [
      { element: <LoginPage />, path: "/login" },
      {
        element: <PrototypePage />,
        path: "/protected",
      },
    ],
    { initialEntries: ["/login"] },
  );
}

function renderLogin(
  gateway: MockSessionGateway,
  router = createLoginRouter(),
) {
  const rendered = render(
    <VinxThemeProvider>
      <SessionProvider gateway={gateway}>
        <RouterProvider router={router} />
      </SessionProvider>
    </VinxThemeProvider>,
  );

  return { ...rendered, router };
}

describe("LoginPage", () => {
  it("exposes the accessible visual preferences menu", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await user.click(
      screen.getByRole("button", { name: "Abrir preferências visuais" }),
    );

    expect(
      screen.getByRole("menuitem", { name: "Tema: sistema" }),
    ).toBeVisible();
    expect(
      screen.getByRole("menuitem", { name: "Densidade: confortável" }),
    ).toBeVisible();
  });

  it("shows an actionable development-profile error and keeps the route public", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await user.clear(await screen.findByLabelText("Perfil de desenvolvimento"));
    await user.type(
      screen.getByLabelText("Perfil de desenvolvimento"),
      "missing-profile",
    );
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Perfil de desenvolvimento indisponível",
    );
    expect(screen.queryByText("Protótipo autenticado")).not.toBeInTheDocument();
  });

  it("keeps the labelled field, action and failure alert usable at a mobile viewport", async () => {
    const user = userEvent.setup();
    Object.defineProperty(window, "innerWidth", {
      configurable: true,
      value: 320,
    });
    renderLogin(createMockSessionGateway());

    const profileField = await screen.findByLabelText(
      "Perfil de desenvolvimento",
    );
    expect(profileField).toBeVisible();
    expect(screen.getByRole("button", { name: "Entrar" })).toBeVisible();

    await user.clear(profileField);
    await user.type(profileField, "missing");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toBeVisible();
  });

  it("opens the protected placeholder after a successful prototype sign-in", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await user.click(await screen.findByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Protótipo autenticado" }),
    ).toBeVisible();
  });

  it("transitions to the protected route exactly once after deferred repeated sign-in", async () => {
    const user = userEvent.setup();
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
    const router = createLoginRouter();
    const protectedTransitions = vi.fn();
    const navigate = vi.spyOn(router, "navigate");
    const unsubscribe = router.subscribe((state) => {
      if (state.location.pathname === "/protected") {
        protectedTransitions();
      }
    });

    renderLogin(gateway, router);

    const enterButton = await screen.findByRole("button", { name: "Entrar" });
    await waitFor(() => expect(enterButton).toBeEnabled());
    await user.click(enterButton);
    expect(enterButton).toBeDisabled();
    fireEvent.click(enterButton);

    expect(login).toHaveBeenCalledTimes(1);
    expect(navigate).not.toHaveBeenCalledWith("/protected", { replace: true });
    expect(protectedTransitions).not.toHaveBeenCalled();

    resolveLogin({
      companies: [],
      flags: { prototypeSession: true },
      user: { id: "maria", name: "Maria" },
    });

    await waitFor(() => {
      expect(router.state.location.pathname).toBe("/protected");
    });
    expect(
      navigate.mock.calls.filter(
        ([destination, options]) =>
          destination === "/protected" && options?.replace === true,
      ),
    ).toHaveLength(1);
    expect(protectedTransitions).toHaveBeenCalledTimes(1);
    unsubscribe();
  });
});
