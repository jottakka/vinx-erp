import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { LoginPage } from "./login-page";
import { createMockSessionGateway } from "./mock-session-gateway";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";
import { SessionProvider } from "./session-provider";

function LegacyProtectedPage() {
  return <h1>Área protegida legada</h1>;
}

function HomePage() {
  return <h1>Início</h1>;
}

function HelpPage() {
  return <h1>Ajuda</h1>;
}

function createLoginRouter(
  initialEntries: Array<string | { pathname: string; state?: unknown }> = [
    "/login",
  ],
) {
  return createMemoryRouter(
    [
      { element: <HomePage />, path: "/" },
      { element: <LoginPage />, path: "/login" },
      { element: <HelpPage />, path: "/ajuda" },
      {
        element: <LegacyProtectedPage />,
        path: "/protected",
      },
    ],
    { initialEntries },
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

function runtimeCssFor(element: Element) {
  const selectors = Array.from(
    element.classList,
    (className) => `.${className}`,
  );

  return Array.from(document.styleSheets)
    .flatMap((styleSheet) => Array.from(styleSheet.cssRules))
    .map((rule) => rule.cssText)
    .filter((rule) => selectors.some((selector) => rule.includes(selector)))
    .join("\n");
}

describe("LoginPage", () => {
  it("composes the responsive public development-login surface", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    expect(
      await screen.findByRole("heading", {
        name: "Entrada do ambiente de desenvolvimento",
      }),
    ).toBeVisible();
    expect(
      screen.getByText(
        "Use o perfil de desenvolvimento autorizado para validar a interface.",
      ),
    ).toBeVisible();
    expect(
      screen.getByText(
        "Entrada interna do protótipo. Não é autenticação de produção.",
      ),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Abrir preferências visuais" }),
    ).toBeVisible();
    expect(screen.getByLabelText("Perfil de desenvolvimento")).toBeVisible();
    expect(screen.getAllByRole("main")).toHaveLength(1);
    expect(screen.getByRole("main")).toBeVisible();

    const profileField = screen.getByLabelText("Perfil de desenvolvimento");
    await user.clear(profileField);
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Informe um perfil",
    );
    expect(profileField).toHaveFocus();
  });

  it("rejects a whitespace-only profile before calling the mock gateway", async () => {
    const user = userEvent.setup();
    const gateway = createMockSessionGateway();
    const login = vi.spyOn(gateway, "login");
    renderLogin(gateway);

    const profileField = await screen.findByLabelText(
      "Perfil de desenvolvimento",
    );
    await user.clear(profileField);
    await user.type(profileField, "   ");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Informe um perfil",
    );
    expect(login).not.toHaveBeenCalled();
    expect(profileField).toHaveFocus();
    expect(profileField).toHaveAttribute(
      "aria-describedby",
      "development-profile-error",
    );
  });

  it("exposes the accessible visual preferences menu", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await user.click(
      screen.getByRole("button", { name: "Abrir preferências visuais" }),
    );

    expect(
      screen.getByRole("menuitemradio", { name: "Tema: claro" }),
    ).toBeVisible();
    expect(
      screen.getByRole("menuitemradio", { name: "Tema: escuro" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("menuitemradio", { name: "Tema: sistema" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("menuitemradio", { name: "Densidade: confortável" }),
    ).toBeVisible();
  });

  it("applies the selected density to the public layout padding", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    const main = screen.getByRole("main");
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled();
    });
    expect(runtimeCssFor(main)).toContain("padding:");

    await user.click(
      screen.getByRole("button", { name: "Abrir preferências visuais" }),
    );
    await user.click(
      screen.getByRole("menuitemradio", { name: "Densidade: compacta" }),
    );

    await waitFor(() => {
      expect(runtimeCssFor(main)).toContain("padding: 12px");
    });
  });

  it("shows an actionable development-profile error and keeps the route public", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    const profileField = await screen.findByLabelText(
      "Perfil de desenvolvimento",
    );
    await user.clear(profileField);
    await user.type(profileField, "missing-profile");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Perfil de desenvolvimento indisponível",
    );
    expect(profileField).toHaveFocus();
    expect(profileField).toHaveAttribute(
      "aria-describedby",
      "development-profile-error",
    );
    expect(screen.queryByText("Área protegida legada")).not.toBeInTheDocument();
  });

  it("trims the development profile before calling the mock gateway", async () => {
    const user = userEvent.setup();
    const gateway = createMockSessionGateway();
    const login = vi.spyOn(gateway, "login");
    renderLogin(gateway);

    const profileField = await screen.findByLabelText(
      "Perfil de desenvolvimento",
    );
    await user.clear(profileField);
    await user.type(profileField, "  maria  ");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    await waitFor(() => expect(login).toHaveBeenCalledWith("maria"));
  });

  it("associates a gateway failure to the profile field and returns focus", async () => {
    const user = userEvent.setup();
    const gateway: MockSessionGateway = {
      getBootstrap: async () => null,
      login: vi
        .fn()
        .mockRejectedValue(new Error("Falha temporária de entrada")),
      logout: async () => {},
    };
    renderLogin(gateway);

    const profileField = await screen.findByLabelText(
      "Perfil de desenvolvimento",
    );
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Falha temporária de entrada",
    );
    expect(profileField).toHaveFocus();
    expect(profileField).toHaveAttribute(
      "aria-describedby",
      "development-profile-error",
    );
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

  it("returns Maria to a known internal help path after sign-in", async () => {
    const user = userEvent.setup();
    renderLogin(
      createMockSessionGateway(),
      createLoginRouter([
        { pathname: "/login", state: { returnPath: "/ajuda" } },
      ]),
    );

    await user.click(await screen.findByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("heading", { name: "Ajuda" })).toBeVisible();
  });

  it("rejects an unknown return path after sign-in", async () => {
    const user = userEvent.setup();
    renderLogin(
      createMockSessionGateway(),
      createLoginRouter([
        {
          pathname: "/login",
          state: { returnPath: "https://outside.example" },
        },
      ]),
    );

    await user.click(await screen.findByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
  });

  it("opens the default authenticated destination after a successful prototype sign-in", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await user.click(await screen.findByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
  });

  it("transitions to the default authenticated route exactly once after deferred repeated sign-in", async () => {
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
    const homeTransitions = vi.fn();
    const navigate = vi.spyOn(router, "navigate");
    const unsubscribe = router.subscribe((state) => {
      if (state.location.pathname === "/") {
        homeTransitions();
      }
    });

    renderLogin(gateway, router);

    const enterButton = await screen.findByRole("button", { name: "Entrar" });
    await waitFor(() => expect(enterButton).toBeEnabled());
    await user.click(enterButton);
    expect(enterButton).toBeDisabled();
    expect(enterButton).toHaveTextContent("Entrando…");
    fireEvent.click(enterButton);

    expect(login).toHaveBeenCalledTimes(1);
    expect(navigate).not.toHaveBeenCalledWith("/", { replace: true });
    expect(homeTransitions).not.toHaveBeenCalled();

    resolveLogin({
      companies: [],
      flags: {},
      platform: { isPlatformOperator: false },
      user: { id: "maria", displayName: "Maria" },
    });

    await waitFor(() => {
      expect(router.state.location.pathname).toBe("/");
    });
    expect(
      navigate.mock.calls.filter(
        ([destination, options]) =>
          destination === "/" && options?.replace === true,
      ),
    ).toHaveLength(1);
    expect(homeTransitions).toHaveBeenCalledTimes(1);
    unsubscribe();
  });
});
