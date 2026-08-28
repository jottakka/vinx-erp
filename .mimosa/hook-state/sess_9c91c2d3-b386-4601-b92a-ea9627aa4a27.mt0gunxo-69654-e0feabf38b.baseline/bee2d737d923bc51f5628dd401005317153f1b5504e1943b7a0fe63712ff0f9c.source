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

const TEST_CREDENTIALS = {
  email: "maria@example.test",
  password: "test-password",
} as const;

async function fillCredentials(
  user: ReturnType<typeof userEvent.setup>,
  credentials: { email: string; password: string } = TEST_CREDENTIALS,
) {
  await user.type(screen.getByLabelText("E-mail"), credentials.email);
  await user.type(screen.getByLabelText("Senha"), credentials.password);
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
  it("composes the responsive public login surface", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    expect(
      await screen.findByRole("heading", {
        name: "Entrar",
      }),
    ).toBeVisible();
    expect(
      screen.getByText("Informe suas credenciais para acessar o aplicativo."),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Abrir preferências visuais" }),
    ).toBeVisible();
    expect(screen.getByLabelText("E-mail")).toBeVisible();
    expect(screen.getByLabelText("Senha")).toBeVisible();
    expect(screen.getAllByRole("main")).toHaveLength(1);
    expect(screen.getByRole("main")).toBeVisible();

    const emailField = screen.getByLabelText("E-mail");
    await user.clear(emailField);
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Informe seu e-mail",
    );
    expect(emailField).toHaveFocus();
  });

  it("rejects a whitespace-only email before calling the mock gateway", async () => {
    const user = userEvent.setup();
    const gateway = createMockSessionGateway();
    const login = vi.spyOn(gateway, "login");
    renderLogin(gateway);

    const emailField = await screen.findByLabelText("E-mail");
    await user.clear(emailField);
    await user.type(emailField, "   ");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Informe seu e-mail",
    );
    expect(login).not.toHaveBeenCalled();
    expect(emailField).toHaveFocus();
    expect(emailField).toHaveAttribute("aria-describedby", "login-error");
  });

  it("requires a password and focuses it before calling the mock gateway", async () => {
    const user = userEvent.setup();
    const gateway = createMockSessionGateway();
    const login = vi.spyOn(gateway, "login");
    renderLogin(gateway);

    await user.type(screen.getByLabelText("E-mail"), TEST_CREDENTIALS.email);
    const passwordField = screen.getByLabelText("Senha");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Informe sua senha",
    );
    expect(passwordField).toHaveFocus();
    expect(login).not.toHaveBeenCalled();
  });

  it("uses browser credential autocomplete and clears the password after an attempt", async () => {
    const user = userEvent.setup();
    const gateway = createMockSessionGateway();
    renderLogin(gateway);

    const emailField = screen.getByLabelText("E-mail");
    const passwordField = screen.getByLabelText("Senha");
    expect(emailField).toHaveAttribute("autocomplete", "email");
    expect(passwordField).toHaveAttribute("autocomplete", "current-password");
    await fillCredentials(user, {
      email: "missing@example.test",
      password: TEST_CREDENTIALS.password,
    });
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Não foi possível entrar. Verifique suas credenciais e tente novamente.",
    );
    expect(
      screen.queryByText("Perfil de desenvolvimento indisponível"),
    ).not.toBeInTheDocument();
    expect(passwordField).toHaveValue("");
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

  it("shows a safe credential error and keeps the route public", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    const emailField = await screen.findByLabelText("E-mail");
    await user.type(emailField, "missing@example.test");
    await user.type(screen.getByLabelText("Senha"), TEST_CREDENTIALS.password);
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Não foi possível entrar. Verifique suas credenciais e tente novamente.",
    );
    expect(emailField).toHaveAttribute("aria-describedby", "login-error");
    expect(
      screen.queryByText("Perfil de desenvolvimento indisponível"),
    ).not.toBeInTheDocument();
  });

  it("trims the email before calling the mock gateway", async () => {
    const user = userEvent.setup();
    const gateway = createMockSessionGateway();
    const login = vi.spyOn(gateway, "login");
    renderLogin(gateway);

    await user.type(screen.getByLabelText("E-mail"), "  maria@example.test  ");
    await user.type(screen.getByLabelText("Senha"), TEST_CREDENTIALS.password);
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    await waitFor(() =>
      expect(login).toHaveBeenCalledWith("maria@example.test"),
    );
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

    const emailField = await screen.findByLabelText("E-mail");
    await user.type(emailField, TEST_CREDENTIALS.email);
    await user.type(screen.getByLabelText("Senha"), TEST_CREDENTIALS.password);
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Não foi possível entrar. Verifique suas credenciais e tente novamente.",
    );
    expect(emailField).toHaveAttribute("aria-describedby", "login-error");
  });

  it("keeps the labelled field, action and failure alert usable at a mobile viewport", async () => {
    const user = userEvent.setup();
    Object.defineProperty(window, "innerWidth", {
      configurable: true,
      value: 320,
    });
    renderLogin(createMockSessionGateway());

    const emailField = await screen.findByLabelText("E-mail");
    expect(emailField).toBeVisible();
    expect(screen.getByLabelText("Senha")).toBeVisible();
    expect(screen.getByRole("button", { name: "Entrar" })).toBeVisible();

    await user.type(emailField, "missing@example.test");
    await user.type(screen.getByLabelText("Senha"), TEST_CREDENTIALS.password);
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

    await fillCredentials(user);
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

    await fillCredentials(user);
    await user.click(await screen.findByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
  });

  it("opens the default authenticated destination after a successful prototype sign-in", async () => {
    const user = userEvent.setup();
    renderLogin(createMockSessionGateway());

    await fillCredentials(user);
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
    await fillCredentials(user);
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
