import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { createMockSessionGateway } from "../features/auth/mock-session-gateway";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../features/auth/session-contract";
import { SessionProvider } from "../features/auth/session-provider";
import { appRouteManifest } from "./app-route-manifest";
import { AppShell } from "./app-shell";
import { ProtectedRoute } from "./protected-route";
import { VinxThemeProvider } from "./theme/vinx-theme-provider";

async function signedInGateway(options = {}) {
  const gateway = createMockSessionGateway(options);
  await gateway.login("maria");
  return gateway;
}

function gatewayFor(snapshot: BootstrapSnapshot): MockSessionGateway {
  return {
    getBootstrap: async () => snapshot,
    login: async () => snapshot,
    logout: async () => {},
  };
}

function renderShell(path: string, gateway: MockSessionGateway) {
  const router = createMemoryRouter(
    [
      {
        element: (
          <ProtectedRoute>
            <AppShell />
          </ProtectedRoute>
        ),
        path: "/",
        children: appRouteManifest.map(({ element, path }) =>
          path === "/"
            ? { element, index: true }
            : { element, path: path.slice(1) },
        ),
      },
    ],
    { initialEntries: [path] },
  );

  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gateway}>
        <RouterProvider router={router} />
      </SessionProvider>
    </VinxThemeProvider>,
  );

  return router;
}

