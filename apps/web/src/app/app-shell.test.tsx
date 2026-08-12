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
import { AppShell } from "./app-shell";
import { HelpPage } from "./help-page";
import { HomePage } from "./home-page";
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
        children: [
          { element: <HomePage />, index: true },
          { element: <HelpPage />, path: "ajuda" },
        ],
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
        companies: [{ capabilities: [], id: "delta", name: "Delta" }],
        flags: { prototypeSession: true },
        user: { id: "maria", name: "Maria" },
      }),
    );
    expect(
      await screen.findByRole("group", { name: "Empresa Delta" }),
    ).toBeVisible();
    expect(futureCompanyRouter.state.location.pathname).toBe("/");
  });

  it("shows an honest no-authorized-company state with a working help route", async () => {
    const router = renderShell(
      "/",
      gatewayFor({
        companies: [],
        flags: { prototypeSession: true },
        user: { id: "maria", name: "Maria" },
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

  it("keeps the authenticated shell and announces a recoverable logout failure", async () => {
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
    expect(screen.getByRole("alert")).toHaveAttribute("aria-live", "assertive");
    expect(screen.getByRole("heading", { name: "Início" })).toBeVisible();
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
    unsubscribe();
  });
});