describe("AppShell", () => {
  it("welcomes Maria on an all-authorized home without technical capability text", async () => {
    const router = renderShell("/", await signedInGateway());

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
    expect(screen.getByText("Olá, Maria")).toBeVisible();
    expect(screen.getByText("Todas as empresas autorizadas")).toBeVisible();
    expect(screen.getByRole("group", { name: "Empresa Alfa" })).toBeVisible();
    expect(screen.getByRole("group", { name: "Empresa Beta" })).toBeVisible();
    expect(screen.queryByText("Gama", { exact: true })).not.toBeInTheDocument();
    expect(screen.queryByText("prototype.view")).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");

    const futureCompanyRouter = renderShell(
      "/",
      gatewayFor({
        companies: [
          {
            capabilities: [],
            companyId: "company-delta",
            companyName: "Delta",
            membershipId: "membership-delta",
          },
        ],
        flags: {},
        platform: { isPlatformOperator: false },
        user: { id: "maria", displayName: "Maria" },
      }),
    );
    expect(
      await screen.findByRole("group", { name: "Empresa Delta" }),
    ).toBeVisible();
    expect(futureCompanyRouter.state.location.pathname).toBe("/");
  });

  it("keeps Home free of fake operational metrics", async () => {
    renderShell("/", await signedInGateway());

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
    expect(
      screen.queryByText(/pendências hoje|atividade da equipe/i),
    ).not.toBeInTheDocument();
  });

  it("renders all concrete navigation group labels", async () => {
    renderShell("/", await signedInGateway());

    await screen.findByRole("heading", { name: "Início" });
    const navigation = screen.getByRole("navigation", {
      name: "Navegação lateral",
    });
    for (const label of ["Espaço de trabalho", "Módulos", "Suporte"]) {
      expect(
        within(navigation).getByText(label).closest("li"),
      ).toBeInTheDocument();
    }
  });

  it("identifies the signed-in user in the header", async () => {
    renderShell("/", await signedInGateway());

    await screen.findByRole("heading", { name: "Início" });
    expect(screen.getByLabelText("Sessão ativa: Maria")).toBeInTheDocument();
    expect(screen.getByText("M")).toBeInTheDocument();
  });

  it("collapses and expands the desktop navigation without losing access", async () => {
    const user = userEvent.setup();
    const router = renderShell("/", await signedInGateway());
    await screen.findByRole("heading", { name: "Início" });

    const collapseButton = screen.getByRole("button", {
      name: "Recolher navegação lateral",
    });
    await user.click(collapseButton);

    const navigation = screen.getByRole("navigation", {
      name: "Navegação lateral",
    });
    expect(
      within(navigation).getByRole("link", { name: "Ajuda" }),
    ).toBeInTheDocument();
    expect(within(navigation).queryByText("Suporte")).not.toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: "Expandir navegação lateral" }),
    );
    expect(within(navigation).getByText("Suporte")).toBeInTheDocument();

    await user.click(within(navigation).getByRole("link", { name: "Ajuda" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/ajuda"));
  });

  it("navigates from a module card to the real Paguai workspace", async () => {
    const user = userEvent.setup();
    const router = renderShell("/", await signedInGateway());
    await screen.findByRole("heading", { name: "Início" });
    const moduleSection = screen
      .getByRole("heading", { name: "Estrutura em evolução" })
      .closest("section");

    expect(moduleSection).not.toBeNull();
    if (!moduleSection) {
      throw new Error("Module section not found");
    }

    expect(
      await within(moduleSection).findByRole("link", {
        name: "Paguai Módulo em preparação",
      }),
    ).toBeVisible();
    expect(
      within(moduleSection).getByRole("link", { name: /Atendimento/ }),
    ).toBeVisible();
    expect(
      within(moduleSection).getByRole("link", { name: /Kanban/ }),
    ).toBeVisible();
    expect(
      within(moduleSection).getByRole("link", { name: /Pessoas/ }),
    ).toBeVisible();
    expect(
      within(moduleSection).getByTestId("PaymentsOutlinedIcon"),
    ).toBeVisible();

    await user.click(
      within(moduleSection).getByRole("link", {
        name: "Paguai Módulo em preparação",
      }),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe("/paguai"));
    expect(
      await screen.findByRole("heading", { name: "Paguai" }),
    ).toBeVisible();
    expect(
      screen.getByText(
        "Rascunhos de solicitações financeiras por empresa. O MVP não cria cobrança ou pagamento.",
      ),
    ).toBeVisible();
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Nenhuma empresa tem acesso ao Paguai.",
    );
    expect(
      screen.queryByText(
        "Esta área ainda não possui dados ou ações operacionais.",
      ),
    ).not.toBeInTheDocument();
  });

  it("keeps product routes primary and exposes only the available Paguai prototype action in development", async () => {
    const router = renderShell("/", await signedInGateway());

    await screen.findByRole("heading", { name: "Início" });
    const moduleSection = screen
      .getByRole("heading", { name: "Estrutura em evolução" })
      .closest("section");

    expect(moduleSection).not.toBeNull();
    if (!moduleSection) {
      throw new Error("Module section not found");
    }

    const paguaiCard = within(moduleSection)
      .getByRole("link", { name: "Paguai Módulo em preparação" })
      .closest("li");
    expect(paguaiCard).not.toBeNull();
    if (!paguaiCard) {
      throw new Error("Paguai card not found");
    }

    expect(
      within(paguaiCard).getByRole("link", {
        name: "Paguai Módulo em preparação",
      }),
    ).toHaveAttribute("href", "/paguai");
    expect(
      within(paguaiCard).getByRole("link", {
        name: "Abrir protótipo Paguai",
      }),
    ).toHaveAttribute("href", "/prototipos/paguai");

    for (const label of ["Kanban", "Atendimento", "Pessoas"]) {
      const card = within(moduleSection)
        .getByRole("link", { name: `${label} Módulo em preparação` })
        .closest("li");
      expect(card).not.toBeNull();
      if (!card) {
        throw new Error(`${label} card not found`);
      }
      expect(
        within(card).queryByRole("link", { name: /Abrir protótipo/ }),
      ).not.toBeInTheDocument();
    }

    expect(router.state.location.pathname).toBe("/");
  });

  it("shows an honest no-authorized-company state with a working help route", async () => {
    const router = renderShell(
      "/",
      gatewayFor({
        companies: [],
        flags: {},
        platform: { isPlatformOperator: false },
        user: { id: "maria", displayName: "Maria" },
      }),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Nenhuma empresa autorizada",
    );
    await userEvent
      .setup()
      .click(screen.getByRole("link", { name: "Consultar orientações" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/ajuda"));
    expect(await screen.findByRole("heading", { name: "Ajuda" })).toBeVisible();
  });

  it("blocks the authenticated shell and offers a logout retry after failure", async () => {
    const router = renderShell(
      "/",
      await signedInGateway({
        beforeLogout: async () => {
          throw new Error("fixture failure");
        },
      }),
    );

    await screen.findByRole("heading", { name: "Início" });
    await userEvent.setup().click(screen.getByRole("button", { name: "Sair" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Não foi possível sair do aplicativo. Tente novamente.",
    );
    expect(
      screen.getByRole("button", { name: "Tentar sair novamente" }),
    ).toBeVisible();
    expect(
      screen.queryByRole("heading", { name: "Início" }),
    ).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
  });

  it("opens, closes with Escape, restores mobile-trigger focus and navigates once", async () => {
    const user = userEvent.setup();
    const router = renderShell("/", await signedInGateway());
    const transitions = vi.fn();
    const unsubscribe = router.subscribe((state) => {
      if (state.location.pathname === "/ajuda") {
        transitions();
      }
    });
    const trigger = await screen.findByRole("button", {
      name: "Abrir navegação",
    });

    trigger.focus();
    await user.click(trigger);
    expect(trigger).toHaveAttribute("aria-expanded", "true");
    expect(trigger).toHaveAttribute("aria-controls", "vinx-mobile-navigation");
    await screen.findByRole("navigation", {
      hidden: true,
      name: "Navegação principal",
    });
    await user.keyboard("{Escape}");

    await waitFor(() =>
      expect(
        screen.queryByRole("navigation", {
          hidden: true,
          name: "Navegação principal",
        }),
      ).not.toBeInTheDocument(),
    );
    expect(trigger).toHaveFocus();
    expect(trigger).toHaveAttribute("aria-expanded", "false");
    expect(trigger).not.toHaveAttribute("aria-controls");

    await user.click(trigger);
    const reopenedMobileNavigation = await screen.findByRole("navigation", {
      hidden: true,
      name: "Navegação principal",
    });
    await user.click(
      within(reopenedMobileNavigation).getByRole("link", {
        hidden: true,
        name: "Ajuda",
      }),
    );

    await waitFor(() => expect(router.state.location.pathname).toBe("/ajuda"));
    expect(transitions).toHaveBeenCalledTimes(1);
    expect(trigger).toHaveAttribute("aria-expanded", "false");
    expect(trigger).not.toHaveAttribute("aria-controls");
    unsubscribe();
  });
});
